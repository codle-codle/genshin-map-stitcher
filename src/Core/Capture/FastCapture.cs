using System.Text.Json.Nodes;
using GenshinMap.Core.Vision;
using OpenCvSharp;
using static GenshinMap.Core.Capture.CaptureConsts;
using CV = GenshinMap.Core.Vision.CaptureVision;
using gs = GenshinMap.Core.Vision.GridParts;
using mo = GenshinMap.Core.Vision.MapObservation;

namespace GenshinMap.Core.Capture;

/// <summary>
/// 빠른 찍기 한 판. py: auto_capture.run_capture_fast(2421~3399행). 안쪽 함수 = 같은 이름 메서드, 닫힌 변수 = 필드.
/// 파이썬 st 사전의 열쇠는 아래 '상태' 필드로 풀었다(열쇠 이름은 주석·JSON에 그대로). 사진 저장·기준 사진 맞추기는
/// 흉내 화면(IsReal=false)에서는 그 자리에서 끝내고(파이썬 시험의 가짜 시계·동기 실행기와 같게), 실제 판은 사진 저장만 따로 도는 일꾼 하나로 보낸다.
/// </summary>
public sealed partial class FastCapture
{
    // ---- 상수(py: auto_capture 모듈 상수)
    public const double SETTLE_MAX = 12.0, SETTLE_CONFIRM = 0.45;
    public const double SLIDE_SCORE = 0.2, SLIDE_GAP = 0.1, SLIDE_REACH = 300;
    public const double FAST_MIN_SCORE = 0.05;
    public const double FAST_CAPTURE_SECONDS = double.PositiveInfinity;
    public const int FAST_ZOOM_EVERY = 10;
    public static readonly (double Top, double Bottom, double Side) SETTLE_CENTER = (0.20, 0.30, 0.08);
    const double RECOVER_REACH = 1.3, RECOVER_SECONDS = 1.0, RECOVER_TOTAL = 15.0;

    // ---- 바깥에서 받는 것
    readonly IScreen screen;
    readonly IClock time;
    readonly IReporter reporter;
    readonly int maxCells;
    readonly Action<double, double, (double X, double Y), string>? trace;
    readonly Func<bool>? endKeyHit;
    readonly string? tracePath;
    public readonly List<string> Log;

    // ---- 닫힌 변수
    readonly int w, h;
    double sx, sy;
    readonly Model model = new();
    Mat? zoomRefImage; ImageItem? zoomRefFeatures;
    readonly List<JsonObject> rows = new();
    readonly List<(JsonObject Row, CalibSample Sample)> calibRows = new();
    readonly OrderedDictionary<string, JsonObject> tiles = new();
    readonly WallLimits limits = new();
    readonly FastGrid topology;
    readonly List<Mat> uiFrames = new();
    readonly Mat uiMoving;
    readonly StaticUiState ui = new();
    readonly double began;
    readonly string archive;
    /// <summary>py: anchors = [(path, pos, future)]. Done이 null이면 그 자리에서 저장함(시뮬)</summary>
    readonly List<(string Path, (double X, double Y) Pos, Task? Done)> anchors = new();
    /// <summary>py: pending(사진 저장 일꾼에 넘긴 일). 실제 판만 쓴다 — 시뮬은 가짜 시계의 동기 실행기처럼 그 자리에서</summary>
    readonly Queue<Task> pending = new();
    Task storeTail = Task.CompletedTask;
    IEnumerable<(string Path, (double X, double Y) Pos)> ReadyAnchors()
        => anchors.Where(a => a.Done is null || a.Done.IsCompletedSuccessfully).Select(a => (a.Path, a.Pos));
    /// <summary>찍기가 끝난 뒤 배운 화면 요소(0.25배, 255=요소). py: st["ui"] → ui_mask.png</summary>
    public Mat? UiSmall => ui.Small;
    const double anchorBudget = 1.5;
    int ignored;
    const bool observeRebound = true;
    readonly LiveMap live;
    int grabs;

    // ---- 상태(py: st 사전)
    Mat stImage = null!;                       // image
    (double X, double Y) pos = (0.0, 0.0);     // pos
    Cell cur = new(0, 0);                      // cur
    int saved, skipped, moves, calibMoves, backtracks, unplaced, k;
    bool guessed;
    int lkAmbiguous, dragLkAmbiguous, cappedChecks, cappedConfirmed, resettled;
    bool zoomUnknown; int zoomUnknownStreak;
    readonly Dictionary<string, (int Calls, double Seconds)> timings = new();
    int noDragArrivals, zoomChecks, reverseDrags, secondReverseDrags, reboundWaitFallbacks;
    string? settleStatus;
    JsonObject? settleConfirmation;
    /// <summary>st.get(열쇠, 0)으로 세는 것들: slides, rebound_slides, settle_slides, weak_fast_ok, edge_probes, edge_probe_arrived,
    /// edge_probe_slow, axis_scans, axis_scan_ambiguous, recover_tries, recover_rejected, recovered</summary>
    readonly Dictionary<string, int> counts = new();
    double recoverSeconds;
    readonly List<int> lengthUnknownDrags = new();
    readonly HashSet<(int Axis, int Sign)> guessedWalls = new();
    string? haltReason;

    sealed class Model
    {
        public double? G;
        public double D0 = mo.D0;
        public List<CalibSample> Calibration = new();
        public mo.StrengthResult? Brightness;
        public List<JsonObject> ZoomDiagnostics = new();
        public (double X, double Y)? Step;
        public DragModel Drag => new(G!.Value, D0);
    }

    sealed class CalibSample
    {
        public mo.GainSampleResult S = null!;
        public JsonObject Fit = null!, Release = null!;
        public (double X, double Y) MouseStart, MouseEnd;
        public int Axis;
        public double RequestedMousePx;
        public bool Selected;
        public JsonObject ToJson() => new()
        {
            ["accepted"] = S.Accepted, ["fit"] = Fit.DeepClone(), ["release_fit"] = Release.DeepClone(),
            ["mouse_start"] = Arr(MouseStart), ["mouse_end"] = Arr(MouseEnd), ["mouse_px"] = S.MousePx,
            ["g"] = S.G, ["along_px"] = S.AlongPx, ["cross_px"] = S.CrossPx, ["release_drift_px"] = S.ReleaseDriftPx,
            ["reason"] = S.Reason, ["axis"] = Axis, ["requested_mouse_px"] = RequestedMousePx, ["selected"] = Selected,
        };
    }

    public FastCapture(IScreen screen, IClock clock, IReporter reporter, int maxCells,
        Action<double, double, (double X, double Y), string>? trace = null, List<string>? logOut = null, Func<bool>? endKeyHit = null,
        string? tracePath = null)
    {
        this.tracePath = tracePath;
        this.screen = screen; time = clock; this.reporter = reporter; this.maxCells = maxCells; this.trace = trace; this.endKeyHit = endKeyHit;
        w = screen.W; h = screen.H;
        sx = STEP_X * w; sy = STEP_Y * h;
        var box = Geometry.ContentBox(w, h);
        topology = new FastGrid(limits, (sx, sy), (box.X2 - box.X1, box.Y2 - box.Y1));
        Log = logOut ?? new List<string>();
        uiMoving = new Mat(Py.RoundEvenInt(h * gs.UI_SCALE), Py.RoundEvenInt(w * gs.UI_SCALE), MatType.CV_8UC1, Scalar.All(0));
        CV.SetStaticUi(ui, null, w, h);
        began = time.Monotonic();
        archive = Directory.CreateTempSubdirectory("genshin-fast-").FullName;
        if (screen.IsReal)
            screen.StopCheck = () => reporter.StopRequested || (endKeyHit?.Invoke() ?? false) || time.Monotonic() - began >= FAST_CAPTURE_SECONDS;
        live = new LiveMap((sx, sy), Check);
    }

    bool InCells(Cell c) => topology.Cells.ContainsKey(c);
    int TouchesGet((Cell C, int Axis, int Sign) key) => topology.Touches.TryGetValue(key, out int n) ? n : 0;
    int Count(string key) => counts.TryGetValue(key, out int n) ? n : 0;
    void Inc(string key, int by = 1) => counts[key] = Count(key) + by;

    // ---------------- 작은 도우미
    static JsonArray Arr((double X, double Y) p) => new(p.X, p.Y);
    static JsonArray Arr(double[] v) => new(v.Select(x => (JsonNode?)x).ToArray());
    static JsonNode? ArrOrNull((double X, double Y)? p) => p is (double x, double y) ? new JsonArray(x, y) : null;

    sealed class Timer(FastCapture c, string name) : IDisposable
    {
        readonly double t0 = c.time.PerfCounter();
        public void Dispose()
        {
            double s = c.time.PerfCounter() - t0;
            var e = c.timings.GetValueOrDefault(name);
            c.timings[name] = (e.Calls + 1, e.Seconds + s);
        }
    }
    Timer Timed(string name) => new(this, name);
    void AddTiming(string name, double seconds)
    {
        var e = timings.GetValueOrDefault(name);
        timings[name] = (e.Calls + 1, e.Seconds + seconds);
    }

    /// <summary>py: mo.summary(fit) — 밑줄로 시작하지 않는 칸만</summary>
    static JsonObject Summary(RawPair f)
    {
        var o = new JsonObject { ["ok"] = f.Ok, ["reason"] = f.Reason };
        if (f.Scale is not null) o["scale"] = f.Scale;
        if (f.RotationDeg is not null) o["rotation_deg"] = f.RotationDeg;
        if (f.Inliers is not null) o["inliers"] = f.Inliers;
        if (f.Good is not null) o["good"] = f.Good;
        if (f.SpanA is not null) o["span_a"] = new JsonArray(f.SpanA.Select(v => (JsonNode?)(double)v).ToArray());
        if (f.SpanB is not null) o["span_b"] = new JsonArray(f.SpanB.Select(v => (JsonNode?)(double)v).ToArray());
        if (f.MedianError is not null) o["median_error"] = f.MedianError;
        if (f.Shift is not null) o["shift"] = Arr(f.Shift);
        return o;
    }

    static bool Changed(RawPair f) => Math.Abs(f.Scale!.Value - 1) > mo.ZOOM_DELTA;

    /// <summary>0/255 마스크 둘의 합(py: a | b)</summary>
    static Mat Or(Mat a, Mat? b)
    {
        var o = a.Clone();
        if (b is not null && b.Rows == a.Rows && b.Cols == a.Cols) Cv2.BitwiseOr(o, b, o);
        return o;
    }

    // ---------------- py 안쪽 함수
    /// <summary>py: leading_void(image, axis, sign, need)</summary>
    bool LeadingVoid(Mat image, int axis, int sign, double need = 0.9)
    {
        byte[] m = CV.BrightMask(ui, image, out int r, out int c);
        int kh = Math.Max(1, (int)(r * CV.EDGE_STRIP)), kw = Math.Max(1, (int)(c * CV.EDGE_STRIP));
        (int r0, int r1, int c0, int c1) = (axis, sign) switch
        {
            (0, 1) => (0, r, c - kw, c), (0, -1) => (0, r, 0, kw), (1, 1) => (r - kh, r, 0, c), _ => (0, kh, 0, c),
        };
        r0 = Math.Max(0, r0); c0 = Math.Max(0, c0);
        long s = 0, n = 0;
        for (int y = r0; y < r1; y++) for (int x = c0; x < c1; x++) { s += m[y * c + x]; n++; }
        return 1 - (double)s / n >= need;
    }

    /// <summary>py: slide_exclude() — 끈 축 밀어 보기에서 안 볼 곳(255)</summary>
    Mat SlideExclude()
    {
        var m = new Mat(h, w, MatType.CV_8UC1, Scalar.All(255));
        var (x1, y1, x2, y2) = Geometry.ContentBox(w, h);
        m[new Rect(x1, y1, x2 - x1, y2 - y1)].SetTo(0);
        var (_, _, ux, uy) = Geometry.FixedUiRect(w, h);
        if (ux > 0 && uy > 0) m[new Rect(0, 0, Math.Min(ux, w), Math.Min(uy, h))].SetTo(255);
        var full = ui.Full;
        if (full is not null && full.Rows == h && full.Cols == w) Cv2.BitwiseOr(m, full, m);
        int r0 = h - Py.RoundEvenInt(60.0 * h / 1440), c0 = w - Py.RoundEvenInt(420.0 * w / 2560);
        m[new Rect(c0, r0, w - c0, h - r0)].SetTo(255);
        return m;
    }

    /// <summary>py: slide(a, b, axis, lo, hi) — 믿을 만하면 D, 아니면 D=null(점수는 그대로)</summary>
    SlideResult Slide(Mat a, Mat b, int axis, double lo, double hi)
    {
        SlideResult r;
        using (Mat ex = SlideExclude()) r = mo.AxisSlide(a, b, axis, lo, hi, ex);
        Inc("slides");
        if (r.D is null || r.Score < SLIDE_SCORE || r.Score - r.Second < SLIDE_GAP) return r with { D = null };
        return r;
    }

    /// <summary>py: axis_scan(before, after, axis, sign, reach) → dict(d, score, second, ambiguous)</summary>
    (double? D, double? Score, double? Second, bool Ambiguous) AxisScan(Mat before, Mat after, int axis, int sign, double reach)
    {
        var (lo, hi) = sign > 0 ? (-SLIDE_REACH, reach + 200) : (-(reach + 200), SLIDE_REACH);
        var r = Slide(before, after, axis, lo, hi);
        if (r.D is null) return (null, r.Score, r.Second, false);
        return (sign * r.D.Value, r.Score, r.Second, false);
    }
    static JsonObject ScanJson((double? D, double? Score, double? Second, bool Ambiguous) s)
        => new() { ["d"] = s.D, ["score"] = s.Score, ["second"] = s.Second, ["ambiguous"] = s.Ambiguous };

    sealed record Window(double SpeedPxS, double AlongPx, double End, bool Moving);

    /// <summary>py: rebound_window(image, axis, sign, first, stamp) → (마지막 화면, 창 또는 null)</summary>
    (Mat Image, Window? W) ReboundWindow(Mat image, int axis, int sign, bool first = true, double? stamp0 = null)
    {
        var samples = new List<(double Along, double Gap)>();
        double stamp;
        if (stamp0 is null)
        {
            stamp = time.Monotonic();
            if (first)
            {
                Check();
                double started0 = time.Monotonic(); image = Grab(); stamp = (started0 + time.Monotonic()) / 2;
            }
        }
        else stamp = stamp0.Value;
        for (int it = 0; it < 2; it++)
        {
            Check();
            time.Sleep(.07);
            double started = time.Monotonic(); Mat nxt = Grab(); double now = (started + time.Monotonic()) / 2;
            byte[] bm = CV.BrightMask(ui, image, out int br, out int bc);
            bool any;
            using Mat allowed = new Mat();
            using (Mat b01 = Mat.FromPixelData(br, bc, MatType.CV_8UC1, bm))
            using (Mat k5 = Mat.Ones(5, 5, MatType.CV_8UC1))
                Cv2.Dilate(b01, allowed, k5, null, 1, BorderTypes.Constant, Cv2.MorphologyDefaultBorderValue());
            any = Cv2.CountNonZero(allowed) > 0;
            using Mat full = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
            var (x1, y1, x2, y2) = Geometry.ContentBox(w, h);
            using (Mat rs = new Mat())
            {
                Cv2.Resize(allowed, rs, new Size(x2 - x1, y2 - y1), 0, 0, InterpolationFlags.Nearest);
                rs.CopyTo(full[new Rect(x1, y1, x2 - x1, y2 - y1)]);
            }
            using Mat small = new Mat();
            Cv2.Resize(full, small, new Size(uiMoving.Cols, uiMoving.Rows), 0, 0, InterpolationFlags.Nearest);
            using Mat excluded0 = new Mat();
            Cv2.Compare(small, 0, excluded0, CmpTypes.EQ);
            using Mat excluded = Or(excluded0, ui.Small);
            Motion? motion = any ? mo.LkMotion(image, nxt, excluded, SETTLE_CENTER) : null;
            double[]? d = motion is not null && motion.Status == "valid" ? motion.Shift : null;
            if (d is null)
            {
                var r = Slide(image, nxt, axis, -SLIDE_REACH, SLIDE_REACH);
                if (r.D is not null)
                {
                    d = [0.0, 0.0]; d[axis] = -r.D.Value;
                    Inc("rebound_slides");
                }
            }
            double gap = now - stamp;
            image = nxt; stamp = now;
            if (d is null || !(0 < gap && gap <= 1.0)) return (image, null);
            double along = sign * d[axis];
            if (first && along <= 0) return (image, null);
            samples.Add((along, gap));
            if (samples.Count > 2) samples.RemoveRange(0, samples.Count - 2);
            double sumAlong = 0; foreach (var s in samples) sumAlong += s.Along;
            if (samples.Count == 2 && (!first || sumAlong >= 4))
            {
                double sumGap = 0; foreach (var s in samples) sumGap += s.Gap;
                double speed = sumAlong / sumGap;
                return (image, new Window(speed, sumAlong, now, samples.All(s => s.Along > 0) && speed > 3));
            }
        }
        return (image, null);
    }

    /// <summary>py: reverse_rebound(image, axis, sign, window, diagnostic) → (화면, 멈춤 봄)</summary>
    (Mat Image, bool Still) ReverseRebound(Mat image, int axis, int sign, Window? window, JsonObject diagnostic)
    {
        double[] taus = [1.83, 2.3];
        for (int attempt = 0; attempt < taus.Length; attempt++)
        {
            double tau = taus[attempt];
            Check();
            double speed = Py.Max(0.0, window!.SpeedPxS);
            double delay = Py.Max(0.0, time.Monotonic() - window.End) + .4;
            double predicted = Py.Max(0.0, speed * tau - speed * delay) * 1.1 + 20;
            double mouse = Py.Max(80.0, predicted / model.G!.Value + model.D0);
            double[] vector = [0.0, 0.0]; vector[axis] = -sign * model.G.Value * (mouse - model.D0);
            var spot = Geometry.PressSpot(image, vector[0], vector[1], [], model.Drag, ui.Full);
            diagnostic["predictions"]!.AsArray().Add(new JsonObject
            {
                ["speed_px_s"] = speed, ["tau_s"] = tau, ["remaining_map_px"] = speed * tau, ["catch_delay_s"] = delay,
                ["predicted_map_px"] = predicted, ["mouse_target_px"] = mouse,
            });
            if (spot is null || moves >= maxCells * 1.5) break;
            Check();
            var points = MoveView(vector[0], vector[1], spot.Value.Shift, spot.Value.Along, model.Drag)!.Value;
            moves++; reverseDrags++;
            secondReverseDrags += attempt == 1 ? 1 : 0;
            diagnostic["reverse_count"] = diagnostic["reverse_count"]!.GetValue<int>() + 1;
            diagnostic["actual_reverse_mouse_px"]!.AsArray().Add(Py.Dist(points.Start, points.End));
            diagnostic["reverse_vectors"]!.AsArray().Add(Arr(vector));
            image = Grab();
            (image, window) = ReboundWindow(image, axis, sign, false);
            if (window is not null && Math.Abs(window.SpeedPxS) <= 3) return (image, true);
            if (window is null || !window.Moving) break;
        }
        diagnostic["wait_fallback"] = true;
        reboundWaitFallbacks++;
        return (image, false);
    }

    /// <summary>py: check()</summary>
    void Check()
    {
        if (reporter.StopRequested || (endKeyHit?.Invoke() ?? false)) throw new Stopped();
        if (time.Monotonic() - began >= FAST_CAPTURE_SECONDS) throw new LimitReached("시간 상한");
        if (screen.IsReal) screen.Ready();
        else if (!screen.StillHere()) throw new Stopped();
    }

    /// <summary>py: add_row(row)</summary>
    void AddRow(JsonObject row)
    {
        rows.Add(row);
        // py: trace_path가 있으면 끌기마다 capture_trace.jsonl에 바로 덧붙인다(끊겨도 남게)
        if (tracePath is not null)
            File.AppendAllText(tracePath, row.ToJsonString(new System.Text.Json.JsonSerializerOptions
                { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new System.Text.UTF8Encoding(false));
    }

    /// <summary>py: settle_after_drag(image, vx, vy) → (화면, 기다리는 동안 움직인 양, 멈춤 확인)</summary>
    (Mat Image, (double X, double Y) Sv, bool Settled) SettleAfterDrag(Mat image, double vx, double vy)
    {
        dragLkAmbiguous = 0;
        var (img, sv, status) = Settle(image, vx, vy);
        settleStatus = status;
        if (status == "capped")
        {
            cappedChecks++;
            var item = live.Item(img, ui);
            bool confirmed;
            try { confirmed = ConfirmStill(item, "capped", image: img); }
            finally { gs.Forget(item); }
            cappedConfirmed += confirmed ? 1 : 0;
            return (img, sv, confirmed);
        }
        return (img, sv, status == "still");
    }

    /// <summary>py: grab()</summary>
    Mat Grab()
    {
        if (++grabs % 16 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); } // 큰 화면 Mat(원본 메모리)을 늦지 않게 돌려준다
        Mat image = screen.Grab();
        if (screen.IsReal)
        {
            Cv2.MeanStdDev(image, out Scalar mean, out Scalar sd);
            int c = image.Channels();
            double m = 0, v = 0;
            for (int i = 0; i < c; i++) { m += mean[i]; v += sd[i] * sd[i] + mean[i] * mean[i]; }
            m /= c; v = v / c - m * m;
            if (m < 2 && Math.Sqrt(Math.Max(0, v)) < 2) throw new BlackScreen();
        }
        return image;
    }

    /// <summary>py: idle_grab()</summary>
    Mat IdleGrab()
    {
        while (true)
        {
            try
            {
                Check();
                Mat image = Grab();
                if (screen.IsReal) screen.WasPaused = false;
                return image;
            }
            catch (FocusPause) { screen.WaitResume(); }
        }
    }

    /// <summary>py: move_view(screen, vx, vy, shift, along, on_hold, model) — 끄는 자리(시작점, 끝점)</summary>
    ((double X, double Y) Start, (double X, double Y) End)? MoveView(double vx, double vy, double shift, double along, DragModel m)
    {
        if (Py.Hypot(vx, vy) < 1) return null;
        var (start, end) = Geometry.DragPoints(w, h, vx, vy, shift, along, m);
        screen.Drag(start.X, start.Y, end.X, end.Y, null, null);
        screen.ParkCursor();
        time.Sleep(0.45);
        return (start, end);
    }
}
