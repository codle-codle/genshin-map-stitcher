using System.Numerics;
using OpenCvSharp;

namespace GenshinMap.Core.Vision;

/// <summary>
/// grid_stitch.py에서 찍기와 붙이기가 같이 쓰는 부품: 붙박이 점 빼기, 짝 맞추기, 화면 요소 배우기, 무늬 그림·촘촘 맞추기.
/// 상수 이름·값은 파이썬 그대로(왜 이 값인지는 얼린 판 주석).
/// </summary>
public static class GridParts
{
    public const double DEVIGNETTE = 0.38;
    public const int NFEATURES = 12000;
    public const double RATIO_TEST = 0.75;
    public const int WEAK_INLIERS = 12, STRONG_INLIERS = 80;
    public const double NEIGHBOR = 1.25;
    /// <summary>py: 붙이기 main이 사진 너비/2560 배로 바꾸는 전역(FIT_TOL·STILL_TOL·PRIOR_TOL)이라 static 필드</summary>
    public static double FIT_TOL = 30.0;
    public static readonly (double X, double Y) UI_BAND = (0.10, 0.16);
    public static double STILL_TOL = 2.0;
    public const int STILL_HAM = 64;
    public static readonly (int X, int Y) STILL_GRID = (8, 6);
    public const int STILL_CELL_POINTS = 3, STILL_SPREAD_MIN = 7;
    public const double UI_SCALE = 0.25;
    public const int UI_TOL = 8;
    public const double UI_SHARE = 0.85, UI_GRAD = 24.0;
    public const int UI_MIN_FRAMES = 4, UI_CLOSE = 9, UI_MIN_BLOB = 12;
    public static readonly (double W, double H) FIXED_UI = (0.1875, 0.398);
    public const double DENSE_SCALE = 0.5;
    public static readonly (double X, double Y) DENSE_REACH = (320, 200);
    public const double DENSE_MIN_MAP = 0.2, DENSE_PEAK = 0.45, DENSE_MARGIN = 0.12;
    public static readonly double DENSE_WEIGHT = Math.Sqrt(WEAK_INLIERS);

    // ---------- 붙박이 점 ----------

    /// <summary>py: in_band(pt, w, h) — 화면 테두리 띠 안인가</summary>
    public static bool InBand((double X, double Y) pt, int w, int h)
    {
        double bx = UI_BAND.X * w, by = UI_BAND.Y * h;
        return !(bx <= pt.X && pt.X < w - bx && by <= pt.Y && pt.Y < h - by);
    }

    sealed class StillGeom
    {
        public KeyPoint[] Keys = [];
        public (double X, double Y)[] Points = [];
        public Dictionary<(int, int), List<int>> Buckets = new();
        public (int[] Xs, int[] Ys)[] Cells = [];
    }

    static int FloorDiv4(double v) => (int)Py.FloorDiv(v, 4.0);

    /// <summary>py: set 두 개({a, b})를 튜플로: 파이썬 set의 순서(작은 정수는 값 순)가 결과에 영향은 없다(같은 칸 모음)</summary>
    static int[] Pair(int a, int b) => a == b ? [a] : [a, b];

    static StillGeom Geometry(ImageItem item)
    {
        if (item.StillGeometry is StillGeom c && ReferenceEquals(c.Keys, item.KeyPoints)) return c;
        var g = new StillGeom { Keys = item.KeyPoints };
        g.Points = item.KeyPoints.Select(k => ((double)k.Pt.X, (double)k.Pt.Y)).ToArray();
        for (int k = 0; k < g.Points.Length; k++)
        {
            var (x, y) = g.Points[k];
            var key = (FloorDiv4(x), FloorDiv4(y));
            if (!g.Buckets.TryGetValue(key, out var list)) g.Buckets[key] = list = new List<int>();
            list.Add(k);
        }
        g.Cells = g.Points.Select(p => (Pair(FloorDiv4(p.X - STILL_TOL), FloorDiv4(p.X + STILL_TOL)),
                                        Pair(FloorDiv4(p.Y - STILL_TOL), FloorDiv4(p.Y + STILL_TOL)))).ToArray();
        item.StillGeometry = g;
        return g;
    }

    /// <summary>py: still_points(a, b) — 화면 같은 자리(2px 안)·같은 생김새(해밍 64 이하)인 점 짝</summary>
    public static (int[] A, int[] B) StillPoints(ImageItem a, ImageItem b)
    {
        if (a.Descriptors is null || b.Descriptors is null) return ([], []);
        var ga = Geometry(a);
        var gb = Geometry(b);
        var ia = new List<int>(); var ib = new List<int>();
        for (int k = 0; k < ga.Points.Length; k++)
        {
            var (x, y) = ga.Points[k];
            foreach (int gx in ga.Cells[k].Xs)
                foreach (int gy in ga.Cells[k].Ys)
                    if (gb.Buckets.TryGetValue((gx, gy), out var list))
                        foreach (int m in list)
                        {
                            var (px, py) = gb.Points[m];
                            if (Math.Abs(px - x) <= STILL_TOL && Math.Abs(py - y) <= STILL_TOL) { ia.Add(k); ib.Add(m); }
                        }
        }
        if (ia.Count == 0) return ([], []);
        a.Descriptors.GetArray(out byte[] da);
        b.Descriptors.GetArray(out byte[] db);
        int cols = a.Descriptors.Cols;
        var ra = new List<int>(); var rb = new List<int>();
        for (int t = 0; t < ia.Count; t++)
        {
            int ham = 0;
            for (int c = 0; c < cols; c++) ham += BitOperations.PopCount((uint)(da[ia[t] * cols + c] ^ db[ib[t] * cols + c]));
            if (ham <= STILL_HAM) { ra.Add(ia[t]); rb.Add(ib[t]); }
        }
        return (ra.ToArray(), rb.ToArray());
    }

    /// <summary>py: still_to_drop(a, b) — 매칭 전에 뺄 붙박이 점(a 번호들, b 번호들)</summary>
    public static (HashSet<int> A, HashSet<int> B) StillToDrop(ImageItem a, ImageItem b)
    {
        var (ia, ib) = StillPoints(a, b);
        if (ia.Length == 0) return (new(), new());
        int h = a.Shape.H, w = a.Shape.W;
        bool[] band = ia.Select(k => InBand((a.KeyPoints[k].Pt.X, a.KeyPoints[k].Pt.Y), w, h)).ToArray();
        var cells = new Dictionary<(int, int), int>();
        foreach (int k in ia.Where((_, t) => !band[t]).Distinct().OrderBy(k => k))
        {
            double x = a.KeyPoints[k].Pt.X, y = a.KeyPoints[k].Pt.Y;
            var c = ((int)(x * STILL_GRID.X / w), (int)(y * STILL_GRID.Y / h));
            cells[c] = cells.GetValueOrDefault(c) + 1;
        }
        if (cells.Values.Count(v => v >= STILL_CELL_POINTS) >= STILL_SPREAD_MIN)
        {
            ia = ia.Where((_, t) => band[t]).ToArray();
            ib = ib.Where((_, t) => band[t]).ToArray();
        }
        return (ia.ToHashSet(), ib.ToHashSet());
    }

    /// <summary>py: without_points(item, drop) — 특징점 일부를 뺀 사본(매칭에만)</summary>
    public static ImageItem WithoutPoints(ImageItem item, ISet<int> drop)
    {
        var keep = Enumerable.Range(0, item.KeyPoints.Length).Where(k => !drop.Contains(k)).ToArray();
        Mat? desc = null;
        if (item.Descriptors is not null && keep.Length > 0)
        {
            desc = new Mat(keep.Length, item.Descriptors.Cols, item.Descriptors.Type());
            for (int r = 0; r < keep.Length; r++) item.Descriptors.Row(keep[r]).CopyTo(desc.Row(r));
        }
        return item.With(keyPoints: keep.Select(k => item.KeyPoints[k]).ToArray(), descriptors: desc, setDescriptors: true);
    }

    /// <summary>py: match_pair(a, b, i, j, min_inliers) — 붙박이 점을 빼고 엔진 매칭</summary>
    public static PairMatch? MatchPair(ImageItem a, ImageItem b, int i, int j, int minInliers)
    {
        var (da, db) = StillToDrop(a, b);
        int h = a.Shape.H, w = a.Shape.W;
        var rect = (UI_BAND.X * w, UI_BAND.Y * h, (1 - UI_BAND.X) * w, (1 - UI_BAND.Y) * h);
        return Engine.EstimatePair(WithoutPoints(a, da), WithoutPoints(b, db), i, j, RATIO_TEST, minInliers, minInliers, rect);
    }

    /// <summary>py: offset_of(pm) — j의 위치 - i의 위치</summary>
    public static (double X, double Y) OffsetOf(PairMatch pm) => (-pm.Dx, -pm.Dy);

    // ---------- 화면 요소 ----------

    /// <summary>py: ui_small(gray)</summary>
    public static Mat UiSmall(Mat gray)
    {
        var o = new Mat();
        Cv2.Resize(gray, o, new Size(0, 0), UI_SCALE, UI_SCALE, InterpolationFlags.Area);
        return o;
    }

    /// <summary>py: learn_static_ui(smalls) — 화면 요소 마스크(0.25배, 255=요소) 또는 null(사진이 적음)</summary>
    public static Mat? LearnStaticUi(IReadOnlyList<Mat> smalls)
    {
        if (smalls.Count < UI_MIN_FRAMES) return null;
        int n = smalls.Count, h = smalls[0].Rows, w = smalls[0].Cols;
        var data = smalls.Select(s => { s.GetArray(out byte[] d); return d; }).ToArray();
        var med = new float[h * w];       // py: np.median(int16 stack) → float64, 뒤에서 float32로
        var same = new byte[h * w];
        var col = new double[n];
        for (int p = 0; p < h * w; p++)
        {
            for (int k = 0; k < n; k++) col[k] = data[k][p];
            double m = Py.Median(col);
            int cnt = 0;
            for (int k = 0; k < n; k++) if (Math.Abs(data[k][p] - m) <= UI_TOL) cnt++;
            same[p] = (double)cnt / n >= UI_SHARE ? (byte)1 : (byte)0;
            med[p] = (float)m;
        }
        using var medMat = Mat.FromPixelData(h, w, MatType.CV_32FC1, med);
        using Mat blur = new Mat(), gx = new Mat(), gy = new Mat(), grad = new Mat();
        Cv2.GaussianBlur(medMat, blur, new Size(0, 0), 1.0);
        Cv2.Sobel(blur, gx, MatType.CV_32F, 1, 0);
        Cv2.Sobel(blur, gy, MatType.CV_32F, 0, 1);
        Cv2.Magnitude(gx, gy, grad);
        grad.GetArray(out float[] g);
        var core = new byte[h * w];
        for (int p = 0; p < h * w; p++) core[p] = same[p] == 1 && g[p] >= UI_GRAD ? (byte)1 : (byte)0;
        using var coreMat = Mat.FromPixelData(h, w, MatType.CV_8UC1, core);
        using Mat labels = new Mat(), stats = new Mat(), cents = new Mat();
        int nl = Cv2.ConnectedComponentsWithStats(coreMat, labels, stats, cents, PixelConnectivity.Connectivity8);
        var keepLabel = new bool[nl];
        for (int k = 1; k < nl; k++) keepLabel[k] = stats.At<int>(k, (int)ConnectedComponentsTypes.Area) >= UI_MIN_BLOB;
        labels.GetArray(out int[] lab);
        var keep = new byte[h * w];
        for (int p = 0; p < h * w; p++) keep[p] = keepLabel[lab[p]] ? (byte)1 : (byte)0;
        using var keepMat = Mat.FromPixelData(h, w, MatType.CV_8UC1, keep);
        using Mat closed = new Mat(), dil = new Mat();
        using (Mat k9 = Mat.Ones(UI_CLOSE, UI_CLOSE, MatType.CV_8UC1))
            Cv2.MorphologyEx(keepMat, closed, MorphTypes.Close, k9, null, 1, BorderTypes.Constant, Cv2.MorphologyDefaultBorderValue());
        using (Mat k3 = Mat.Ones(3, 3, MatType.CV_8UC1))
            Cv2.Dilate(closed, dil, k3, null, 1, BorderTypes.Constant, Cv2.MorphologyDefaultBorderValue());
        var outMask = new Mat();
        Cv2.Compare(dil, 0, outMask, CmpTypes.GT);
        return outMask;
    }

    /// <summary>py: fixed_ui_small(shape) — 0.25배 크기 고정 금지 구역(255=구역)</summary>
    public static Mat FixedUiSmall(int h, int w)
    {
        var m = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        int rh = Math.Min(h, Py.RoundEvenInt(FIXED_UI.H * h)), rw = Math.Min(w, Py.RoundEvenInt(FIXED_UI.W * w));
        if (rh > 0 && rw > 0) m[new Rect(0, 0, rw, rh)].SetTo(255);
        return m;
    }

    /// <summary>py: with_fixed_ui(mask, shape)</summary>
    public static Mat WithFixedUi(Mat? mask, int h, int w)
    {
        Mat fixedUi = FixedUiSmall(h, w);
        if (mask is null || mask.Rows != h || mask.Cols != w) return fixedUi;
        var o = new Mat();
        Cv2.BitwiseOr(mask, fixedUi, o);
        fixedUi.Dispose();
        return o;
    }

    /// <summary>py: ui_full(mask, w, h) — 0.25배 마스크를 원래 크기로(255=요소)</summary>
    public static Mat UiFull(Mat mask, int w, int h)
    {
        using Mat r = new Mat();
        Cv2.Resize(mask, r, new Size(w, h), 0, 0, InterpolationFlags.Nearest);
        var o = new Mat();
        Cv2.Compare(r, 0, o, CmpTypes.GT);
        return o;
    }

    /// <summary>py: ui_points(item, full) — 화면 요소 자리에 있는 특징점 번호</summary>
    public static HashSet<int> UiPoints(ImageItem item, Mat full)
    {
        full.GetArray(out byte[] f);
        int fh = full.Rows, fw = full.Cols;
        var s = new HashSet<int>();
        for (int k = 0; k < item.KeyPoints.Length; k++)
        {
            var pt = item.KeyPoints[k].Pt;
            int y = Math.Min(fh - 1, (int)pt.Y), x = Math.Min(fw - 1, (int)pt.X);
            if (f[y * fw + x] != 0) s.Add(k);
        }
        return s;
    }

    /// <summary>py: apply_static_ui(item, full) — 화면 요소 자리 특징점을 빼고 매칭·결과 가림에도 넣은 사본</summary>
    public static ImageItem ApplyStaticUi(ImageItem item, Mat full)
    {
        var o = WithoutPoints(item, UiPoints(item, full));
        Mat mm = item.MatchMask!.Clone(), om = item.OutputMask!.Clone();
        mm.SetTo(0, full);
        om.SetTo(0, full);
        return o.With(matchMask: mm, outputMask: om);
    }

    /// <summary>py: box_static_ui(mask, step, moving) — 테두리에 붙은 화면 요소 덩어리를 사각형으로 채운다(출력 사각형이 남을 때만)</summary>
    public static Mat? BoxStaticUi(Mat? mask, (double X, double Y) step, Mat? moving = null)
    {
        if (mask is null) return null;
        int h = mask.Rows, w = mask.Cols;
        mask.GetArray(out byte[] src);
        bool[] mov = new bool[h * w];
        if (moving is not null) { moving.GetArray(out byte[] mv); for (int p = 0; p < mov.Length; p++) mov[p] = mv[p] != 0; }
        bool[] outM = new bool[h * w];
        for (int p = 0; p < outM.Length; p++) outM[p] = src[p] != 0 && !mov[p];
        int pad = (int)Math.Ceiling(24 * UI_SCALE);
        bool[] baseM;
        using (var gray128 = new Mat(h, w, MatType.CV_8UC3, Scalar.All(128)))
        using (Mat bm = Engine.MakeMask(gray128, "genshin", [], purpose: "output", uiPad: 6))
        using (Mat fx = FixedUiSmall(h, w))
        {
            bm.GetArray(out byte[] b); fx.GetArray(out byte[] fb);
            baseM = new bool[h * w];
            for (int p = 0; p < baseM.Length; p++) baseM[p] = b[p] > 0 && fb[p] == 0;
        }
        int reqw = (int)Math.Ceiling((step.X + 64) * UI_SCALE), reqh = (int)Math.Ceiling((step.Y + 64) * UI_SCALE);

        bool Enough(bool[] free)
        {
            var heights = new int[w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) heights[x] = free[y * w + x] ? heights[x] + 1 : 0;
                int run = 0, best = 0;
                for (int x = 0; x < w; x++) { run = heights[x] >= reqh ? run + 1 : 0; if (run > best) best = run; }
                if (best > 0 && best >= reqw) return true;
            }
            return false;
        }

        using var outMat = new Mat(h, w, MatType.CV_8UC1);
        outMat.SetArray(outM.Select(v => v ? (byte)1 : (byte)0).ToArray());
        using Mat labels = new Mat(), stats = new Mat(), cents = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(outMat, labels, stats, cents, PixelConnectivity.Connectivity8);
        for (int k = 1; k < n; k++)
        {
            int x = stats.At<int>(k, 0), y = stats.At<int>(k, 1), bw = stats.At<int>(k, 2), bh = stats.At<int>(k, 3);
            if (Math.Min(Math.Min(x, y), Math.Min(w - x - bw, h - y - bh)) > pad) continue;
            var cand = (bool[])outM.Clone();
            var (r0, r1) = Py.Slice(Math.Max(0, y - pad), Math.Min(h, y + bh + pad), h);
            var (c0, c1) = Py.Slice(Math.Max(0, x - pad), Math.Min(w, x + bw + pad), w);
            for (int yy = r0; yy < r1; yy++) for (int xx = c0; xx < c1; xx++) cand[yy * w + xx] = true;
            if (moving is not null) for (int p = 0; p < cand.Length; p++) cand[p] &= !mov[p];
            var free = new bool[h * w];
            for (int p = 0; p < free.Length; p++) free[p] = baseM[p] && !cand[p];
            if (Enough(free)) outM = cand;
        }
        var res = new Mat(h, w, MatType.CV_8UC1);
        res.SetArray(outM.Select(v => v ? (byte)255 : (byte)0).ToArray());
        return res;
    }

    // ---------- 무늬 그림·촘촘 맞추기 ----------

    /// <summary>py: 모듈 전역 _gradients — (path, name) → (float16로 줄여 둔 무늬 그림, 지도 자리)</summary>
    static readonly Dictionary<(string, string), (Half[] G, bool[] Valid, int H, int W)> gradients = new();

    public static void ClearGradientCache() => gradients.Clear();

    /// <summary>py: map_gradient(item) — 반 크기 밝기 기울기(화면 요소·빈 하늘은 0)와 쓸 수 있는 자리. 처음 한 번 만들어 둔다.</summary>
    public static (float[] G, bool[] Valid, int H, int W) MapGradient(ImageItem item)
    {
        var key = (item.Path, item.Name);
        if (!gradients.TryGetValue(key, out var entry))
        {
            double s = DENSE_SCALE;
            using Mat small = new Mat(), f = new Mat(), blur = new Mat(), gx = new Mat(), gy = new Mat(), g = new Mat();
            Cv2.Resize(item.Gray!, small, new Size(0, 0), s, s, InterpolationFlags.Area);
            small.ConvertTo(f, MatType.CV_32F);
            Cv2.GaussianBlur(f, blur, new Size(0, 0), 1.5);
            Cv2.Sobel(blur, gx, MatType.CV_32F, 1, 0);
            Cv2.Sobel(blur, gy, MatType.CV_32F, 0, 1);
            Cv2.Magnitude(gx, gy, g);
            int gh = g.Rows, gw = g.Cols;
            using Mat img = new Mat(), hsv = new Mat(), mm = new Mat();
            Cv2.Resize(item.Image!, img, new Size(gw, gh), 0, 0, InterpolationFlags.Area);
            Cv2.CvtColor(img, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.Resize(item.MatchMask!, mm, new Size(gw, gh), 0, 0, InterpolationFlags.Nearest);
            hsv.GetArray(out Vec3b[] hv);
            mm.GetArray(out byte[] mv);
            g.GetArray(out float[] gv);
            var valid = new bool[gh * gw];
            var half = new Half[gh * gw];
            for (int p = 0; p < valid.Length; p++)
            {
                byte S = hv[p].Item1, V = hv[p].Item2;
                bool land = V > 80 || (V >= 58 && S >= 150);
                valid[p] = land && mv[p] > 127;
                half[p] = (Half)(valid[p] ? gv[p] : 0f);
            }
            entry = (half, valid, gh, gw);
            gradients[key] = entry;
        }
        var outG = new float[entry.G.Length];
        for (int p = 0; p < outG.Length; p++) outG[p] = (float)entry.G[p];
        return (outG, entry.Valid, entry.H, entry.W);
    }

    /// <summary>py: forget(item) — 무늬 그림·붙박이 점 캐시를 버린다</summary>
    public static void Forget(ImageItem item)
    {
        gradients.Remove((item.Path, item.Name));
        item.StillGeometry = null;
    }

    /// <summary>py: dense_offset(a, b, guess) — b 위치 - a 위치를 guess 둘레에서 무늬로: ((ox, oy), 맞음 정도, 두 번째) 또는 null</summary>
    public static ((double X, double Y) Offset, double Peak, double Second)? DenseOffset(ImageItem a, ImageItem b, (double X, double Y) guess)
    {
        double s = DENSE_SCALE;
        var ga = MapGradient(a);
        var gb = MapGradient(b);
        int h = gb.H, w = gb.W;
        double rx = DENSE_REACH.X * s, ry = DENSE_REACH.Y * s;
        double gx = guess.X * s, gy = guess.Y * s;
        double u1 = Py.Max(UI_BAND.X * w, rx - gx), u2 = Py.Min((1 - UI_BAND.X) * w, w - rx - gx);
        double v1 = Py.Max(UI_BAND.Y * h, ry - gy), v2 = Py.Min((1 - UI_BAND.Y) * h, h - ry - gy);
        if (u2 - u1 < 100 || v2 - v1 < 60) return null;
        int U1 = (int)Py.CeilToLong(u1) + 1, U2 = (int)u2 - 1, V1 = (int)Py.CeilToLong(v1) + 1, V2 = (int)v2 - 1;
        // py: valid_b[v1:v2, u1:u2].mean() < DENSE_MIN_MAP
        var (vr0, vr1) = Py.Slice(V1, V2, h);
        var (vc0, vc1) = Py.Slice(U1, U2, w);
        long cnt = 0, tot = (long)(vr1 - vr0) * (vc1 - vc0);
        for (int y = vr0; y < vr1; y++) for (int x = vc0; x < vc1; x++) if (gb.Valid[y * w + x]) cnt++;
        double meanValid = tot == 0 ? double.NaN : (double)cnt / tot;
        if (meanValid < DENSE_MIN_MAP) return null;
        int th = vr1 - vr0, tw = vc1 - vc0;
        using var tpl = new Mat(th, tw, MatType.CV_32FC1);
        var tbuf = new float[th * tw];
        for (int y = 0; y < th; y++) Array.Copy(gb.G, (vr0 + y) * w + vc0, tbuf, y * tw, tw);
        tpl.SetArray(tbuf);
        int sx = Math.Max(0, Py.RoundEvenInt(U1 + gx - rx)), sy = Math.Max(0, Py.RoundEvenInt(V1 + gy - ry));
        var (ar0, ar1) = Py.Slice(sy, sy + (V2 - V1) + (int)(2 * ry), ga.H);
        var (ac0, ac1) = Py.Slice(sx, sx + (U2 - U1) + (int)(2 * rx), ga.W);
        int ah = ar1 - ar0, aw = ac1 - ac0;
        if (ah <= th || aw <= tw) return null;
        using var area = new Mat(ah, aw, MatType.CV_32FC1);
        var abuf = new float[ah * aw];
        for (int y = 0; y < ah; y++) Array.Copy(ga.G, (ar0 + y) * ga.W + ac0, abuf, y * aw, aw);
        area.SetArray(abuf);
        using Mat res = new Mat();
        Cv2.MatchTemplate(area, tpl, res, TemplateMatchModes.CCoeffNormed);
        // 지도가 적은 자리(가림·빈 하늘 위)는 맞음 정도가 1로 튀어서 뺀다
        using Mat nz = new Mat(), sNz = new Mat();
        Cv2.Compare(area, 0, nz, CmpTypes.GT);
        using (Mat nz1 = new Mat()) { nz.ConvertTo(nz1, MatType.CV_8U, 1.0 / 255); Cv2.Integral(nz1, sNz); }
        int rh = res.Rows, rw = res.Cols;
        sNz.GetArray(out int[] si);
        int sw = sNz.Cols;
        res.GetArray(out float[] rv);
        double area1 = (double)(th * tw);
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                long c = (long)si[(th + y) * sw + tw + x] - si[y * sw + tw + x] - si[(th + y) * sw + x] + si[y * sw + x];
                if (c / area1 < DENSE_MIN_MAP) rv[y * rw + x] = -1.0f;
            }
        res.SetArray(rv);
        Cv2.MinMaxLoc(res, out _, out double peak, out _, out Point loc);
        using Mat rest = res.Clone();
        Cv2.Rectangle(rest, new Point(loc.X - 12, loc.Y - 12), new Point(loc.X + 12, loc.Y + 12), Scalar.All(-1.0), -1);
        Cv2.MinMaxLoc(rest, out _, out double second);
        double ox = (sx + loc.X - U1) / s, oy = (sy + loc.Y - V1) / s;
        return ((ox, oy), (double)(float)peak, (double)(float)second);
    }
}
