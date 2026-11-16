using System.Globalization;
using System.Text;
using GenshinMap.Core.Vision;
using OpenCvSharp;

namespace GenshinMap.Core.Stitch;

/// <summary>
/// 직접 넣은 스크린샷 붙이기(엔진 모드). py: 엔진/genshin_map_stitcher.py의 build_pair_matches·place_images·compose·main.
/// 앱의 'engine'(직접 넣은 사진)·'wide'(옛 자동 캡처) 모드가 같은 exe를 'engine' 인자로 자식 프로세스로 띄운다.
/// 출력 줄([input]·[devignette]·[features]·[pair-search]·[match]·[place]·[done])은 파이썬과 같은 꼴(앱이 줄로 진행을 읽는다).
/// 안 옮긴 것: --auto-devignette(앱이 안 씀), --debug 그림.
/// </summary>
public static class EngineStitch
{
    sealed class Args
    {
        public string? InputDir;
        public string Output = "stitched.png";
        public string MaskPreset = "genshin";
        public List<(int, int, int, int)> Exclude = new(), OutputExclude = new();
        public int OutputUiPad = 25, NFeatures = 12000, MinGood = 60, MinInliers = 80, MaxPairGap = 0, MinGapBeforeStop = 3;
        public double Ratio = 0.75, Devignette = 0.0, DevignettePower = 2.0, DevignetteStart = 0.0;
        public string PairSearch = "progressive";
        public bool FallbackAllPairs, NoOutputMask;
    }

    static string F(double v, int d) => v.ToString("F" + d, CultureInfo.InvariantCulture);
    static void Say(string s) { Console.WriteLine(s); Console.Out.Flush(); }

    public static int Main(string[] a)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = new UTF8Encoding(false);
        var g = new Args();
        try
        {
            for (int i = 0; i < a.Length; i++)
            {
                string Next() => a[++i];
                int I() => int.Parse(Next(), CultureInfo.InvariantCulture);
                double D() => double.Parse(Next(), CultureInfo.InvariantCulture);
                switch (a[i])
                {
                    case "-o": case "--output": g.Output = Next(); break;
                    case "--mask-preset": g.MaskPreset = Next(); break;
                    case "--exclude": g.Exclude.Add(Engine.ParseRect(Next())); break;
                    case "--output-exclude": g.OutputExclude.Add(Engine.ParseRect(Next())); break;
                    case "--output-ui-pad": g.OutputUiPad = I(); break;
                    case "--nfeatures": g.NFeatures = I(); break;
                    case "--ratio": g.Ratio = D(); break;
                    case "--min-good": g.MinGood = I(); break;
                    case "--min-inliers": g.MinInliers = I(); break;
                    case "--pair-search": g.PairSearch = Next(); break;
                    case "--max-pair-gap": g.MaxPairGap = I(); break;
                    case "--min-gap-before-stop": g.MinGapBeforeStop = I(); break;
                    case "--fallback-all-pairs": g.FallbackAllPairs = true; break;
                    case "--no-output-mask": g.NoOutputMask = true; break;
                    case "--devignette": g.Devignette = D(); break;
                    case "--devignette-power": g.DevignettePower = D(); break;
                    case "--devignette-start": g.DevignetteStart = D(); break;
                    case "--auto-devignette": throw new ArgumentException("--auto-devignette는 C# 판에 없어요(앱이 안 씀)");
                    case "--debug": Next(); break; // 디버그 그림은 안 옮김
                    default:
                        if (g.InputDir is null && !a[i].StartsWith('-')) g.InputDir = a[i];
                        else throw new ArgumentException($"모르는 인자: {a[i]}");
                        break;
                }
            }
            if (g.InputDir is null) throw new ArgumentException("쓰기: engine <사진 폴더> -o stitched.png [선택지...]");
            Run(g);
            return 0;
        }
        catch (Exception e)
        {
            // 파이썬은 잡지 않은 예외의 마지막 줄 'RuntimeError: …'가 남고 앱이 그 줄을 보여 준다(server.last_error_line·friendly_error)
            string kind = e is ArgumentException ? "ValueError" : e is FileNotFoundException ? "FileNotFoundError" : "RuntimeError";
            Console.Out.Flush();
            Console.Error.WriteLine($"{kind}: {e.Message}");
            return 1;
        }
    }

    static void Run(Args g)
    {
        var loadedRaw = Engine.LoadImages(g.InputDir!);
        Say($"[input] {loadedRaw.Count} images");
        var loaded = loadedRaw;
        if (g.Devignette > 0)
        {
            if (g.DevignettePower != 2.0 || g.DevignetteStart != 0.0) throw new ArgumentException("--devignette-power·--devignette-start는 기본값만 돼요");
            Say($"[devignette] strength={F(g.Devignette, 3)}, power={F(g.DevignettePower, 2)}, start={F(g.DevignetteStart, 2)}");
            loaded = loadedRaw.Select(t => (t.Path, Engine.RemoveVignette(t.Img, g.Devignette))).ToList();
            foreach (var t in loadedRaw) t.Img.Dispose();
        }
        var items = Engine.ExtractFeatures(loaded, g.MaskPreset, g.Exclude, g.NFeatures, g.OutputExclude, g.OutputUiPad);
        foreach (var it in items) Say($"[features] {it.Name}: {it.KeyPoints.Length} keypoints");
        var matches = BuildPairMatches(items, g);
        if (matches.Count == 0) throw new InvalidOperationException("No valid image pairs found. Try --max-pair-gap 12, --pair-search all, or add more overlap.");
        var positions = PlaceImages(items.Count, matches);
        using var output = Compose(items, positions, useMaskForOutput: !g.NoOutputMask);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(g.Output));
        if (dir is not null) Directory.CreateDirectory(dir);
        ImageIO.ImwriteUnicode(g.Output, output);
        Say($"[done] saved: {g.Output} ({output.Cols}x{output.Rows})");
    }

    /// <summary>py: _component_count(n, matches)</summary>
    static int ComponentCount(int n, List<PairMatch> matches)
    {
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var m in matches) { int ra = Find(m.I), rb = Find(m.J); if (ra != rb) parent[rb] = ra; }
        return Enumerable.Range(0, n).Select(Find).Distinct().Count();
    }

    static string MatchLine(ImageItem a, ImageItem b, PairMatch? pm) => pm is null
        ? $"[match] {a.Name} -> {b.Name}: failed"
        : $"[match] {a.Name} -> {b.Name}: dx={F(pm.Dx, 1)}, dy={F(pm.Dy, 1)}, inliers={pm.Inliers}, score={F(pm.Score, 1)}";

    /// <summary>py: build_pair_matches(items, args) — 'progressive'는 이름 순 가까운 쌍부터, 다 이어지면 일찍 멈춘다</summary>
    static List<PairMatch> BuildPairMatches(List<ImageItem> items, Args g)
    {
        var matches = new List<PairMatch>();
        int n = items.Count;
        PairMatch? Pair(int i, int j)
        {
            var pm = Engine.EstimatePair(items[i], items[j], i, j, g.Ratio, g.MinGood, g.MinInliers);
            if (pm is not null) matches.Add(pm);
            Say(MatchLine(items[i], items[j], pm));
            return pm;
        }
        if (g.PairSearch == "all")
        {
            Say($"[pair-search] all pairs: {n * (n - 1) / 2} pairs");
            for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) Pair(i, j);
            return matches;
        }
        if (g.PairSearch != "progressive") throw new ArgumentException($"Unknown --pair-search: {g.PairSearch}");
        int maxPairGap;
        if (g.MaxPairGap <= 0) { maxPairGap = n - 1; Say("[pair-search] progressive with no hard gap limit"); }
        else { maxPairGap = Math.Min(g.MaxPairGap, n - 1); Say($"[pair-search] progressive with hard max gap={maxPairGap}"); }
        maxPairGap = Math.Max(1, maxPairGap);
        int minGap = Math.Max(1, Math.Min(g.MinGapBeforeStop, maxPairGap));
        Say($"[pair-search] progressive by filename/order gap: gap 1..{maxPairGap}, stop check after gap {minGap}");
        int tested = 0;
        for (int gap = 1; gap <= maxPairGap; gap++)
        {
            Say($"[pair-search] gap={gap}");
            for (int i = 0; i < n - gap; i++) { tested++; Pair(i, i + gap); }
            int comps = ComponentCount(n, matches);
            Say($"[pair-search] after gap={gap}: tested={tested}, valid={matches.Count}, components={comps}");
            if (gap >= minGap && comps == 1)
            {
                Say($"[pair-search] connected; stopping early at gap={gap}");
                return matches;
            }
        }
        int c2 = ComponentCount(n, matches);
        Say($"[pair-search] reached max gap. tested={tested}, valid={matches.Count}, components={c2}");
        if (c2 != 1 && g.FallbackAllPairs)
        {
            Say("[pair-search] fallback all-pairs enabled. Comparing remaining far pairs...");
            var already = matches.Select(m => (Math.Min(m.I, m.J), Math.Max(m.I, m.J))).ToHashSet();
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (j - i <= maxPairGap || already.Contains((i, j))) continue;
                    tested++;
                    Pair(i, j);
                    if (ComponentCount(n, matches) == 1) { Say("[pair-search] connected during fallback; stopping"); return matches; }
                }
        }
        return matches;
    }

    /// <summary>py: place_images(n, matches) — 점수 큰 짝부터 최대 신장 나무, 0번에서 깊이 우선으로 자리</summary>
    static Dictionary<int, (double X, double Y)> PlaceImages(int n, List<PairMatch> matches)
    {
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var mst = new List<PairMatch>();
        foreach (var m in matches.OrderByDescending(m => m.Score)) // py: sorted(reverse=True)도 안정 정렬
        {
            int ra = Find(m.I), rb = Find(m.J);
            if (ra == rb) continue;
            parent[rb] = ra;
            mst.Add(m);
        }
        if (mst.Count != n - 1)
        {
            int connected = Enumerable.Range(0, n).Select(Find).Distinct().Count();
            throw new InvalidOperationException($"Could not connect all images. Connected components: {connected}. Try more overlap, more images, or lower --min-inliers.");
        }
        var adj = Enumerable.Range(0, n).ToDictionary(i => i, _ => new List<(int, double, double)>());
        foreach (var m in mst)
        {
            adj[m.I].Add((m.J, -m.Dx, -m.Dy)); // point_j = point_i + d → pos_j = pos_i - d
            adj[m.J].Add((m.I, m.Dx, m.Dy));
        }
        var pos = new Dictionary<int, (double X, double Y)> { [0] = (0.0, 0.0) };
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            var (cx, cy) = pos[cur];
            foreach (var (nxt, ox, oy) in adj[cur])
            {
                if (pos.ContainsKey(nxt)) continue;
                pos[nxt] = (cx + ox, cy + oy);
                stack.Push(nxt);
            }
        }
        return pos;
    }

    /// <summary>py: compose(items, positions, use_mask_for_output) — 가운데가 센 깃털 가중 평균(float32), 빈 테두리 자름</summary>
    static Mat Compose(List<ImageItem> items, Dictionary<int, (double X, double Y)> positions, bool useMaskForOutput)
    {
        int h = items[0].Image!.Rows, w = items[0].Image!.Cols;
        double minXd = double.PositiveInfinity, minYd = double.PositiveInfinity, maxXd = double.NegativeInfinity, maxYd = double.NegativeInfinity;
        foreach (var (x, y) in positions.Values)
        {
            minXd = Math.Min(minXd, x); minYd = Math.Min(minYd, y);
            maxXd = Math.Max(maxXd, x + w); maxYd = Math.Max(maxYd, y + h);
        }
        int minX = (int)Math.Floor(minXd), minY = (int)Math.Floor(minYd), maxX = (int)Math.Ceiling(maxXd), maxY = (int)Math.Ceiling(maxYd);
        int outW = maxX - minX, outH = maxY - minY;
        using var acc = new Mat(outH, outW, MatType.CV_32FC3, Scalar.All(0));
        using var weight = new Mat(outH, outW, MatType.CV_32FC1, Scalar.All(0));
        for (int idx = 0; idx < items.Count; idx++)
        {
            var item = items[idx];
            int px = Py.RoundEvenInt(positions[idx].X - minX), py = Py.RoundEvenInt(positions[idx].Y - minY);
            item.OutputMask!.GetArray(out byte[] om);
            var valid = new float[h * w];
            for (int p = 0; p < valid.Length; p++) valid[p] = useMaskForOutput ? om[p] / 255.0f : 1f;
            var bin = new byte[h * w];
            for (int p = 0; p < bin.Length; p++) bin[p] = valid[p] > 0.5f ? (byte)1 : (byte)0;
            float[] dist;
            using (var binM = Mat.FromPixelData(h, w, MatType.CV_8UC1, bin))
            using (var distM = new Mat())
            {
                Cv2.DistanceTransform(binM, distM, DistanceTypes.L2, DistanceTransformMasks.Mask3);
                distM.GetArray(out dist);
            }
            float dmax = 0f;
            foreach (float d in dist) if (d > dmax) dmax = d;
            var feather = new float[h * w];
            for (int p = 0; p < feather.Length; p++)
            {
                float d = dmax > 0 ? dist[p] / dmax : dist[p];
                float c = d * 2.5f;
                c = c < 0.15f ? 0.15f : c > 1.0f ? 1.0f : c; // py: np.clip(dist*2.5, 0.15, 1.0) — float32
                feather[p] = c * valid[p];
            }
            using var fM = Mat.FromPixelData(h, w, MatType.CV_32FC1, feather);
            using var imgF = new Mat();
            item.Image!.ConvertTo(imgF, MatType.CV_32FC3);
            using var f3 = new Mat();
            Cv2.Merge([fM, fM, fM], f3);
            using var prod = new Mat();
            Cv2.Multiply(imgF, f3, prod);
            var roi = new Rect(px, py, w, h);
            using (var a = new Mat(acc, roi)) Cv2.Add(a, prod, a);
            using (var wt = new Mat(weight, roi)) Cv2.Add(wt, fM, wt);
            Say($"[place] {item.Name}: x={F(positions[idx].X, 1)}, y={F(positions[idx].Y, 1)}");
        }
        // py: out[valid] = clip(acc[valid] / weight[valid], 0, 255).astype(uint8) — 한 줄씩(큰 그림이라 줄 단위로)
        var output = new Mat(outH, outW, MatType.CV_8UC3, Scalar.All(0));
        int r0 = -1, r1 = -1, c0 = int.MaxValue, c1 = -1;
        var rowO = new byte[outW * 3];
        for (int y = 0; y < outH; y++)
        {
            Vec3f[] arow; float[] wrow;
            using (var arM = acc.Row(y)) arM.GetArray(out arow);
            using (var wrM = weight.Row(y)) wrM.GetArray(out wrow);
            Array.Clear(rowO);
            bool any = false;
            for (int x = 0; x < outW; x++)
            {
                float wv = wrow[x];
                if (!(wv > 1e-6f)) continue;
                any = true;
                if (x < c0) c0 = x;
                if (x > c1) c1 = x;
                for (int ch = 0; ch < 3; ch++)
                {
                    float q = arow[x][ch] / wv;
                    q = q < 0 ? 0 : q > 255 ? 255 : q;
                    rowO[x * 3 + ch] = (byte)q;
                }
            }
            if (any) { if (r0 < 0) r0 = y; r1 = y; }
            System.Runtime.InteropServices.Marshal.Copy(rowO, 0, output.Ptr(y), outW * 3);
        }
        if (r0 >= 0 && c1 >= 0)
        {
            var cropped = new Mat(output, new Rect(c0, r0, c1 - c0 + 1, r1 - r0 + 1)).Clone();
            output.Dispose();
            return cropped;
        }
        return output;
    }
}
