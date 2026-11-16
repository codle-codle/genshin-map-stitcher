using GenshinMap.Core.Capture;
using OpenCvSharp;

namespace GenshinMap.Core.Vision;

/// <summary>
/// 배운 화면 요소. py: auto_capture.STATIC_UI(모듈 전역 사전) — small = 화면 전체 0.25배, full = 원래 크기, content = small_content 크기.
/// C#은 찍기 한 판이 객체 하나를 들고 다닌다. 마스크는 0/255.
/// </summary>
public sealed class StaticUiState
{
    public Mat? Small, Full, Content;
}

/// <summary>py: auto_capture의 그림 판단 함수(밝은 점·가장자리·움직임·겹친 곳 비교·이동 재기)</summary>
public static class CaptureVision
{
    public const double VOID_MAX = 0.02, EDGE_STRIP = 0.12, EDGE_MIN = 0.01;
    public const int SEA_V_MIN = 58, SEA_S_MIN = 150;
    public const double STILL_DIFF = 6.0, FULL_MOVE_DIFF = 12.0, SHARP_MARGIN = 3.0;

    /// <summary>py: set_static_ui(mask, w, h). mask: 배운 화면 요소(0.25배, 0이 아니면 요소) 또는 null</summary>
    public static void SetStaticUi(StaticUiState st, Mat? mask, int w, int h)
    {
        int sh = Py.RoundEvenInt(h * GridParts.UI_SCALE), sw = Py.RoundEvenInt(w * GridParts.UI_SCALE);
        Mat? learned = mask is not null && mask.Rows == sh && mask.Cols == sw ? mask : null;
        using Mat withFixed = GridParts.WithFixedUi(learned, sh, sw);
        using Mat zoned = GridParts.UiFull(withFixed, w, h);
        var (x1, y1, x2, y2) = Geometry.ContentBox(w, h);
        using Mat crop = new Mat(zoned, new Rect(x1, y1, x2 - x1, y2 - y1));
        using Mat small = new Mat();
        Cv2.Resize(crop, small, new Size(0, 0), 0.25, 0.25, InterpolationFlags.Nearest);
        var content = new Mat();
        Cv2.Compare(small, 0, content, CmpTypes.GT);
        st.Small = learned?.Clone();
        st.Full = learned is null ? null : GridParts.UiFull(learned, w, h);
        st.Content = content;
    }

    /// <summary>py: small_content(image) — 지도 영역만 0.25배</summary>
    public static Mat SmallContent(Mat image)
    {
        var (x1, y1, x2, y2) = Geometry.ContentBox(image.Cols, image.Rows);
        using Mat crop = new Mat(image, new Rect(x1, y1, x2 - x1, y2 - y1));
        var o = new Mat();
        Cv2.Resize(crop, o, new Size(0, 0), 0.25, 0.25, InterpolationFlags.Area);
        return o;
    }

    /// <summary>py: without_ui(m) — 화면 요소 자리를 뺀다(제자리)</summary>
    static void WithoutUi(StaticUiState st, byte[] m, int rows, int cols)
    {
        var u = st.Content;
        if (u is null || u.Rows != rows || u.Cols != cols) return;
        u.GetArray(out byte[] uv);
        for (int p = 0; p < m.Length; p++) if (uv[p] != 0) m[p] = 0;
    }

    /// <summary>py: bright_mask(image) — 지도 점(밝은 점 또는 진한 먼바다), 화면 요소 자리는 뺌. 0/1 바이트, (행, 열)</summary>
    public static byte[] BrightMask(StaticUiState st, Mat image, out int rows, out int cols)
    {
        using Mat small = SmallContent(image), hsv = new Mat();
        Cv2.CvtColor(small, hsv, ColorConversionCodes.BGR2HSV);
        hsv.GetArray(out Vec3b[] hv);
        rows = small.Rows; cols = small.Cols;
        var m = new byte[hv.Length];
        for (int p = 0; p < m.Length; p++)
        {
            byte S = hv[p].Item1, V = hv[p].Item2;
            m[p] = V > 80 || (V >= SEA_V_MIN && S >= SEA_S_MIN) ? (byte)1 : (byte)0;
        }
        WithoutUi(st, m, rows, cols);
        return m;
    }

    static double Mean(byte[] m) { long c = 0; foreach (byte v in m) c += v; return (double)c / m.Length; }

    /// <summary>py: bright_ratio(image)</summary>
    public static double BrightRatio(StaticUiState st, Mat image) => Mean(BrightMask(st, image, out _, out _));

    /// <summary>py: edges_of(image) — 방향(right, left, down, up)마다 가장자리 띠에 지도가 닿나</summary>
    public static bool[] EdgesOf(StaticUiState st, Mat image)
    {
        byte[] m = BrightMask(st, image, out int r, out int c);
        int kh = Math.Max(1, (int)(r * EDGE_STRIP)), kw = Math.Max(1, (int)(c * EDGE_STRIP));
        double Strip(int r0, int r1, int c0, int c1)
        {
            var (y0, y1) = Py.Slice(r0, r1, r);
            var (x0, x1) = Py.Slice(c0, c1, c);
            long s = 0, n = (long)(y1 - y0) * (x1 - x0);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) s += m[y * c + x];
            return n == 0 ? double.NaN : (double)s / n;
        }
        return [Strip(0, r, -kw, c) >= EDGE_MIN, Strip(0, r, 0, kw) >= EDGE_MIN, Strip(-kh, r, 0, c) >= EDGE_MIN, Strip(0, kh, 0, c) >= EDGE_MIN];
    }

    /// <summary>py: map_moved(prev, cur) — true / false(안 움직임) / null(판단 못 함)</summary>
    public static bool? MapMoved(StaticUiState st, Mat prev, Mat cur)
    {
        double pb = BrightRatio(st, prev), cb = BrightRatio(st, cur);
        if ((pb >= VOID_MAX) != (cb >= VOID_MAX)) return true;
        if (pb < VOID_MAX) return null;
        using Mat a = SmallContent(prev), b = SmallContent(cur), ha = new Mat(), hb = new Mat();
        Cv2.CvtColor(a, ha, ColorConversionCodes.BGR2HSV);
        Cv2.CvtColor(b, hb, ColorConversionCodes.BGR2HSV);
        ha.GetArray(out Vec3b[] hva); hb.GetArray(out Vec3b[] hvb);
        var bright = new byte[hva.Length];
        for (int p = 0; p < bright.Length; p++) bright[p] = hva[p].Item2 > 80 || hvb[p].Item2 > 80 ? (byte)1 : (byte)0;
        WithoutUi(st, bright, a.Rows, a.Cols);
        if (Mean(bright) < VOID_MAX) return null;
        a.GetArray(out Vec3b[] va); b.GetArray(out Vec3b[] vb);
        var sel = new List<double>();
        for (int p = 0; p < bright.Length; p++)
        {
            if (bright[p] == 0) continue;
            int d0 = Math.Abs(va[p].Item0 - vb[p].Item0), d1 = Math.Abs(va[p].Item1 - vb[p].Item1), d2 = Math.Abs(va[p].Item2 - vb[p].Item2);
            sel.Add(((double)d0 + d1 + d2) / 3);
        }
        double diff = Py.PairwiseSum(sel.ToArray()) / sel.Count;
        return diff > STILL_DIFF;
    }

    /// <summary>py: overlap_diff(prev, cur, vx, vy) — (vx, vy)만큼 옮겼다면 겹친 곳의 평균 회색 차(지도 있는 곳만). 무늬 없으면 null</summary>
    public static double? OverlapDiff(StaticUiState st, Mat prev, Mat cur, double vx, double vy)
    {
        using Mat sa = SmallContent(prev), sb = SmallContent(cur), ga = new Mat(), gb = new Mat();
        Cv2.CvtColor(sa, ga, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(sb, gb, ColorConversionCodes.BGR2GRAY);
        ga.GetArray(out byte[] a); gb.GetArray(out byte[] b);
        int sx = Py.RoundEvenInt(vx * 0.25), sy = Py.RoundEvenInt(vy * 0.25);
        int h = ga.Rows, w = ga.Cols;
        if (Math.Abs(sx) >= w || Math.Abs(sy) >= h) return null;
        byte[]? u = null;
        if (st.Content is not null && st.Content.Rows == h && st.Content.Cols == w) st.Content.GetArray(out u);
        int ay0 = Math.Max(0, sy), ay1 = h + Math.Min(0, sy), ax0 = Math.Max(0, sx), ax1 = w + Math.Min(0, sx);
        int by0 = Math.Max(0, -sy), bx0 = Math.Max(0, -sx);
        long n = 0, land = 0, sum = 0;
        for (int y = 0; y < ay1 - ay0; y++)
            for (int x = 0; x < ax1 - ax0; x++)
            {
                n++;
                int pa = (ay0 + y) * w + ax0 + x, pb = (by0 + y) * w + bx0 + x;
                bool l = a[pa] > 60;
                if (u is not null) l &= u[pa] == 0 && u[pb] == 0;
                if (!l) continue;
                land++;
                sum += Math.Abs(a[pa] - b[pb]);
            }
        if ((double)land / n < 0.05) return null;
        return (double)sum / land;
    }

    /// <summary>py: measure_view_shift(prev, cur) — 엔진 매칭(붙박이 점 빼고)으로 잰 보는 자리 이동. 못 재면 null</summary>
    public static (double X, double Y)? MeasureViewShift(Mat prev, Mat cur)
    {
        var items = Engine.ExtractFeatures([("a.png", prev), ("b.png", cur)], "genshin", [], 8000, [], 25);
        var pm = GridParts.MatchPair(items[0], items[1], 0, 1, 12);
        return pm is null ? null : (-pm.Dx, -pm.Dy);
    }

    /// <summary>py: view_shift(before, after, vx, vy) — 끈 뒤 실제로 옮겨진 양. 빠른 길(겹친 곳 비교) → 안 되면 매칭</summary>
    public static (double X, double Y)? ViewShift(StaticUiState st, Mat before, Mat after, double vx, double vy)
    {
        bool? moved = MapMoved(st, before, after);
        if (moved == false) return (0.0, 0.0);
        double? diff = OverlapDiff(st, before, after, vx, vy);
        if (diff is double d && d <= FULL_MOVE_DIFF)
        {
            var others = new[] { (100, 0), (-100, 0), (0, 100), (0, -100) }
                .Select(o => OverlapDiff(st, before, after, vx + o.Item1, vy + o.Item2)).Where(o => o is not null).Select(o => o!.Value).ToList();
            if (others.Count > 0 && others.Min() - d >= SHARP_MARGIN) return (vx, vy);
        }
        return MeasureViewShift(before, after);
    }
}

/// <summary>
/// 찍으면서 붙이기. py: auto_capture.LiveMap. 붙은 사진마다 특징점·무늬 그림(반 크기)만 들고 있다가 새 화면의 지도 위 자리를 정한다.
/// </summary>
public sealed class LiveMap
{
    public const int LIVE_FEATURES = 4000, LIVE_NEAR = 6, LIVE_STRONG = 80;
    public (double X, double Y) Step;
    public List<(string Name, ImageItem Item, (double X, double Y) Pos)> Tiles = new();
    public int Count;
    readonly Action check;

    public LiveMap((double X, double Y) step, Action? check = null)
    {
        Step = step;
        this.check = check ?? (() => { });
    }

    /// <summary>py: item(image) — 화면 한 장의 특징점(화면 요소 자리는 뺌)과 무늬 그림. 큰 그림은 버린다.</summary>
    public ImageItem Item(Mat image, StaticUiState st)
    {
        Count++;
        var item = Engine.ExtractFeatures([($"live_{Count}.png", image)], "genshin", [], LIVE_FEATURES, [], 25)[0];
        var full = st.Full;
        if (full is not null && full.Rows == item.Gray!.Rows && full.Cols == item.Gray.Cols) item = GridParts.ApplyStaticUi(item, full);
        GridParts.MapGradient(item);
        return item.With(dropImage: true, dropGray: true, dropMatch: true, dropOutput: true);
    }

    public void Add(string name, ImageItem item, (double X, double Y) pos) => Tiles.Add((name, item, pos));

    /// <summary>py: locate(item, guess, refs, wide, tiles) — ((x, y), 맞은 수, 맞춰 본 수, 맞은 것 중 붙은 사진 수) 또는 null</summary>
    public ((double X, double Y) Pos, int Members, int Tried, int FromTiles)? Locate(ImageItem item, (double X, double Y) guess,
        IReadOnlyList<(ImageItem Ref, (double X, double Y) Pos)>? refs = null, bool wide = false, bool tiles = true)
    {
        refs ??= [];
        var near = new List<(double D, ImageItem Ref, (double X, double Y) Pos)>();
        foreach (var (_, rf, pos) in tiles ? Tiles : new List<(string, ImageItem, (double X, double Y))>())
        {
            if (refs.Any(r => ReferenceEquals(r.Ref, rf))) continue;
            double d = Py.Max(Math.Abs(pos.X - guess.X) / Step.X, Math.Abs(pos.Y - guess.Y) / Step.Y);
            if (wide || d <= GridParts.NEIGHBOR) near.Add((d, rf, pos));
        }
        var sorted = near.OrderBy(t => t.D).ToList(); // py: list.sort는 안정 정렬
        var cands = refs.Select(r => (r.Ref, r.Pos)).Concat((wide ? sorted : sorted.Take(LIVE_NEAR)).Select(t => (t.Ref, t.Pos))).ToList();
        var votes = new List<(double X, double Y, double W, int Inliers, bool FromTile)>();
        for (int k = 0; k < cands.Count; k++)
        {
            check();
            var (rf, pos) = cands[k];
            bool fromTile = k >= refs.Count;
            var pm = GridParts.MatchPair(rf, item, 0, 1, GridParts.WEAK_INLIERS);
            if (pm is not null)
            {
                var (ox, oy) = GridParts.OffsetOf(pm);
                bool farOff = Py.Hypot(pos.X + ox - guess.X, pos.Y + oy - guess.Y) > CaptureConsts.ARRIVE_PX;
                if (wide || farOff)
                {
                    var r = GridParts.DenseOffset(rf, item, (ox, oy));
                    if (r is null || r.Value.Peak < GridParts.DENSE_PEAK || r.Value.Peak - r.Value.Second < GridParts.DENSE_MARGIN
                        || Py.Hypot(r.Value.Offset.X - ox, r.Value.Offset.Y - oy) > GridParts.FIT_TOL)
                        pm = null;
                }
                if (pm is not null)
                {
                    votes.Add((pos.X + ox, pos.Y + oy, Math.Sqrt(pm.Inliers), pm.Inliers, fromTile));
                    continue;
                }
            }
            if (!wide)
            {
                var r = GridParts.DenseOffset(rf, item, (guess.X - pos.X, guess.Y - pos.Y));
                if (r is not null && r.Value.Peak >= GridParts.DENSE_PEAK && r.Value.Peak - r.Value.Second >= GridParts.DENSE_MARGIN)
                    votes.Add((pos.X + r.Value.Offset.X, pos.Y + r.Value.Offset.Y, GridParts.DENSE_WEIGHT, 0, fromTile));
            }
        }
        if (votes.Count == 0) return null;
        var support = votes.Select(u => { double s = 0; foreach (var v in votes) if (Py.Hypot(v.X - u.X, v.Y - u.Y) <= GridParts.FIT_TOL) s += v.W; return s; }).ToList();
        int bi = 0;
        for (int i = 1; i < support.Count; i++) if (support[i] > support[bi]) bi = i;
        var best = votes[bi];
        var members = votes.Where(v => Py.Hypot(v.X - best.X, v.Y - best.Y) <= GridParts.FIT_TOL).ToList();
        if (wide && members.Max(v => v.Inliers) < LIVE_STRONG && members.Count < 2) return null;
        double weight = 0, sx = 0, sy = 0;
        foreach (var v in members) weight += v.W;
        foreach (var v in members) sx += v.X * v.W;
        foreach (var v in members) sy += v.Y * v.W;
        return ((sx / weight, sy / weight), members.Count, cands.Count, members.Count(v => v.FromTile));
    }
}

/// <summary>py: fast_anchor.py — 빠른 찍기가 느린 길로 들어갈 때만 믿을 만한 저장 사진을 읽어 자리를 찾는다</summary>
public static class FastAnchor
{
    sealed class BudgetExpired : Exception { }

    /// <summary>py: locate(image, candidates, guess, step, full_ui, deadline). timeUp: 시간 한도가 지났나(py: time.monotonic() >= deadline).
    /// blind: 시뮬 시험용, 참이면 그 맞춰 보기를 못 한 것으로 친다</summary>
    public static (double X, double Y)? Locate(Mat image, IReadOnlyList<(string Path, (double X, double Y) Pos)> candidates,
        (double X, double Y) guess, (double X, double Y) step, Mat? fullUi, Func<bool> timeUp, Func<bool>? blind = null)
    {
        void Check() { if (timeUp()) throw new BudgetExpired(); }
        var live = new LiveMap(step, Check);
        var items = new List<ImageItem>();
        ImageItem Item(string path, Mat frame)
        {
            Check();
            var r = Engine.ExtractFeatures([(path, frame)], "genshin", [], LiveMap.LIVE_FEATURES, [], 25)[0];
            if (fullUi is not null) r = GridParts.ApplyStaticUi(r, fullUi);
            GridParts.MapGradient(r);
            r = r.With(dropImage: true, dropGray: true, dropMatch: true, dropOutput: true);
            items.Add(r);
            Check();
            return r;
        }
        try
        {
            if (candidates.Count == 0) return null;
            var current = Item("fast_current_" + Guid.NewGuid().ToString("N") + ".png", image);
            foreach (int radius in new[] { 1, 2 })
            {
                foreach (var (path, pos) in candidates)
                {
                    double distance = Py.Max(Math.Abs(pos.X - guess.X) / step.X, Math.Abs(pos.Y - guess.Y) / step.Y);
                    if (distance > radius || (radius == 2 && distance <= 1)) continue;
                    Check();
                    using Mat? frame = ImageIO.ImreadUnicode(path);
                    Check();
                    if (frame is null) continue;
                    var rf = Item(path, frame);
                    try
                    {
                        // blind: 흉내 화면 시험용(벽 앞에서 맞춰 보기를 못 한 것으로 침). 실제 판은 null
                        var found = blind is not null && blind() ? null : live.Locate(current, guess, [(rf, pos)], wide: true, tiles: false);
                        Check();
                        if (found is not null) return found.Value.Pos;
                    }
                    finally
                    {
                        GridParts.Forget(rf);
                    }
                }
            }
            return null;
        }
        catch (BudgetExpired)
        {
            return null;
        }
        finally
        {
            foreach (var e in items) GridParts.Forget(e);
        }
    }
}
