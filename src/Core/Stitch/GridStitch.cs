using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenshinMap.Core.Vision;
using OpenCvSharp;
using static GenshinMap.Core.Vision.GridParts;

namespace GenshinMap.Core.Stitch;

/// <summary>
/// 자동 캡처 사진 전용 격자 붙이기. py: grid_stitch.py(main, trusted_layout, 합치기). 표준 출력 줄([match]·[live]·[seam]·[light]·[done] …)은
/// 앱이 읽어 진행을 보이므로 파이썬과 같은 꼴로 찍는다. 끈 위치 기록이 없는 옛 기록(version 1)은 옮기지 않는다(안_옮기는_목록).
/// </summary>
public static class GridStitch
{
    public static double PRIOR_TOL = 80.0;
    public const int TRUST_STRONG = 150;
    public const double PRIOR_WEIGHT = 0.05, GROUP_MATCH_VOTE = 0.5, DENSE_VOTE = 1.0, GUESSED_DRAG_VOTE = 0.25;
    public static readonly (int Lo, int Hi) POINTER_AREA = (500, 1600);
    public const int POINTER_RADIUS = 105, POINTER_TIGHT = 14;
    public const double SEAM_SCALE = 0.25;
    public const int LIGHT_BLOCK = 8;
    public const double LIGHT_FLAT = 6.0;
    public const int LIGHT_DARK = 20, LIGHT_MIN = 8, LIGHT_MIN_PHOTOS = 12;
    public const double PICTURE_MIN = 0.75, PICTURE_MIN_OVERLAP = 0.08;
    public const int UNPLACED_PER_PHOTO = 10, UNPLACED_PAIRS = 800, PAIR_BUDGET = 25, GROW_LIMIT = 3, COMPARE_SECONDS = 900;
    public const double SPREAD_LIMIT = 2.0, MAX_OUTPUT_PX = 600e6;

    /// <summary>py: POINTER_SOFT — 사진 이름 → (화살표로 뺀 곳, 메울 곳) 원래 크기 0/255</summary>
    public static readonly Dictionary<string, (Mat Hidden, Mat Tight)> POINTER_SOFT = new();

    static string F(double v, int d) => v.ToString("F" + d, CultureInfo.InvariantCulture);
    static void Say(string s) { Console.WriteLine(s); Console.Out.Flush(); }

    // ---------- 플레이어 화살표 ----------

    /// <summary>py: pointer_mask(image) — (뺄 곳, 메울 곳) 원래 크기 0/255 또는 null</summary>
    public static (Mat Hide, Mat Tight)? PointerMask(Mat image)
    {
        int h = image.Rows, w = image.Cols;
        double scale = w / 2560.0;
        using Mat hsv = new Mat();
        Cv2.CvtColor(image, hsv, ColorConversionCodes.BGR2HSV);
        hsv.GetArray(out Vec3b[] hv);
        var cy = new byte[h * w];
        for (int p = 0; p < cy.Length; p++)
            cy[p] = hv[p].Item0 >= 85 && hv[p].Item0 <= 100 && hv[p].Item1 > 200 && hv[p].Item2 > 220 ? (byte)1 : (byte)0;
        using var cyan = Mat.FromPixelData(h, w, MatType.CV_8UC1, cy);
        using Mat labels = new Mat(), stats = new Mat(), cents = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(cyan, labels, stats, cents, PixelConnectivity.Connectivity8);
        double lo = POINTER_AREA.Lo * scale * scale, hi = POINTER_AREA.Hi * scale * scale;
        var hide = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        var tight = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        labels.GetArray(out int[] lab);
        var tb = new byte[h * w];
        for (int i = 1; i < n; i++)
        {
            int area = stats.At<int>(i, 4);
            if (lo <= area && area <= hi)
            {
                double cx = cents.At<double>(i, 0), cyy = cents.At<double>(i, 1);
                Cv2.Circle(hide, new Point(Py.RoundEvenInt(cx), Py.RoundEvenInt(cyy)), Py.RoundEvenInt(POINTER_RADIUS * scale), Scalar.All(1), -1);
                for (int p = 0; p < lab.Length; p++) if (lab[p] == i) tb[p] = 1;
            }
        }
        if (Cv2.CountNonZero(hide) == 0) { hide.Dispose(); tight.Dispose(); return null; }
        tight.SetArray(tb);
        int k = Math.Max(3, Py.RoundEvenInt(POINTER_TIGHT * scale) * 2 + 1);
        using Mat el = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(k, k));
        using Mat td = new Mat();
        Cv2.Dilate(tight, td, el, null, 1, BorderTypes.Constant, Cv2.MorphologyDefaultBorderValue());
        var hideO = new Mat(); Cv2.Compare(hide, 0, hideO, CmpTypes.GT);
        var tightO = new Mat(); Cv2.Compare(td, 0, tightO, CmpTypes.GT);
        hide.Dispose(); tight.Dispose();
        return (hideO, tightO);
    }

    // ---------- 위치 풀기 ----------

    /// <summary>py: components(n, links) — 이어진 묶음(큰 것부터, 같으면 가장 작은 번호가 앞인 묶음부터)</summary>
    public static List<List<int>> Components(int n, IEnumerable<(int I, int J)> links)
    {
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (var (i, j) in links) parent[Find(j)] = Find(i);
        var groups = new List<(int Root, List<int> M)>();
        var at = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!at.TryGetValue(r, out int gi)) { gi = groups.Count; at[r] = gi; groups.Add((r, new List<int>())); }
            groups[gi].M.Add(i);
        }
        return groups.Select(g => g.M).OrderByDescending(m => m.Count).ToList(); // 안정 정렬(py sorted reverse도 같은 것끼리 순서 유지)
    }

    /// <summary>py: solve(n, edges, start, anchor) — 위치[j] − 위치[i] = (ox, oy)를 무게 최소제곱으로(SVD, 최소 노름)</summary>
    public static double[,] Solve(int n, IReadOnlyList<(int I, int J, double Ox, double Oy, double W)> edges, double[,] start, IReadOnlyList<int> anchor)
    {
        int rows = edges.Count + anchor.Count;
        using var a = new Mat(rows, n, MatType.CV_64FC1, Scalar.All(0));
        using var bx = new Mat(rows, 1, MatType.CV_64FC1, Scalar.All(0));
        using var by = new Mat(rows, 1, MatType.CV_64FC1, Scalar.All(0));
        for (int k = 0; k < edges.Count; k++)
        {
            var (i, j, ox, oy, w) = edges[k];
            a.Set(k, j, w); a.Set(k, i, -w);
            bx.Set(k, 0, w * ox); by.Set(k, 0, w * oy);
        }
        for (int t = 0; t < anchor.Count; t++)
        {
            int k = edges.Count + t, i = anchor[t];
            a.Set(k, i, 1.0);
            bx.Set(k, 0, start[i, 0]); by.Set(k, 0, start[i, 1]);
        }
        using Mat x = new Mat(), y = new Mat();
        Cv2.Solve(a, bx, x, DecompTypes.SVD);
        Cv2.Solve(a, by, y, DecompTypes.SVD);
        var pos = new double[n, 2];
        for (int i = 0; i < n; i++) { pos[i, 0] = x.At<double>(i, 0); pos[i, 1] = y.At<double>(i, 0); }
        return pos;
    }

    static double Residual(double[,] pos, int i, int j, (double X, double Y) off)
        => Py.Hypot(pos[j, 0] - pos[i, 0] - off.X, pos[j, 1] - pos[i, 1] - off.Y);

    static HashSet<(int, int)> NearPairs(double[,] pos, (double X, double Y) step, IEnumerable<int> members)
    {
        var m = members.ToList();
        var s = new HashSet<(int, int)>();
        foreach (int i in m)
            foreach (int j in m)
                if (i < j && Math.Abs(pos[j, 0] - pos[i, 0]) <= NEIGHBOR * step.X && Math.Abs(pos[j, 1] - pos[i, 1]) <= NEIGHBOR * step.Y) s.Add((i, j));
        return s;
    }

    static bool BorderOnlyFarZero(PairMatch pm, (double X, double Y) expected, (double X, double Y) step)
        => Py.Hypot(-pm.Dx, -pm.Dy) < 40 && (Math.Abs(expected.X) > step.X / 2 || Math.Abs(expected.Y) > step.Y / 2) && !(pm.InteriorInliers is int v && v != 0);

    // ---------- 그림 비교(0 근처 먼 매칭) ----------

    /// <summary>py: picture_agreement(a, b, valid, off) — b를 off 방향으로 옮겨 a와 정규화 상관(±3px 안 최고)</summary>
    public static (double Score, int Samples, string? Reason) PictureAgreement(Mat a, Mat b, Mat valid, (double X, double Y) off)
    {
        int h = a.Rows, w = a.Cols;
        double? best = null; int samples = 0;
        int minimum = (int)Math.Ceiling(h * w * PICTURE_MIN_OVERLAP);
        a.GetArray(out float[] av);
        valid.GetArray(out byte[] vv);
        using Mat vf = new Mat();
        valid.ConvertTo(vf, MatType.CV_32F, 1.0 / 255);
        for (int ex = -3; ex <= 3; ex++)
            for (int ey = -3; ey <= 3; ey++)
            {
                float tx = (float)(off.X * UI_SCALE + ex), ty = (float)(off.Y * UI_SCALE + ey); // py: np.float32 행렬
                using var m = new Mat(2, 3, MatType.CV_32FC1);
                m.SetArray(new float[] { 1, 0, tx, 0, 1, ty });
                using Mat moved = new Mat(), wv = new Mat();
                Cv2.WarpAffine(b, moved, m, new Size(w, h), InterpolationFlags.Linear);
                Cv2.WarpAffine(vf, wv, m, new Size(w, h));
                moved.GetArray(out float[] mv);
                wv.GetArray(out float[] wvv);
                var xs = new List<float>(); var ys = new List<float>();
                for (int p = 0; p < av.Length; p++) if (vv[p] != 0 && wvv[p] > .99f) { xs.Add(av[p]); ys.Add(mv[p]); }
                if (xs.Count < minimum) continue;
                float mx = Py.MeanF32(xs.ToArray()), my = Py.MeanF32(ys.ToArray());
                float[] x = xs.Select(v => v - mx).ToArray(), y = ys.Select(v => v - my).ToArray();
                double den = Math.Sqrt((double)Py.PairwiseSumF32(x.Select(v => v * v).ToArray()) * Py.PairwiseSumF32(y.Select(v => v * v).ToArray()));
                if (den <= 1e-6) continue;
                double score = Py.PairwiseSumF32(x.Select((v, k) => v * y[k]).ToArray()) / den;
                if (best is null || score > best) { best = score; samples = xs.Count; }
            }
        return (best ?? 0.0, samples, best is null ? "겹침 또는 무늬 부족" : null);
    }

    /// <summary>py: make_picture_check(paths, ui) — 처음 쓸 때만 원사진을 0.25배로 줄이고 가운데 값을 뺀다</summary>
    public static Func<int, int, (double X, double Y), (double Score, int Samples, string? Reason)> MakePictureCheck(IReadOnlyList<string> paths, Mat? ui)
    {
        List<Mat>? frames = null; Mat? valid = null;
        return (i, j, off) =>
        {
            if (frames is null)
            {
                var smalls = new List<float[]>();
                int sh = 0, sw = 0;
                foreach (var path in paths)
                {
                    using Mat? gray = ImageIO.ImreadUnicode(path, ImreadModes.Grayscale) ?? throw new ArgumentException($"그림 비교용 사진 읽기 실패: {path}");
                    using Mat s = UiSmall(gray), f = new Mat();
                    s.ConvertTo(f, MatType.CV_32F);
                    f.GetArray(out float[] fv);
                    smalls.Add(fv); sh = f.Rows; sw = f.Cols;
                }
                int stepN = Math.Max(1, smalls.Count / 70);
                var pick = smalls.Where((_, k) => k % stepN == 0).ToList();
                var med = new float[sh * sw];
                var col = new float[pick.Count];
                for (int p = 0; p < med.Length; p++) { for (int k = 0; k < pick.Count; k++) col[k] = pick[k][p]; med[p] = Py.Median(col); }
                frames = new List<Mat>();
                foreach (var s in smalls)
                {
                    var d = new float[s.Length];
                    for (int p = 0; p < d.Length; p++) d[p] = s[p] - med[p];
                    using var dm = Mat.FromPixelData(sh, sw, MatType.CV_32FC1, d);
                    var bl = new Mat();
                    Cv2.GaussianBlur(dm, bl, new Size(0, 0), 1.5);
                    frames.Add(bl);
                }
                valid = new Mat(sh, sw, MatType.CV_8UC1, Scalar.All(0));
                int r0 = (int)(.04 * sh), r1 = (int)(.96 * sh), c0 = (int)(.04 * sw), c1 = (int)(.96 * sw);
                valid[new Rect(c0, r0, c1 - c0, r1 - r0)].SetTo(255);
                if (ui is not null)
                {
                    using Mat r = new Mat(), m = new Mat(), dil = new Mat(), k9 = Mat.Ones(9, 9, MatType.CV_8UC1);
                    Cv2.Resize(ui, r, new Size(sw, sh), 0, 0, InterpolationFlags.Nearest);
                    Cv2.Compare(r, 0, m, CmpTypes.GT);
                    Cv2.Dilate(m, dil, k9, null, 1, BorderTypes.Constant, Cv2.MorphologyDefaultBorderValue());
                    valid.SetTo(0, dil);
                }
            }
            return PictureAgreement(frames[i], frames[j], valid!, off);
        };
    }

    // ---------- 합치기 ----------

    /// <summary>py: screen_light(items, positions, step) — 화면 자리별로 '가운데 칸에서 찍은 값보다 더 밝은 양'(칸 크기, BGR float32) 또는 null</summary>
    public static Mat? ScreenLight(IReadOnlyList<ImageItem> items, IReadOnlyDictionary<int, (double X, double Y)> positions, (double X, double Y) step)
    {
        int n = items.Count;
        int h = items[0].Shape.H, w = items[0].Shape.W;
        int b = Math.Max(2, Py.RoundEvenInt(LIGHT_BLOCK * (double)w / 2560));
        int ny = h / b - 1, nx = w / b - 1;
        if (n < LIGHT_MIN_PHOTOS || step.X >= w || step.Y >= h) return null;
        double minX = Math.Floor(positions.Values.Min(p => p.X)), minY = Math.Floor(positions.Values.Min(p => p.Y));
        var zone = new bool[ny * nx];
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
                zone[y * nx + x] = Math.Abs(x * b + b - 0.5 - (w - 1) / 2.0) <= step.X / 2 && Math.Abs(y * b + b - 0.5 - (h - 1) / 2.0) <= step.Y / 2;
        var vals = new float[n][]; var ok = new bool[n][]; var corner = new (int X, int Y)[n];
        for (int k = 0; k < n; k++)
        {
            var item = items[k];
            int px = Py.RoundEvenInt(positions[k].X - minX), py = Py.RoundEvenInt(positions[k].Y - minY);
            int ax = Py.Mod(-px, b), ay = Py.Mod(-py, b);
            using Mat part8 = new Mat(item.Image!, new Rect(ax, ay, nx * b, ny * b));
            using Mat part = new Mat(), sq = new Mat(), mean = new Mat(), square = new Mat();
            part8.ConvertTo(part, MatType.CV_32FC3);
            Cv2.Multiply(part, part, sq);
            Cv2.Resize(part, mean, new Size(nx, ny), 0, 0, InterpolationFlags.Area);
            Cv2.Resize(sq, square, new Size(nx, ny), 0, 0, InterpolationFlags.Area);
            using Mat om = new Mat(item.OutputMask!, new Rect(ax, ay, nx * b, ny * b)), omr = new Mat();
            Cv2.Resize(om, omr, new Size(nx, ny), 0, 0, InterpolationFlags.Area);
            float[] mv = ImageIO.RawFloats(mean), sv = ImageIO.RawFloats(square); omr.GetArray(out byte[] ov);
            vals[k] = mv;
            ok[k] = new bool[ny * nx];
            for (int p = 0; p < ny * nx; p++)
            {
                float mx = float.NegativeInfinity, std = float.NegativeInfinity;
                for (int c = 0; c < 3; c++)
                {
                    float m = mv[p * 3 + c], d = sv[p * 3 + c] - m * m;
                    float s = MathF.Sqrt(d > 0f ? d : 0f);
                    if (s > std) std = s;
                    if (m > mx) mx = m;
                }
                ok[k][p] = std < (float)LIGHT_FLAT && ov[p] >= 255 && mx > LIGHT_DARK;
            }
            corner[k] = (Py.FloorDiv(px + ax, b), Py.FloorDiv(py + ay, b));
        }
        int cw = corner.Max(c => c.X) + nx, ch = corner.Max(c => c.Y) + ny;
        var light = new float[ny * nx * 3];
        var measured = new bool[ny * nx];
        for (int c = 0; c < 3; c++)
        {
            var depth = new sbyte[ch * cw];
            var stack = new float[4][];
            for (int l = 0; l < 4; l++) { stack[l] = new float[ch * cw]; Array.Fill(stack[l], float.NaN); }
            for (int k = 0; k < n; k++)
            {
                var (x0, y0) = corner[k];
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                    {
                        int p = y * nx + x, q = (y + y0) * cw + x + x0;
                        if (ok[k][p] && zone[p] && depth[q] < 4) { stack[depth[q]][q] = vals[k][p * 3 + c]; depth[q]++; }
                    }
            }
            var refv = new float[ch * cw];
            var buf = new List<float>(n);
            for (int q = 0; q < refv.Length; q++)
            {
                buf.Clear();
                for (int l = 0; l < 4; l++) if (!float.IsNaN(stack[l][q])) buf.Add(stack[l][q]);
                refv[q] = buf.Count == 0 ? float.NaN : Py.Median(buf);
            }
            var value = new float[ny * nx];
            var known = new bool[ny * nx];
            for (int p = 0; p < ny * nx; p++)
            {
                buf.Clear();
                int y = p / nx, x = p % nx;
                for (int k = 0; k < n; k++)
                {
                    float r = refv[(y + corner[k].Y) * cw + x + corner[k].X];
                    if (ok[k][p] && !zone[p] && float.IsFinite(r)) buf.Add(vals[k][p * 3 + c] - r);
                }
                known[p] = buf.Count >= LIGHT_MIN;
                value[p] = known[p] ? Py.Median(buf) : 0f;
                known[p] |= zone[p];
            }
            var weight = known.Select(v => v ? 1f : 0f).ToArray();
            var vw = new float[ny * nx];
            for (int p = 0; p < vw.Length; p++) vw[p] = value[p] * weight[p];
            float[] Blur(float[] src, double sigma)
            {
                using var m = Mat.FromPixelData(ny, nx, MatType.CV_32FC1, src);
                using var o = new Mat();
                Cv2.GaussianBlur(m, o, new Size(0, 0), sigma);
                o.GetArray(out float[] r);
                return r;
            }
            float[] near = Blur(weight, 2.0), far = Blur(weight, 12.0), vn = Blur(vw, 2.0), vf = Blur(vw, 12.0);
            for (int p = 0; p < ny * nx; p++)
            {
                light[p * 3 + c] = near[p] > (float)0.05 ? vn[p] / (near[p] > (float)1e-6 ? near[p] : (float)1e-6) : vf[p] / (far[p] > (float)1e-6 ? far[p] : (float)1e-6);
                measured[p] |= known[p] && !zone[p];
            }
        }
        if (!measured.Any(v => v)) return null;
        using Mat outOkM = new Mat();
        Cv2.Resize(items[0].OutputMask!, outOkM, new Size(nx, ny), 0, 0, InterpolationFlags.Nearest);
        outOkM.GetArray(out byte[] oo);
        var size = new List<float>();
        for (int p = 0; p < ny * nx; p++)
            if (oo[p] > 127 && !zone[p]) size.Add(Math.Max(Math.Abs(light[p * 3]), Math.Max(Math.Abs(light[p * 3 + 1]), Math.Abs(light[p * 3 + 2]))));
        Say($"[light] 화면에 더해진 빛을 사진 {n}장으로 재서 뺌: 잰 칸 {measured.Count(v => v)}개, 결과에 쓰이는 자리에서 크기 가운데 값 "
            + $"{F(Py.Median(size), 1)}·99% {F(Py.PercentileF32(size, 99), 1)}(0~255)");
        var lm = new Mat(ny, nx, MatType.CV_32FC3);
        ImageIO.SetRaw(lm, light);
        return lm;
    }

    /// <summary>py: remove_screen_light(items, light) — 사진에서 화면 빛을 뺀다(제자리)</summary>
    public static void RemoveScreenLight(IEnumerable<ImageItem> items, Mat light)
    {
        var list = items.ToList();
        int h = list[0].Shape.H, w = list[0].Shape.W;
        using Mat padded = new Mat(), full = new Mat();
        Cv2.CopyMakeBorder(light, padded, 0, 1, 0, 1, BorderTypes.Replicate);
        Cv2.Resize(padded, full, new Size(w, h), 0, 0, InterpolationFlags.Linear);
        foreach (var it in list)
        {
            using Mat o = new Mat(), noMask = new Mat();
            Cv2.Subtract(it.Image!, full, o, noMask, MatType.CV_8U);
            o.CopyTo(it.Image!);
        }
    }

    /// <summary>py: seam_masks(items, positions) — 사진마다 맡을 곳(0.25배 마스크). 이음매는 겹친 두 사진 색이 가장 비슷한 곳(그래프컷, 색 차이 비용)</summary>
    public static Mat[] SeamMasks(IReadOnlyList<ImageItem> items, IReadOnlyDictionary<int, (double X, double Y)> positions)
    {
        double s = SEAM_SCALE;
        double minX = Math.Floor(positions.Values.Min(p => p.X)), minY = Math.Floor(positions.Values.Min(p => p.Y));
        var imgs = new Mat[items.Count]; var corners = new Point[items.Count]; var masks = new Mat[items.Count];
        for (int idx = 0; idx < items.Count; idx++)
        {
            using Mat small = new Mat();
            Cv2.Resize(items[idx].Image!, small, new Size(0, 0), s, s, InterpolationFlags.Area);
            imgs[idx] = new Mat();
            small.ConvertTo(imgs[idx], MatType.CV_32FC3);
            corners[idx] = new Point(Py.RoundEvenInt((positions[idx].X - minX) * s), Py.RoundEvenInt((positions[idx].Y - minY) * s));
            using Mat m = new Mat();
            Cv2.Resize(items[idx].OutputMask!, m, new Size(small.Cols, small.Rows), 0, 0, InterpolationFlags.Nearest);
            masks[idx] = new Mat();
            Cv2.Threshold(m, masks[idx], 127, 255, ThresholdTypes.Binary);
        }
        if (Environment.GetEnvironmentVariable("GENSHIN_DUMP_DIR") is string dd)
        {
            Directory.CreateDirectory(dd);
            using var sha = System.Security.Cryptography.SHA1.Create();
            var lines = Enumerable.Range(0, items.Count).Select(k => $"{k} {corners[k].X} {corners[k].Y} "
                + Convert.ToHexString(sha.ComputeHash(ImageIO.RawBytes(imgs[k]))).ToLowerInvariant() + " "
                + Convert.ToHexString(sha.ComputeHash(ImageIO.RawBytes(masks[k]))).ToLowerInvariant());
            File.WriteAllLines(Path.Combine(dd, "seam_inputs.txt"), lines);
        }
        using var finder = new OpenCvSharp.Detail.GraphCutSeamFinder(OpenCvSharp.Detail.SeamFinderCostFunction.Color, 10000f, 1000f); // py/C++ 기본값(terminal_cost, bad_region_penalty)을 적어 넘긴다
        finder.Find(imgs, corners, masks);
        foreach (var m in imgs) m.Dispose();
        return masks;
    }

    /// <summary>py: compose_sharp(items, positions, owned, fill, canvas) — 겹친 곳을 섞지 않고 한 장만(맡은 곳 먼저, 그다음 가운데 가까운 한 장)</summary>
    public static Mat ComposeSharp(IReadOnlyList<ImageItem> items, IReadOnlyDictionary<int, (double X, double Y)> positions, Mat[]? owned,
        List<FillRow> fill, JsonObject? canvas)
    {
        int h = items[0].Shape.H, w = items[0].Shape.W;
        var xs = positions.Values.Select(p => p.X).ToList(); var ys = positions.Values.Select(p => p.Y).ToList();
        int refX = (int)Math.Floor(xs.Min()), refY = (int)Math.Floor(ys.Min());
        xs.AddRange(fill.Select(r => r.Position.X)); ys.AddRange(fill.Select(r => r.Position.Y));
        int minX = (int)Math.Floor(xs.Min()), minY = (int)Math.Floor(ys.Min());
        int outW = (int)Math.Ceiling(xs.Max() + w) - minX, outH = (int)Math.Ceiling(ys.Max() + h) - minY;
        if ((double)outW * outH > MAX_OUTPUT_PX) throw new InvalidOperationException("미확인 채움 포함 출력 크기가 합성 한도를 넘음");
        using var outImg = new Mat(outH, outW, MatType.CV_8UC3, Scalar.All(0));
        using var best = new Mat(outH, outW, MatType.CV_32FC1, Scalar.All(0));
        int Px(double x) => Py.RoundEvenInt(x - refX) + refX - minX;
        int Pyy(double y) => Py.RoundEvenInt(y - refY) + refY - minY;
        for (int idx = 0; idx < items.Count; idx++)
        {
            var item = items[idx];
            int px = Px(positions[idx].X), py = Pyy(positions[idx].Y);
            using Mat valid = new Mat(), dist = new Mat();
            Cv2.Threshold(item.OutputMask!, valid, 127, 1, ThresholdTypes.Binary);
            Cv2.DistanceTransform(valid, dist, DistanceTypes.L2, DistanceTransformMasks.Mask3);
            Cv2.MinMaxLoc(dist, out _, out double dmax);
            if (dmax > 0)
            {
                float fmax = (float)dmax;
                dist.GetArray(out float[] dv);
                for (int p = 0; p < dv.Length; p++) dv[p] /= fmax;
                dist.SetArray(dv);
            }
            if (owned is not null)
            {
                using Mat mine = new Mat();
                Cv2.Resize(owned[idx], mine, new Size(w, h), 0, 0, InterpolationFlags.Nearest);
                mine.GetArray(out byte[] mn); valid.GetArray(out byte[] va); dist.GetArray(out float[] dv);
                for (int p = 0; p < dv.Length; p++) if (mn[p] > 127 && va[p] > 0) dv[p] += 2.0f;
                dist.SetArray(dv);
            }
            var rect = new Rect(px, py, w, h);
            using Mat region = new Mat(best, rect), winM = new Mat(), dst = new Mat(outImg, rect);
            Cv2.Compare(dist, region, winM, CmpTypes.GT);
            item.Image!.CopyTo(dst, winM);
            dist.CopyTo(region, winM);
        }
        foreach (var row in fill)
        {
            var item = row.Item;
            var rect = new Rect(Px(row.Position.X), Pyy(row.Position.Y), w, h);
            using Mat region = new Mat(best, rect), z = new Mat(), om = new Mat(), win = new Mat(), dst = new Mat(outImg, rect);
            Cv2.Compare(region, 0, z, CmpTypes.EQ);
            Cv2.Compare(item.OutputMask!, 127, om, CmpTypes.GT);
            Cv2.BitwiseAnd(z, om, win);
            item.Image!.CopyTo(dst, win);
            region.SetTo(1.0, win);
            row.FilledPixels = Cv2.CountNonZero(win);
        }
        using var patch = new Mat(outH, outW, MatType.CV_8UC1, Scalar.All(0));
        for (int idx = 0; idx < items.Count; idx++)
        {
            var item = items[idx];
            if (!POINTER_SOFT.TryGetValue(item.Name, out var soft)) continue;
            var rect = new Rect(Px(positions[idx].X), Pyy(positions[idx].Y), w, h);
            using Mat region = new Mat(best, rect), z = new Mat(), win = new Mat(), wt = new Mat(), dst = new Mat(outImg, rect), pr = new Mat(patch, rect);
            Cv2.Compare(region, 0, z, CmpTypes.EQ);
            Cv2.BitwiseAnd(z, soft.Hidden, win);
            item.Image!.CopyTo(dst, win);
            region.SetTo(0.5, win);
            Cv2.BitwiseAnd(win, soft.Tight, wt);
            pr.SetTo(255, wt);
        }
        int pc = Cv2.CountNonZero(patch);
        if (pc > 0) Say($"[ui] 다른 사진으로 못 덮은 플레이어 화살표 {pc}px는 그대로 둠(플레이어 실제 자리)");
        using Mat any = new Mat();
        Cv2.Compare(best, 0, any, CmpTypes.GT);
        using Mat rowsAny = new Mat(), colsAny = new Mat();
        Cv2.Reduce(any, rowsAny, ReduceDimension.Column, ReduceTypes.Max, MatType.CV_8U);
        Cv2.Reduce(any, colsAny, ReduceDimension.Row, ReduceTypes.Max, MatType.CV_8U);
        rowsAny.GetArray(out byte[] ra); colsAny.GetArray(out byte[] ca);
        int r0 = Array.FindIndex(ra, v => v > 0), r1 = Array.FindLastIndex(ra, v => v > 0);
        int c0 = Array.FindIndex(ca, v => v > 0), c1 = Array.FindLastIndex(ca, v => v > 0);
        Mat result = r0 >= 0 && c0 >= 0 ? new Mat(outImg, new Rect(c0, r0, c1 - c0 + 1, r1 - r0 + 1)).Clone() : outImg.Clone();
        if (canvas is not null)
        {
            canvas["origin"] = new JsonArray(minX + c0, minY + r0);
            canvas["size"] = new JsonArray(result.Cols, result.Rows);
        }
        return result;
    }

    public sealed class FillRow
    {
        public required ImageItem Item;
        public (double X, double Y) Position;
        public int FilledPixels;
    }

    /// <summary>py: compose_grid(items, positions, fill, canvas, step) — 화면 빛 빼기 → 이음매 → 한 장씩 합치기</summary>
    public static Mat ComposeGrid(IReadOnlyList<ImageItem> items, IReadOnlyDictionary<int, (double X, double Y)> positions, List<FillRow> fill,
        JsonObject? canvas, (double X, double Y)? step)
    {
        if (step is (double, double) st)
        {
            try
            {
                using Mat? light = ScreenLight(items, positions, st);
                // 진단(이식 대조용): GENSHIN_DUMP_DIR가 있으면 화면 빛 값을 원시 float로 남긴다
                if (light is not null && Environment.GetEnvironmentVariable("GENSHIN_DUMP_DIR") is string dd)
                {
                    Directory.CreateDirectory(dd);
                    var lf = ImageIO.RawFloats(light);
                    var lb = new byte[lf.Length * 4]; Buffer.BlockCopy(lf, 0, lb, 0, lb.Length);
                    File.WriteAllBytes(Path.Combine(dd, $"light_{light.Rows}x{light.Cols}x3.f32"), lb);
                }
                if (light is null) Say("[light] 사진이 적거나 겹친 곳이 모자라 화면에 더해진 빛은 안 뺌");
                else RemoveScreenLight(items.Concat(fill.Select(r => r.Item)), light);
            }
            catch (Exception exc) when (exc is OpenCVException or ArgumentException or OutOfMemoryException)
            {
                Say($"[light] 화면에 더해진 빛 재기 실패, 안 빼고 합침: {exc.Message}");
            }
        }
        Mat[]? owned;
        try
        {
            owned = SeamMasks(items, positions);
            Say($"[seam] 이음매를 겹친 사진이 가장 비슷한 곳으로 냄({items.Count}장)");
            if (Environment.GetEnvironmentVariable("GENSHIN_DUMP_DIR") is string dd)
                for (int k = 0; k < owned.Length; k++) ImageIO.ImwriteUnicode(Path.Combine(dd, $"seam_{k:D3}.png"), owned[k]);
        }
        catch (OpenCVException exc)
        {
            Say($"[seam] 이음매 찾기 실패, 가운데 가까운 한 장으로 합침: {exc.Message}");
            owned = null;
        }
        return ComposeSharp(items, positions, owned, fill, canvas);
    }

    // ---------- 붙이기 본체 ----------

    public sealed class Args
    {
        public required string InputDir, Grid, Output;
        public List<(int, int, int, int)> Exclude = new();
    }

    static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>py: main() — 끈 위치 기록(capture_grid.json version 2 이상)으로 격자 붙이기. 끝 코드 0 = 성공</summary>
    public static int Main(Args args)
    {
        var grid = JsonNode.Parse(File.ReadAllText(args.Grid))!.AsObject();
        var stepArr = grid["step"]!.AsArray();
        (double X, double Y) step = (stepArr[0]!.GetValue<double>(), stepArr[1]!.GetValue<double>());
        var loaded = Engine.LoadImages(args.InputDir);
        double pxScale = loaded[0].Img.Cols / 2560.0;
        if (Math.Abs(pxScale - 1.0) > 1e-6)
        {
            PRIOR_TOL *= pxScale; FIT_TOL *= pxScale; STILL_TOL *= pxScale;
            Say($"[scale] 사진 너비 {loaded[0].Img.Cols}px: 위치 허용치 {F(pxScale, 3)}배(끈 위치 {F(PRIOR_TOL, 0)}px, 앞뒤 {F(FIT_TOL, 0)}px)");
        }
        string gridDir = Path.GetDirectoryName(Path.GetFullPath(args.Grid))!;
        string outDir = Path.GetDirectoryName(Path.GetFullPath(args.Output))!;
        string picUiPath = Path.Combine(gridDir, "ui_mask.png");
        Mat? pictureUi = null;
        if (File.Exists(picUiPath))
        {
            using Mat? c = ImageIO.ImreadUnicode(picUiPath);
            if (c is not null) { pictureUi = new Mat(); Cv2.CvtColor(c, pictureUi, ColorConversionCodes.BGR2GRAY); }
        }
        var pictureCheck = MakePictureCheck(loaded.Select(l => l.Path).ToList(), pictureUi);
        Console.WriteLine($"[input] {loaded.Count} images");
        var tiles = grid["tiles"]!.AsObject();
        var missing = loaded.Select(l => Path.GetFileName(l.Path)).Where(nm => !tiles.ContainsKey(nm)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"끈 위치 기록이 없는 사진: {string.Join(", ", missing.Take(5))}");
        var model = grid["map_model"] as JsonObject;
        bool hasModel = model is not null && model.Count > 0;
        double correction = DEVIGNETTE;
        if (model?["brightness"] is JsonObject br && br["strength"] is JsonNode sn) correction = sn.GetValue<double>();
        if (!double.IsFinite(correction) || correction < 0 || correction > .6) throw new ArgumentException("Invalid calibrated brightness strength");
        if (correction > 0)
        {
            Say($"[devignette] strength={F(correction, 3)}, power={F(2.0, 2)}, start={F(0.0, 2)}");
            loaded = loaded.Select(l => (l.Path, Engine.RemoveVignette(l.Img, correction))).ToList();
        }
        var items = Engine.ExtractFeatures(loaded, "genshin", args.Exclude, NFEATURES, [], 25);
        foreach (var it in items) Console.WriteLine($"[features] {it.Name}: {it.KeyPoints.Length} keypoints");
        int n = items.Count;
        var smalls = items.Select(it => UiSmall(it.Gray!)).ToList();
        Mat? ui = LearnStaticUi(smalls);
        int h0 = items[0].Gray!.Rows, w0 = items[0].Gray!.Cols;
        if (hasModel)
        {
            if (File.Exists(picUiPath))
            {
                using Mat? pr = ImageIO.ImreadUnicode(picUiPath);
                if (pr is not null)
                {
                    using Mat g = new Mat(); Cv2.CvtColor(pr, g, ColorConversionCodes.BGR2GRAY);
                    var prior = new Mat(); Cv2.Compare(g, 127, prior, CmpTypes.GT);
                    if (prior.Rows == smalls[0].Rows && prior.Cols == smalls[0].Cols)
                    {
                        if (ui is null) ui = prior;
                        else { var u2 = new Mat(); Cv2.BitwiseOr(ui, prior, u2); ui = u2; }
                    }
                }
            }
            ui = BoxStaticUi(ui, step);
        }
        if (ui is not null)
        {
            using Mat full = UiFull(ui, w0, h0);
            items = items.Select(it => ApplyStaticUi(it, full)).ToList();
            // py: ui.astype(np.uint8) * 255 → 0/255 PNG(출력 폴더의 ui_mask.png. 입력이 같은 폴더면 덮어쓴다 — 다시 붙이면 결과가 달라질 수 있는 알려진 점)
            Cv2.ImEncode(".png", ui, out byte[] buf);
            File.WriteAllBytes(Path.Combine(outDir, "ui_mask.png"), buf);
            Say($"[ui] 사진 {n}장에서 안 움직이는 화면 요소(화면의 {F(Cv2.CountNonZero(ui) * 100.0 / (ui.Rows * ui.Cols), 1)}%)를 찾아 매칭·결과에서 뺌");
        }
        using Mat fixedSmall = FixedUiSmall(smalls[0].Rows, smalls[0].Cols);
        using Mat zone = UiFull(fixedSmall, w0, h0);
        int pointers = 0;
        for (int k = 0; k < n; k++)
        {
            var it = items[k];
            Mat outMask = it.OutputMask!.Clone();
            outMask.SetTo(0, zone);
            var found = PointerMask(it.Image!);
            if (found is var (hide, tight))
            {
                using Mat om127 = new Mat(); Cv2.Compare(outMask, 127, om127, CmpTypes.GT);
                var hid = new Mat(); Cv2.BitwiseAnd(hide, om127, hid);
                POINTER_SOFT[it.Name] = (hid, tight);
                outMask.SetTo(0, hide);
                pointers++;
            }
            items[k] = it.With(outputMask: outMask);
        }
        Say($"[ui] 고정 금지 구역(공지 패널 자리, 화면의 {F(Cv2.CountNonZero(zone) * 100.0 / (zone.Rows * zone.Cols), 1)}%)은 결과 그림에서만 뺌");
        Say($"[ui] 플레이어 방향 표시(파란 화살표)를 {pointers}장에서 결과 그림에서만 뺌");
        var priorPos = new double[n, 2];
        for (int i = 0; i < n; i++) { var t = tiles[items[i].Name]!; priorPos[i, 0] = t["x"]!.GetValue<double>(); priorPos[i, 1] = t["y"]!.GetValue<double>(); }

        var matches = new OrderedDictionary<(int, int), PairMatch?>();
        void Compare(IEnumerable<(int, int)> pairs)
        {
            foreach (var (i, j) in pairs.OrderBy(p => p))
            {
                if (matches.ContainsKey((i, j))) continue;
                var pm = MatchPair(items[i], items[j], i, j, WEAK_INLIERS);
                matches[(i, j)] = pm;
                if (pm is null) Say($"[match] {items[i].Name} -> {items[j].Name}: failed");
                else Say($"[match] {items[i].Name} -> {items[j].Name}: dx={F(pm.Dx, 1)}, dy={F(pm.Dy, 1)}, inliers={pm.Inliers}, score={F(pm.Score, 1)}");
            }
        }

        int version = grid["version"]?.GetValue<int>() ?? 1;
        if (version < 2) throw new NotSupportedException("끈 위치 기록이 없는 옛 capture_grid.json(version 1)은 C# 판에서 붙이지 않는다");
        var guessed = new HashSet<int>(); var unplaced = new HashSet<int>();
        var cells = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            var t = tiles[items[i].Name]!.AsObject();
            if (t["guessed"] is JsonNode gn && gn.GetValue<bool>()) guessed.Add(i);
            if (t["placed"] is JsonNode pn && pn.GetValueKind() == JsonValueKind.False) unplaced.Add(i);
            if (t["cell"] is JsonArray cell)
            {
                var key = (cell[0]!.GetValue<int>(), cell[1]!.GetValue<int>());
                if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<int>();
                l.Add(i);
            }
        }
        var sameCells = new HashSet<(int, int)>();
        foreach (var g in cells.Values) foreach (int i in g) foreach (int j in g) if (i < j) sameCells.Add((i, j));
        return TrustedLayout(args, outDir, items, priorPos, step, matches, Compare, guessed, unplaced, sameCells, pictureCheck);
    }

    static (double, double) Off(PairMatch pm) => OffsetOf(pm);

    /// <summary>py: trusted_layout — 끈 위치 기록 모드의 자리 맞추기·표결·검증·합치기</summary>
    static int TrustedLayout(Args args, string outDir, List<ImageItem> items, double[,] prior, (double X, double Y) step,
        OrderedDictionary<(int, int), PairMatch?> matches, Action<IEnumerable<(int, int)>> compare,
        HashSet<int> guessed, HashSet<int> unplaced, HashSet<(int, int)> sameCells,
        Func<int, int, (double X, double Y), (double Score, int Samples, string? Reason)> pictureCheck)
    {
        int n = items.Count;
        var chain = Enumerable.Range(0, Math.Max(0, n - 1)).Select(i => (i, i + 1)).ToHashSet();
        var first = NearPairs(prior, step, Enumerable.Range(0, n));
        first.UnionWith(chain); first.UnionWith(sameCells);
        int baseCount = first.Count;
        var extra = new HashSet<(int, int)>();
        if (unplaced.Count > 0)
        {
            int share = Math.Max(1, Math.Min(UNPLACED_PER_PHOTO, UNPLACED_PAIRS / unplaced.Count));
            foreach (int i in unplaced.OrderBy(v => v))
            {
                var others = Enumerable.Range(0, n).Where(j => j != i)
                    .OrderBy(j => Py.Hypot((prior[j, 0] - prior[i, 0]) / step.X, (prior[j, 1] - prior[i, 1]) / step.Y)).ToList();
                int got = 0;
                foreach (int j in others)
                {
                    var k = (Math.Min(i, j), Math.Max(i, j));
                    if (first.Contains(k) || extra.Contains(k)) continue;
                    extra.Add(k);
                    if (++got >= share) break;
                }
            }
        }
        first.UnionWith(extra);
        int budgetUsed = 0, budgetMax = PAIR_BUDGET * n;
        var clock = Stopwatch.StartNew();
        string? budgetStop = null;
        var borderRejected = new SortedDictionary<(int, int), int>();
        var pictureResults = new SortedDictionary<(int, int), (double Score, int Samples, string? Reason)>();
        double pictureSeconds = 0;
        (double, double) Expect((int I, int J) k) => (prior[k.J, 0] - prior[k.I, 0], prior[k.J, 1] - prior[k.I, 1]);

        void RunCompare(IEnumerable<(int, int)> pairs)
        {
            foreach (var k in pairs.Where(p => !matches.ContainsKey(p)).Distinct().OrderBy(p => p).ToList())
            {
                if (budgetStop is null && budgetUsed >= budgetMax) budgetStop = $"비교 한도 {budgetMax}쌍(사진 {n}장 × {PAIR_BUDGET})";
                if (budgetStop is null && clock.Elapsed.TotalSeconds > COMPARE_SECONDS) budgetStop = $"비교 시간 한도 {COMPARE_SECONDS}초";
                if (budgetStop is not null) { Say($"[limit] {budgetStop}에 닿아 비교를 그만둠: 그때까지 맞춘 것으로 놓는다"); return; }
                compare([k]);
                var pm = matches[k];
                var ex = Expect(k);
                if (pm is not null && BorderOnlyFarZero(pm, ex, step))
                {
                    borderRejected[k] = pm.Inliers;
                    matches[k] = null;
                    Say($"[reject-border] {items[k.Item1].Name} -> {items[k.Item2].Name}: {pm.Inliers}점 모두 테두리, 기록은 먼데 거의 같은 화면");
                }
                else if (pm is not null && Py.Hypot(-pm.Dx, -pm.Dy) < 40 && (Math.Abs(ex.Item1) > step.X / 2 || Math.Abs(ex.Item2) > step.Y / 2))
                {
                    var sw = Stopwatch.StartNew();
                    var result = pictureCheck(k.Item1, k.Item2, Off(pm));
                    pictureSeconds += sw.Elapsed.TotalSeconds;
                    pictureResults[k] = result;
                    if (result.Score < PICTURE_MIN)
                    {
                        matches[k] = null;
                        Say($"[reject-picture] {items[k.Item1].Name} -> {items[k.Item2].Name}: 상관 {F(result.Score, 3)}, 0 근처 먼 매칭");
                    }
                }
                pm = matches[k];
                if (pm is null) Say($"[live] {items[k.Item1].Name} {items[k.Item2].Name} 0 0 0");
                else
                {
                    var (ox, oy) = Off(pm);
                    bool ok = pm.Inliers >= TRUST_STRONG || Py.Hypot(ox - ex.Item1, oy - ex.Item2) <= PRIOR_TOL;
                    Say($"[live] {items[k.Item1].Name} {items[k.Item2].Name} {(ok ? 1 : 0)} {F(ox, 1)} {F(oy, 1)}");
                }
                budgetUsed++;
            }
        }

        Say($"[pair-search] gap=grid: 끈 위치 이웃 + 찍은 순서 이웃 {baseCount}쌍 + 캡처가 자리를 못 찾은 사진 {unplaced.Count}장 몫 {extra.Count}쌍부터 비교(한도 {budgetMax}쌍·{COMPARE_SECONDS}초)");
        RunCompare(sameCells);
        RunCompare(extra);
        RunCompare(first);

        var accepted = matches.Where(kv => kv.Value is not null && (kv.Value.Inliers >= TRUST_STRONG
            || Py.Hypot(Off(kv.Value).Item1 - Expect(kv.Key).Item1, Off(kv.Value).Item2 - Expect(kv.Key).Item2) <= PRIOR_TOL)).Select(kv => kv.Key).ToHashSet();
        var inconsistent = new HashSet<(int, int)>();
        var chainUsed = new HashSet<(int, int)>(chain);
        var groupNotes = new JsonArray();
        var groupRejected = new List<JsonObject>();
        var voted = new HashSet<string>();
        var denseSeen = new Dictionary<(int, int), ((double X, double Y) Offset, double Peak, double Second)?>();
        var denseUsed = new SortedDictionary<(int, int), (double X, double Y)>();

        double[,] Fit()
        {
            var edges = new List<(int, int, double, double, double)>();
            foreach (var (i, j) in chainUsed.OrderBy(p => p)) { var e = Expect((i, j)); edges.Add((i, j, e.Item1, e.Item2, PRIOR_WEIGHT)); }
            foreach (var (i, j) in accepted.OrderBy(p => p)) { var pm = matches[(i, j)]!; var o = Off(pm); edges.Add((i, j, o.Item1, o.Item2, Math.Sqrt(pm.Inliers))); }
            foreach (var kv in denseUsed) edges.Add((kv.Key.Item1, kv.Key.Item2, kv.Value.X, kv.Value.Y, DENSE_WEIGHT));
            return Solve(n, edges, prior, [0]);
        }

        bool GroupMoveSupported(HashSet<int> gs, HashSet<int> main, double[,] pos, (double X, double Y) best,
            List<((int, int) Pair, string Kind, double Vote, (double X, double Y) Off)> votes, List<bool> agree)
        {
            double MedDiff(IEnumerable<int> idx, int axis) => Py.Median(idx.OrderBy(i => i).Select(i => pos[i, axis] - prior[i, axis]));
            double cx = MedDiff(gs, 0) - MedDiff(main, 0) + best.X, cy = MedDiff(gs, 1) - MedDiff(main, 1) + best.Y;
            if (Math.Abs(cx) <= step.X / 2 && Math.Abs(cy) <= step.Y / 2) return true;
            var anchors = new HashSet<int>();
            for (int t = 0; t < votes.Count; t++)
            {
                if (!agree[t]) continue;
                var (i, j) = votes[t].Pair;
                anchors.Add(gs.Contains(j) ? i : j);
                PairMatch? pm = votes[t].Kind == "match" && matches.TryGetValue((i, j), out var m) ? m : null;
                if (pm is not null && pm.Inliers >= TRUST_STRONG && (pm.InteriorInliers ?? 0) >= WEAK_INLIERS) return true;
            }
            return anchors.Count >= 2;
        }

        double[,] JoinGroups(double[,] pos)
        {
            bool changed = false;
            var groups = Components(n, accepted);
            var groupOf = new int[n];
            for (int gi = 0; gi < groups.Count; gi++) foreach (int i in groups[gi]) groupOf[i] = gi;
            foreach (var k in chainUsed.ToList())
                if (groupOf[k.Item1] == groupOf[k.Item2] && Residual(pos, k.Item1, k.Item2, Expect(k)) > PRIOR_TOL) { chainUsed.Remove(k); changed = true; }
            var main = groups.First(g => g.Contains(0)).ToHashSet();
            var waiting = groups.Where(g => !g.Contains(0) && !voted.Contains(Key(g))).Select(g => g.ToHashSet()).ToList();
            while (waiting.Count > 0)
            {
                bool placed = false;
                foreach (var gs in waiting.OrderByDescending(g => g.Count).ToList())
                {
                    var votes = new List<((int, int) Pair, string Kind, double Vote, (double X, double Y) Off)>();
                    bool Cross(int i, int j) => (gs.Contains(i) && main.Contains(j)) || (gs.Contains(j) && main.Contains(i));
                    foreach (var (i, j) in NearPairs(pos, step, gs.Union(main)).OrderBy(p => p))
                    {
                        if (!Cross(i, j)) continue;
                        if (!denseSeen.ContainsKey((i, j)))
                        {
                            if (budgetUsed >= budgetMax || clock.Elapsed.TotalSeconds > COMPARE_SECONDS) continue;
                            budgetUsed++;
                            denseSeen[(i, j)] = DenseOffset(items[i], items[j], (pos[j, 0] - pos[i, 0], pos[j, 1] - pos[i, 1]));
                        }
                        var r = denseSeen[(i, j)];
                        if (r is not null && r.Value.Peak >= DENSE_PEAK && r.Value.Peak - r.Value.Second >= DENSE_MARGIN) votes.Add(((i, j), "dense", DENSE_VOTE, r.Value.Offset));
                    }
                    foreach (var (i, j) in chain.OrderBy(p => p))
                        if (Cross(i, j)) { var e = Expect((i, j)); votes.Add(((i, j), "drag", guessed.Contains(j) ? GUESSED_DRAG_VOTE : 1.0, (e.Item1, e.Item2))); }
                    foreach (var kv in matches)
                        if (kv.Value is not null && !inconsistent.Contains(kv.Key) && Cross(kv.Key.Item1, kv.Key.Item2))
                        { var o = Off(kv.Value); votes.Add((kv.Key, "match", GROUP_MATCH_VOTE, (o.Item1, o.Item2))); }
                    if (votes.Count == 0) continue;
                    var shifts = new List<(double X, double Y)>();
                    foreach (var v in votes)
                    {
                        var (i, j) = v.Pair;
                        (double, double) d = (v.Off.X - (pos[j, 0] - pos[i, 0]), v.Off.Y - (pos[j, 1] - pos[i, 1]));
                        shifts.Add(gs.Contains(j) ? d : (-d.Item1, -d.Item2));
                    }
                    var support = shifts.Select(s => { double sum = 0; for (int t = 0; t < votes.Count; t++) if (Py.Hypot(shifts[t].X - s.X, shifts[t].Y - s.Y) <= FIT_TOL) sum += votes[t].Vote; return sum; }).ToList();
                    int bi = 0;
                    for (int t = 1; t < support.Count; t++) if (support[t] > support[bi]) bi = t;
                    var best = shifts[bi];
                    var agree = shifts.Select(t => Py.Hypot(t.X - best.X, t.Y - best.Y) <= FIT_TOL).ToList();
                    if (!GroupMoveSupported(gs, main, pos, best, votes, agree))
                    {
                        groupRejected.Add(new JsonObject
                        {
                            ["members"] = new JsonArray(gs.OrderBy(i => i).Select(i => (JsonNode)items[i].Name).ToArray()),
                            ["shift"] = new JsonArray(best.X, best.Y),
                            ["reason"] = "큰 묶음 보정에 독립 바깥 사진 2장 또는 안쪽 강매칭 지지 부족",
                            ["votes"] = new JsonArray(votes.Where((_, t) => agree[t]).Select(v => (JsonNode)new JsonArray(v.Kind, items[v.Pair.Item1].Name, items[v.Pair.Item2].Name)).ToArray()),
                        });
                        continue;
                    }
                    for (int t = 0; t < votes.Count; t++)
                    {
                        var (k, kind, _, off) = votes[t];
                        if (kind == "match" && agree[t]) { accepted.Add(k); inconsistent.Remove(k); }
                        else if (kind == "dense" && agree[t]) denseUsed[k] = off;
                        else if (kind == "drag" && !agree[t]) chainUsed.Remove(k);
                    }
                    var names = gs.OrderBy(i => i).Select(i => items[i].Name.Split('_')[1]).ToList();
                    int Count(string kind) => votes.Where((v, t) => v.Kind == kind && agree[t]).Count();
                    string note = $"따로 떨어진 묶음 {gs.Count}장({string.Join(", ", names.Take(6))}{(names.Count > 6 ? "…" : "")}): "
                        + $"촘촘 {Count("dense")}·끈 위치 {Count("drag")}·매칭 {Count("match")}개가 맞아 ({SignedF0(best.X)},{SignedF0(best.Y)})px 옮김, 안 맞아 뺀 것 {agree.Count(a => !a)}개";
                    Say($"[group] {note}");
                    groupNotes.Add(new JsonObject
                    {
                        ["note"] = note,
                        ["votes"] = new JsonArray(votes.Select((v, t) => (JsonNode)new JsonArray(v.Kind, items[v.Pair.Item1].Name, items[v.Pair.Item2].Name,
                            Py.RoundEven(shifts[t].X), Py.RoundEven(shifts[t].Y), agree[t])).ToArray()),
                    });
                    voted.Add(Key(gs));
                    main.UnionWith(gs);
                    waiting.Remove(waiting.First(w => w.SetEquals(gs)));
                    placed = changed = true;
                    break;
                }
                if (!placed) break;
            }
            return changed ? Fit() : pos;
        }

        double[,] DropInconsistent(double[,] pos)
        {
            while (accepted.Count > 0)
            {
                var worst = accepted.OrderBy(p => p).First();
                double wr = Residual(pos, worst.Item1, worst.Item2, Off(matches[worst]!));
                foreach (var k in accepted.OrderBy(p => p))
                {
                    double r = Residual(pos, k.Item1, k.Item2, Off(matches[k]!));
                    if (r > wr) { wr = r; worst = k; }
                }
                if (wr <= FIT_TOL) break;
                accepted.Remove(worst);
                inconsistent.Add(worst);
                pos = Fit();
            }
            return pos;
        }

        double[,] Refit() => DropInconsistent(JoinGroups(DropInconsistent(Fit())));

        var posF = Refit();
        string? growNote = null;
        for (int it = 0; it < 8; it++)
        {
            var newPairs = NearPairs(posF, step, Enumerable.Range(0, n));
            newPairs.ExceptWith(matches.Keys);
            if (newPairs.Count > GROW_LIMIT * baseCount)
            {
                growNote = $"맞춘 위치로 찾은 새 이웃 {newPairs.Count}쌍이 처음 기본 쌍 {baseCount}의 {GROW_LIMIT}배를 넘음: 위치가 뭉친 것 같아 더 넓히지 않음";
                Say($"[limit] {growNote}");
                break;
            }
            RunCompare(newPairs);
            var added = matches.Where(kv => kv.Value is not null && !accepted.Contains(kv.Key) && !inconsistent.Contains(kv.Key)
                && (kv.Value.Inliers >= TRUST_STRONG || Residual(posF, kv.Key.Item1, kv.Key.Item2, Off(kv.Value)) <= FIT_TOL)).Select(kv => kv.Key).ToHashSet();
            if (newPairs.Count == 0 && added.Count == 0) break;
            accepted.UnionWith(added);
            posF = Refit();
        }
        var offPrior = matches.Where(kv => kv.Value is not null).Select(kv => kv.Key).ToHashSet();
        offPrior.ExceptWith(accepted); offPrior.ExceptWith(inconsistent);
        var links = new HashSet<(int, int)>(accepted); links.UnionWith(denseUsed.Keys);
        var linked = links.SelectMany(k => new[] { k.Item1, k.Item2 }).ToHashSet();
        var alone = Enumerable.Range(0, n).Where(i => !linked.Contains(i)).Select(i => items[i].Name).ToList();
        var verified = new HashSet<int>();
        foreach (var g in Components(n, links)) if (g.Any(i => !unplaced.Contains(i))) verified.UnionWith(g);
        var unverified = Enumerable.Range(0, n).Where(i => !verified.Contains(i)).ToList();
        var farZero = accepted.Where(k => Py.Hypot(Off(matches[k]!).Item1, Off(matches[k]!).Item2) < 40
            && (Math.Abs(Expect(k).Item1) > step.X / 2 || Math.Abs(Expect(k).Item2) > step.Y / 2)).ToList();
        if (farZero.Count > 0) Say($"[note] 기록상 반 칸 넘게 먼데 거의 같은 자리로 맞은 연결 {farZero.Count}개(같은 곳을 다시 찍었거나 화면 요소)");
        if (unverified.Count > 0)
            Say($"[unverified] 자리를 검증 못 한 사진 {unverified.Count}장은 결과 그림에 안 넣고 미확인_사진/에 따로 둠: "
                + string.Join(", ", unverified.Take(12).Select(i => items[i].Name.Split('_')[1])) + (unverified.Count > 12 ? "…" : ""));
        double medDx = Py.Median(Enumerable.Range(0, n).Select(i => posF[i, 0] - prior[i, 0]));
        double medDy = Py.Median(Enumerable.Range(0, n).Select(i => posF[i, 1] - prior[i, 1]));
        var shiftLen = Enumerable.Range(0, n).Select(i => Math.Sqrt(Math.Pow(posF[i, 0] - prior[i, 0] - medDx, 2) + Math.Pow(posF[i, 1] - prior[i, 1] - medDy, 2))).ToList();
        var chainUnused = chain.Where(k => !chainUsed.Contains(k)).OrderBy(p => p).ToList();
        Say($"[grid] 비교 {matches.Count}쌍, 인정 {accepted.Count}쌍 · 맞는 점 적고 위치도 안 맞아 버림 {offPrior.Count}쌍 · "
            + $"앞뒤 안 맞아 뺌 {inconsistent.Count}쌍, 촘촘 맞추기로 이은 쌍 {denseUsed.Count}개, 매칭 없이 끈 위치로 놓은 사진 {alone.Count}장, "
            + $"끈 위치에서 옮겨진 정도 중앙 {F(Py.Median(shiftLen), 1)}px 최대 {F(shiftLen.Max(), 1)}px, "
            + $"매칭과 안 맞아 안 쓴 끈 위치 연결 {chainUnused.Count}개(그중 캡처가 못 잰 것 {guessed.Count}개 중 {chainUnused.Count(k => guessed.Contains(k.Item2))}개), "
            + $"큰 묶음과 매칭으로 안 이어진 묶음 {Components(n, links).Count(g => !g.Contains(0))}개");

        JsonArray Pairs3(IEnumerable<(int, int)> ks) => new(ks.OrderBy(p => p).Select(k => (JsonNode)new JsonArray(items[k.Item1].Name, items[k.Item2].Name, matches[k]!.Inliers)).ToArray());
        var record = new JsonObject
        {
            ["mode"] = "trusted",
            ["positions"] = Obj(Enumerable.Range(0, n).Select(i => (items[i].Name, (JsonNode)new JsonArray(posF[i, 0], posF[i, 1])))),
            ["prior"] = Obj(Enumerable.Range(0, n).Select(i => (items[i].Name, (JsonNode)new JsonArray(prior[i, 0], prior[i, 1])))),
            ["accepted"] = Pairs3(accepted),
            ["rejected_off_prior"] = Pairs3(offPrior),
            ["rejected_inconsistent"] = Pairs3(inconsistent),
            ["dense_links"] = new JsonArray(denseUsed.Select(kv => (JsonNode)new JsonArray(items[kv.Key.Item1].Name, items[kv.Key.Item2].Name, Py.Round(kv.Value.X, 1), Py.Round(kv.Value.Y, 1))).ToArray()),
            ["placed_by_drag_only"] = new JsonArray(alone.Select(s => (JsonNode)s).ToArray()),
            ["drag_links_unused"] = new JsonArray(chainUnused.Select(k => (JsonNode)new JsonArray(items[k.Item1].Name, items[k.Item2].Name)).ToArray()),
            ["drag_guessed"] = new JsonArray(guessed.OrderBy(i => i).Select(i => (JsonNode)items[i].Name).ToArray()),
            ["groups"] = groupNotes,
            ["rejected_group_moves"] = new JsonArray(groupRejected.Select(o => (JsonNode)o).ToArray()),
            ["unjoined_groups"] = new JsonArray(Components(n, links).Where(g => !g.Contains(0)).Select(g => (JsonNode)new JsonArray(g.Select(i => (JsonNode)items[i].Name).ToArray())).ToArray()),
            ["unverified"] = new JsonArray(unverified.Select(i => (JsonNode)items[i].Name).ToArray()),
            ["far_zero_pairs"] = new JsonArray(farZero.OrderBy(p => p).Select(k => (JsonNode)new JsonArray(items[k.Item1].Name, items[k.Item2].Name)).ToArray()),
            ["rejected_border_only"] = new JsonArray(borderRejected.Select(kv => (JsonNode)new JsonArray(items[kv.Key.Item1].Name, items[kv.Key.Item2].Name, kv.Value)).ToArray()),
            ["picture_checks"] = new JsonArray(pictureResults.Select(kv => (JsonNode)new JsonObject
            {
                ["a"] = items[kv.Key.Item1].Name, ["b"] = items[kv.Key.Item2].Name, ["score"] = kv.Value.Score, ["samples"] = kv.Value.Samples,
                ["reason"] = kv.Value.Reason, ["accepted_evidence"] = kv.Value.Score >= PICTURE_MIN,
            }).ToArray()),
            ["picture_seconds"] = Math.Round(pictureSeconds, 3),
            ["same_cell_pairs"] = new JsonArray(sameCells.OrderBy(p => p).Select(k => (JsonNode)new JsonArray(items[k.Item1].Name, items[k.Item2].Name)).ToArray()),
            ["compare_budget"] = new JsonObject { ["used"] = budgetUsed, ["max"] = budgetMax, ["stop"] = budgetStop, ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1) },
            ["grow_stopped"] = growNote,
        };
        var keep = Enumerable.Range(0, n).Where(i => !unverified.Contains(i)).ToList();
        int h0 = items[0].Gray!.Rows, w0 = items[0].Gray!.Cols;
        double Area(double[,] p) => (keep.Max(i => p[i, 0]) - keep.Min(i => p[i, 0]) + w0) * (keep.Max(i => p[i, 1]) - keep.Min(i => p[i, 1]) + h0);
        double spread = keep.Count > 0 ? Area(posF) / Area(prior) : 0.0;
        record["spread"] = Py.Round(spread, 3);
        string logPath = Path.Combine(outDir, "grid_stitch_log.json");
        File.WriteAllText(logPath, record.ToJsonString(JsonOut));
        if (unverified.Count > 0)
        {
            string folder = Path.Combine(outDir, "미확인_사진");
            Directory.CreateDirectory(folder);
            foreach (int i in unverified) File.Copy(items[i].Path, Path.Combine(folder, Path.GetFileName(items[i].Path)), true);
        }
        if (spread > SPREAD_LIMIT)
            throw new InvalidOperationException($"맞춘 위치로 그린 그림이 끈 위치 그림의 {F(spread, 1)}배로 퍼짐: 위치가 크게 틀린 것 같아 합성하지 않음 (grid_stitch_log.json 참고)");
        if (keep.Count > 0 && Area(posF) > MAX_OUTPUT_PX)
            throw new InvalidOperationException($"결과 그림이 {F(Area(posF) / 1e6, 0)}백만 px로 한도 {F(MAX_OUTPUT_PX / 1e6, 0)}백만 px를 넘음: 합성하지 않음 (grid_stitch_log.json 참고)");
        double alX = Py.Median(keep.Select(i => posF[i, 0] - prior[i, 0])), alY = Py.Median(keep.Select(i => posF[i, 1] - prior[i, 1]));
        var fill = unverified.Select(i => new FillRow { Item = items[i], Position = (prior[i, 0] + alX, prior[i, 1] + alY) }).ToList();
        var canvas = new JsonObject();
        var keptItems = keep.Select(i => items[i]).ToList();
        var positions = new Dictionary<int, (double X, double Y)>();
        for (int k = 0; k < keep.Count; k++) positions[k] = (posF[keep[k], 0], posF[keep[k], 1]);
        using Mat outImg = ComposeGrid(keptItems, positions, fill, canvas, step);
        record["filled_from_unverified"] = new JsonArray(fill.Select(r => (JsonNode)new JsonObject
        {
            ["name"] = r.Item.Name, ["number"] = r.Item.Name.Split('_')[1], ["position"] = new JsonArray(r.Position.X, r.Position.Y), ["filled_pixels"] = r.FilledPixels,
        }).ToArray());
        record["unverified_fill_alignment"] = new JsonArray(alX, alY);
        record["output_canvas"] = canvas;
        File.WriteAllText(logPath, record.ToJsonString(JsonOut));
        ImageIO.ImwriteUnicode(args.Output, outImg);
        Console.WriteLine($"[done] saved: {args.Output} ({outImg.Cols}x{outImg.Rows})" + (unverified.Count > 0 ? $", 미확인 사진 {unverified.Count}장은 빈 픽셀만 채움" : ""));
        return 0;
    }

    static string Key(IEnumerable<int> g) => string.Join(",", g.OrderBy(i => i));
    static string SignedF0(double v) { string s = F(v, 0); return v >= 0 && !s.StartsWith('-') ? "+" + s : s; }
    static JsonObject Obj(IEnumerable<(string K, JsonNode V)> kvs) { var o = new JsonObject(); foreach (var (k, v) in kvs) o[k] = v; return o; }
}
