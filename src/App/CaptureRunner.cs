using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using GenshinMap.Core.Capture;
using GenshinMap.Win;
using OpenCvSharp;
using Size = OpenCvSharp.Size;
using Rect = OpenCvSharp.Rect;

namespace GenshinMap.App;

/// <summary>
/// py: auto_capture.Reporter — 앱과 연락. C#은 같은 프로세스라 HTTP 대신 작업(Job)을 바로 부른다.
/// 사진은 PNG(압축 1)로 묶어 따로 도는 줄에서 작업에 넣는다(py: _upload_loop).
/// </summary>
sealed class JobReporter : IReporter, IDisposable
{
    readonly Job job;
    readonly BlockingCollection<(string Name, byte[] Data)> uploads = new();
    readonly CountdownEvent inFlight = new(1);
    public List<string> UploadErrors { get; } = new();
    public Dictionary<string, (int Calls, double Seconds)> Timings { get; } = new();
    readonly object timingGate = new();

    public JobReporter(Job job)
    {
        this.job = job;
        new Thread(UploadLoop) { IsBackground = true, Name = "사진 넣기" }.Start();
    }

    public bool StopRequested => job.CaptureStopRequested();

    void Time(string name, double s) { lock (timingGate) { var e = Timings.GetValueOrDefault(name); Timings[name] = (e.Calls + 1, e.Seconds + s); } }

    /// <summary>py: status(**data)</summary>
    public void Status(IReadOnlyDictionary<string, object?> data)
    {
        var o = new JsonObject();
        foreach (var (k, v) in data) o[k] = v switch { null => null, JsonNode n => n, _ => System.Text.Json.JsonSerializer.SerializeToNode(v) };
        double t0 = PreciseClock.Shared.PerfCounter();
        try { job.UpdateCapture(o); } catch (Exception) { }
        Time("status_http", PreciseClock.Shared.PerfCounter() - t0);
    }

    public void Status(params (string K, object? V)[] kv) => Status(kv.ToDictionary(x => x.K, x => x.V));

    /// <summary>py: upload(name, image) — PNG로 묶어 넣기 줄에 보낸다</summary>
    public void Upload(string name, Mat image)
    {
        double t0 = PreciseClock.Shared.PerfCounter();
        Cv2.ImEncode(".png", image, out byte[] buf, [new ImageEncodingParam(ImwriteFlags.PngCompression, 1)]);
        Time("png_encode", PreciseClock.Shared.PerfCounter() - t0);
        inFlight.AddCount();
        uploads.Add((name, buf));
    }

    void UploadLoop()
    {
        foreach (var (name, data) in uploads.GetConsumingEnumerable())
        {
            try
            {
                double t0 = PreciseClock.Shared.PerfCounter();
                job.AddImage(name, data);
                Time("upload_http_async", PreciseClock.Shared.PerfCounter() - t0);
            }
            catch (Exception e) { lock (UploadErrors) UploadErrors.Add($"{name}: {e.Message}"); }
            finally { inFlight.Signal(); }
        }
    }

    /// <summary>py: flush() — 넣기가 다 끝날 때까지</summary>
    public void Flush()
    {
        double t0 = PreciseClock.Shared.PerfCounter();
        inFlight.Signal();
        inFlight.Wait();
        inFlight.Reset(1);
        Time("flush_wait", PreciseClock.Shared.PerfCounter() - t0);
    }

    public void Dispose() => uploads.CompleteAdding();
}

/// <summary>
/// py: auto_capture.main()의 실제 원신 길 — 시작 키 기다림 → 마우스 잠금·예고 → 빠른 찍기 → 결과 기록·알림.
/// 앱 프로세스 안 작업 스레드에서 돈다(앱이 관리자라 따로 띄울 까닭이 없다).
/// </summary>
static class CaptureRunner
{
    const double COUNTDOWN = 3.0;
    static readonly string[] GOOD = ["done", "partial", "limit", "stuck", "lost"];

    public static void Launch(Job job, int maxCells)
    {
        var t = new Thread(() =>
        {
            try { Run(job, maxCells); }
            catch (Exception e)
            {
                try { new JobReporter(job).Status(("state", "error"), ("code", "error"), ("message", "문제가 생겨 멈췄어요."), ("capture_result", "error")); } catch (Exception) { }
                try { File.AppendAllText(Path.Combine(job.Dir, "log_capture.txt"), e + "\n", new UTF8Encoding(false)); } catch (Exception) { }
            }
        }) { IsBackground = true, Name = "찍기" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static void Beep(int freq, int ms) { try { Console.Beep(freq, ms); } catch (Exception) { } PreciseClock.Shared.Sleep(0.05); }
    static void Tones(params int[] freqs) { foreach (int f in freqs) Beep(f, 120); }

    static void Run(Job job, int maxCells)
    {
        maxCells = Math.Max(1, Math.Min(600, maxCells));
        var clock = PreciseClock.Shared;
        using var reporter = new JobReporter(job);
        string logPath = Path.Combine(job.Dir, "log_capture.txt");
        // py: apply_key_settings() — 시작·멈추기 키(사용자 설정)
        var keys = AppState.GetKeys();
        int vkHome = keys["start"]!["vk"]!.GetValue<int>(), vkEnd = keys["stop"]!["vk"]!.GetValue<int>();
        string keyStart = keys["start"]!["name"]!.ToString(), keyStop = keys["stop"]!["name"]!.ToString();
        string ks = $"{keyStart} 키를", ke = $"{keyStop} 키를";
        var startRetry = new Dictionary<string, (string Title, string Sub)>
        {
            ["void_start"] = ("여기는 지도 바깥이에요", $"지도를 끌어서 땅이 화면 가운데 오게 한 뒤 {ks} 다시 눌러 주세요"),
            ["black"] = ("원신 화면을 찍을 수 없어요", $"원신 화면 모드(전체 화면·창 모드)를 바꿔 본 뒤 {ks} 다시 눌러 주세요"),
            ["calibration_failed"] = ("땅이 잘 안 보여서 시작하지 못했어요", $"땅이 화면 가운데 오게 옮긴 뒤 {ks} 다시 눌러 주세요"),
        };
        string RetryText(string s) => startRetry[s].Title + ". " + startRetry[s].Sub + ".";
        string waitText = $"원신 지도 화면에서 {ks} 눌러 주세요.";
        Band? band = null;
        try { band = new Band(); } catch (Exception) { band = null; }
        GenshinScreen? screen = null;
        void BandShow(string kind, string title, string sub, GenshinScreen? scr, double? seconds = null)
        {
            if (band is not null && scr is not null) try { band.Show(kind, title, sub, (scr.Left, scr.Top, scr.W, scr.H), seconds); } catch (Exception) { }
        }
        void BandHide() { if (band is not null) try { band.Hide(); } catch (Exception) { } }
        MouseGuard? guard = null;
        try
        {
            guard = new MouseGuard(() => BandShow("info", "찍는 동안은 마우스가 잠겨 있어요",
                $"클릭하면 바로 멈추고 마우스를 돌려받아요 · Esc나 {ke} 눌러도 돼요", screen, 4));
        }
        catch (Exception) { }
        void GuardRelease() => guard?.Release();
        void FinishHelpers() { guard?.Dispose(); band?.Dispose(); }

        bool Countdown(GenshinScreen scr)
        {
            BandShow("go", "찍기 시작! 6~8분 걸려요", $"손을 떼 주세요 · 멈추려면 클릭하거나 Esc·{keyStop}", scr);
            double end = clock.Time() + COUNTDOWN;
            while (clock.Time() < end)
            {
                if (WinUtil.KeyDown(vkEnd) || WinUtil.KeyDown(KeyLatch.VK_ESCAPE) || StopSignal.IsSet || reporter.StopRequested) { BandHide(); return false; }
                clock.Sleep(0.03);
            }
            BandHide(); // 찍는 사진에 안 들어가게 첫 화면을 찍기 전에 숨긴다
            clock.Sleep(0.15);
            return true;
        }

        GenshinScreen? WaitHome(string code, string message)
        {
            reporter.Status(("state", "waiting"), ("code", code), ("message", message));
            double lastBeat = clock.Time();
            while (true)
            {
                if (reporter.StopRequested) return null;
                if (WinUtil.KeyDown(vkHome))
                {
                    nint hwnd = Native_Foreground();
                    if (GenshinScreen.GENSHIN_EXES.Contains(WinUtil.ProcessName(hwnd))) return new GenshinScreen(hwnd);
                    Beep(300, 200);
                    code = "not_genshin"; message = $"원신 창에서 눌러 주세요. 원신 화면을 한 번 클릭하고 {ks} 눌러 주세요.";
                    reporter.Status(("state", "waiting"), ("code", code), ("message", message));
                    lastBeat = clock.Time();
                    while (WinUtil.KeyDown(vkHome)) clock.Sleep(0.03);
                }
                if (clock.Time() - lastBeat > 2)
                {
                    reporter.Status(("state", "waiting"), ("code", code), ("message", message));
                    lastBeat = clock.Time();
                }
                clock.Sleep(0.03);
            }
        }

        string code0 = "waiting", message0 = waitText;
        var retries = new List<string>();
        string tracePath = Path.Combine(job.Dir, "capture_trace.jsonl");
        string state = "error";
        JsonObject st = new() { ["saved"] = 0, ["skipped"] = 0, ["moves"] = 0, ["backtracks"] = 0 };
        List<string> log = new();
        JsonObject? grid = null;
        double started = clock.Time();
        while (true)
        {
            while (WinUtil.KeyDown(vkHome)) clock.Sleep(0.03); // 앞 시도의 Home을 아직 누르고 있으면 뗄 때까지
            var prev = retries.Count > 0 ? screen : null;
            if (startRetry.TryGetValue(code0, out var rt)) BandShow("warn", rt.Title, rt.Sub, prev); // Home을 다시 누를 때까지 원신 위에 남는다
            screen = WaitHome(code0, message0);
            BandHide();
            if (screen is null)
            {
                reporter.Status(("state", "aborted"), ("code", "aborted"), ("message", "시작 전에 멈췄어요."));
                FinishHelpers();
                return;
            }
            CaptureConsts.ApplyScreenScale(screen.W); // 1080p 등: 픽셀 기준을 화면 너비에 비례로
            guard?.Block(); // 여기서부터 사용자 마우스 막음(클릭·Esc·End·오류·끝나면 풂)
            Tones(880, 1175);
            reporter.Status(("state", "waiting"), ("code", "countdown"), ("message", "곧 찍기 시작해요. 손을 떼 주세요."));
            if (!Countdown(screen))
            {
                GuardRelease();
                while (WinUtil.KeyDown(KeyLatch.VK_ESCAPE) || WinUtil.KeyDown(vkEnd)) clock.Sleep(0.03);
                code0 = "waiting"; message0 = $"시작을 멈췄어요. 다시 찍으려면 원신 지도 화면에서 {ks} 눌러 주세요.";
                continue;
            }
            reporter.Status(("state", "capturing"), ("code", "capturing"), ("done", 0), ("total", maxCells), ("saved", 0), ("skipped", 0), ("message", "찍는 중이에요."));
            started = clock.Time();
            grid = null;
            try { File.Delete(tracePath); } catch (IOException) { }
            FastCapture? cap = null;
            using (var latch = new KeyLatch(vkEnd))
            using (Alive(reporter))
            {
                try
                {
                    log = new List<string>();
                    cap = new FastCapture(screen, clock, reporter, maxCells, logOut: log, endKeyHit: () => latch.Hit, tracePath: tracePath);
                    (state, st, log, grid) = cap.Run();
                    if (cap.LastError is not null) log.Add(cap.LastError);
                }
                catch (Exception exc)
                {
                    state = "error"; st = new JsonObject { ["saved"] = 0, ["skipped"] = 0, ["moves"] = 0, ["backtracks"] = 0 }; log = [exc.ToString()];
                }
                finally
                {
                    GuardRelease(); // 끝·멈춤·오류 모두 여기서 마우스를 돌려준다
                    try { screen.IndicateFinished(); } catch (Exception) { }
                }
                if (startRetry.ContainsKey(state) && Int(st["saved"]) == 0)
                {
                    retries.Add($"{state}({Core.Py.Round(clock.Time() - started, 1)}초)");
                    Tones(392, 330);
                    code0 = state; message0 = RetryText(state);
                    continue;
                }
                reporter.Flush();
                if (grid is not null)
                    File.WriteAllText(Path.Combine(job.Dir, "capture_grid.json"), grid.ToJsonString(AppState.JsonIndent1), new UTF8Encoding(false));
                if (cap?.UiSmall is Mat ui && !ui.Empty())
                {
                    Cv2.ImEncode(".png", ui, out byte[] buf);
                    File.WriteAllBytes(Path.Combine(job.Dir, "ui_mask.png"), buf);
                }
            }
            break;
        }
        if (retries.Count > 0) log.Add($"시작 못 해 {keyStart} 다시 기다림: " + string.Join(", ", retries));
        log.Add($"화면 {screen!.W}×{screen.H}, 픽셀 기준 배율 {screen.W / CaptureConsts.SCREEN_REF_W:F3}");
        double seconds = Core.Py.Round(clock.Time() - started, 1);
        int saved = Int(st["saved"]), skipped = Int(st["skipped"]);
        string? how = StopSignal.How;
        string halt = st["halt_reason"]?.ToString() ?? "";
        var messages = new Dictionary<string, string>
        {
            ["done"] = $"다 찍었어요. 사진 {saved}장", ["partial"] = $"다 찍었어요. 사진 {saved}장",
            ["limit"] = $"지도가 넓어서 정해 둔 사진 수({maxCells}장)에서 멈췄어요. 찍은 데까지 합쳐 드릴게요.",
            ["stuck"] = $"지도 끝에서 계속 막혀 멈췄어요. 찍은 사진 {saved}장으로 합쳐 드릴게요.",
            ["aborted"] = halt.Length > 0 ? halt : how switch
            {
                "click" => $"마우스를 눌러서 멈췄어요. 찍은 사진 {saved}장은 남아 있어요.",
                "esc" => $"Esc를 눌러서 멈췄어요. 찍은 사진 {saved}장은 남아 있어요.",
                "end" => $"{ke} 눌러서 멈췄어요. 찍은 사진 {saved}장은 남아 있어요.",
                _ => $"멈췄어요. 찍은 사진 {saved}장은 남아 있어요.",
            },
            ["halted"] = halt.Length > 0 ? halt : $"확인하지 못해 멈췄어요. 찍은 사진 {saved}장은 남아 있어요.",
            ["lost"] = $"지도 위치를 놓쳐서 멈췄어요. 찍은 사진 {saved}장으로 합쳐 드릴게요.",
            ["void_start"] = RetryText("void_start"), ["black"] = RetryText("black"), ["calibration_failed"] = RetryText("calibration_failed"),
            ["zoom_changed"] = "찍는 도중 지도 크기가 바뀌어서 멈췄어요. 휠을 건드리지 말고 다시 찍어 주세요.",
            ["error"] = "문제가 생겨 멈췄어요.",
        };
        if (reporter.UploadErrors.Count > 0) log.Add("올리기 실패: " + string.Join("; ", reporter.UploadErrors.Take(5)));
        bool good = GOOD.Contains(state);
        string outState = good ? "done" : state is "aborted" or "halted" or "calibration_failed" or "zoom_changed" ? "aborted" : "error";
        reporter.Status(("state", outState), ("code", state), ("capture_result", state), ("stitch_ready", grid is not null && reporter.UploadErrors.Count == 0),
            ("done", saved + skipped), ("total", maxCells), ("saved", saved), ("skipped", skipped), ("seconds", seconds), ("message", messages[state]));
        try
        {
            var timing = new JsonObject
            {
                ["capture"] = st["timings"]?.DeepClone(), ["reporter"] = System.Text.Json.JsonSerializer.SerializeToNode(reporter.Timings.ToDictionary(k => k.Key, k => new { calls = k.Value.Calls, seconds = k.Value.Seconds })),
                ["capture_seconds"] = seconds, ["clock"] = "perf_counter duration seconds",
                ["scope"] = "status includes heartbeat/final; upload is async and overlaps capture/flush; do not sum all buckets",
            };
            File.WriteAllText(Path.Combine(job.Dir, "capture_timing.json"), timing.ToJsonString(AppState.JsonIndent2), new UTF8Encoding(false));
        }
        catch (IOException e) { log.Add($"시간 기록 저장 실패: {e.Message}"); }
        try
        {
            File.WriteAllText(logPath, string.Join("\n", new[] { $"결과 {state}, {seconds}초, 저장 {saved}, 빈 하늘 {skipped}, 끌기 {Int(st["moves"])}번, 되돌아가기 {Int(st["backtracks"])}번" }.Concat(log)) + "\n", new UTF8Encoding(false));
        }
        catch (Exception) { }
        if (good) BandShow("done", "다 찍었어요. 이제 마우스를 써도 돼요", "앱 창에서 지도를 합치고 있어요", screen, 4);
        else BandShow("warn", messages[state].Split(". ")[0].TrimEnd('.'), "자세한 내용은 앱 창에서 볼 수 있어요", screen, 6);
        if (good) Tones(1047, 1319, 1568); else Tones(392, 330, 262);
        if (state != "aborted")
        {
            clock.Sleep(1.5);
            bool moved = WinUtil.BringAppFront();
            try { File.AppendAllText(logPath, $"앱 창 앞으로: {(moved ? "됨" : "못 함")}\n", new UTF8Encoding(false)); } catch (Exception) { }
        }
        if (how is not null) try { File.AppendAllText(logPath, $"사용자가 멈춤: {how}\n", new UTF8Encoding(false)); } catch (Exception) { }
        band?.WaitHidden(7);
        FinishHelpers();
    }

    static int Int(JsonNode? n) => n is JsonValue v ? (v.TryGetValue(out int i) ? i : (int)v.GetValue<double>()) : 0;

    /// <summary>py: alive(reporter) — 찍는 동안 2초마다 살아 있음을 알린다(앱이 20초 조용하면 '연락 끊김'으로 본다)</summary>
    static IDisposable Alive(JobReporter reporter)
    {
        var stop = new ManualResetEventSlim();
        var t = new Thread(() => { while (!stop.Wait(2000)) reporter.Status(new Dictionary<string, object?>()); }) { IsBackground = true };
        t.Start();
        return new Stopper(() => { stop.Set(); t.Join(); });
    }

    sealed class Stopper(Action a) : IDisposable { public void Dispose() => a(); }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint GetForegroundWindow();
    static nint Native_Foreground() => GetForegroundWindow();
}
