using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCvSharp;
using Size = OpenCvSharp.Size;
using Rect = OpenCvSharp.Rect;

namespace GenshinMap.App;

/// <summary>
/// 작업 폴더 하나(py: server.Job): input/ 스크린샷, stitched.png, trimmed.png, 미리보기, job.json.
/// 메타·진행 상태는 파이썬 dict와 같은 열쇠의 JSON으로 들고 있어 job.json이 파이썬 판과 서로 읽힌다.
/// </summary>
public sealed class Job
{
    static readonly string[] IMAGE_EXTS = [".png", ".jpg", ".jpeg", ".bmp", ".webp"];
    public const int PREVIEW_MAX = 4096, SHOT_THUMB_W = 192;
    static readonly (double, double, double, double)[] EXTRA_UI_FRACS = [(0.40, 0.91, 0.60, 1.0)];
    static readonly string[] TRIM_BASE_ARGS = ["--process-scale", "0.5", "--close", "35", "--wall-thickness", "7",
        "--upscale-smooth", "7", "--mask-smooth", "9", "--final-close", "5", "--crop"];
    public static readonly Dictionary<string, string[]> ISLAND_PRESETS = new()
    {
        ["default"] = [],
        ["strict"] = ["--keep-largest-components", "1", "--min-keep-area", "500000", "--min-keep-span", "1000"],
        ["keep2"] = ["--keep-largest-components", "2", "--min-keep-area", "100000", "--min-keep-span", "600"],
        ["off"] = ["--no-remove-islands"],
    };

    public string Id { get; }
    public string Dir { get; }
    public string InputDir => Path.Combine(Dir, "input");
    public string ThumbDir => Path.Combine(Dir, "thumbs");
    public string PreviewDir => Path.Combine(Dir, "preview");
    public string MosaicDir => Path.Combine(Dir, "mosaic");
    string MetaPath => Path.Combine(Dir, "job.json");
    public readonly object Lock = new();
    Process? proc;
    bool cancelled;
    JsonObject? live;
    JsonObject? capture;
    JsonObject? mosaic;
    public JsonObject Meta { get; }
    /// <summary>찍기 시작 함수(앱이 넣는다: 앱 안 작업 스레드로 찍기를 돌림). py: start_capture의 ShellExecute(runas)</summary>
    public static Action<Job, int>? CaptureLauncher;

    public Job(string id)
    {
        Id = id;
        Dir = Path.Combine(AppState.WorkDir, id);
        Meta = new JsonObject { ["id"] = id, ["created"] = AppState.NowText(), ["shot_size"] = null, ["stitch"] = null, ["trim"] = null };
        if (File.Exists(MetaPath))
            foreach (var (k, v) in (JsonObject)JsonNode.Parse(File.ReadAllText(MetaPath, Encoding.UTF8))!) Meta[k] = v?.DeepClone();
    }

    public static Job Create()
    {
        string id = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        while (Directory.Exists(Path.Combine(AppState.WorkDir, id)))
        {
            Thread.Sleep(1000);
            id = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }
        var job = new Job(id);
        foreach (var d in new[] { job.InputDir, job.ThumbDir, job.PreviewDir }) Directory.CreateDirectory(d);
        job.Save();
        return job;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(MetaPath, Meta.ToJsonString(AppState.JsonIndent2), new UTF8Encoding(false));
    }

    /// <summary>py: images() — 붙이기와 같은 순서(파일 이름 정렬)</summary>
    public List<string> Images()
    {
        if (!Directory.Exists(InputDir)) return [];
        return Directory.EnumerateFiles(InputDir).Select(Path.GetFileName).Where(n => IMAGE_EXTS.Contains(Path.GetExtension(n)!.ToLowerInvariant()))
            .Select(n => n!).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public bool Running() { var p = proc; return p is not null && !p.HasExited; }

    JsonNode? MetaGet(string k) => Meta.TryGetPropertyValue(k, out var v) ? v : null;

    /// <summary>py: state()</summary>
    public JsonObject State()
    {
        lock (Lock)
        {
            var images = Images();
            var previews = new JsonObject();
            foreach (var k in new[] { "stitched", "trimmed", "overlay" }) previews[k] = PreviewVersion(k);
            return new JsonObject
            {
                ["id"] = Id, ["created"] = Meta["created"]?.DeepClone(),
                ["images"] = new JsonArray(images.Select(i => (JsonNode?)i).ToArray()),
                ["shotSize"] = MetaGet("shot_size")?.DeepClone(), ["stitch"] = MetaGet("stitch")?.DeepClone(), ["trim"] = MetaGet("trim")?.DeepClone(),
                ["live"] = live?.DeepClone(), ["capture"] = CaptureStateLocked(), ["previews"] = previews,
                ["mosaic"] = mosaic?.DeepClone(),
            };
        }
    }

    /// <summary>py: capture_state()</summary>
    JsonObject? CaptureStateLocked()
    {
        if (capture is null || capture.Count == 0) return null;
        var cap = (JsonObject)capture.DeepClone();
        double quiet = AppState.UnixNow() - (cap["updated"] is JsonValue u ? u.GetValue<double>() : 0);
        string? st = cap["state"]?.ToString();
        if ((st == "waiting" || st == "capturing") && quiet > 20) { cap["state"] = "lost"; cap["message"] = "찍기가 20초 넘게 응답하지 않아요"; }
        else if (st == "launching" && quiet > 120) { cap["state"] = "lost"; cap["message"] = "찍기를 시작하지 못했어요"; }
        return cap;
    }

    public bool CaptureActive()
    {
        lock (Lock)
        {
            var cap = CaptureStateLocked();
            string? s = cap?["state"]?.ToString();
            return s is "launching" or "waiting" or "capturing";
        }
    }

    /// <summary>py: start_capture(max_cells) — C#은 앱 프로세스 안 작업 스레드로(앱 전체가 관리자라 UAC 없음)</summary>
    public void StartCapture(JsonNode? maxCellsNode)
    {
        if (Running() || CaptureActive()) throw new InvalidOperationException("이미 다른 일을 하고 있어요. 끝난 뒤에 다시 눌러 주세요");
        int maxCells = ToInt(maxCellsNode);
        if (!(10 <= maxCells && maxCells <= 600)) throw new ArgumentException("최대 사진 수는 10~600장이에요");
        lock (Lock)
            capture = new JsonObject
            {
                ["state"] = "launching", ["message"] = "찍기를 준비하는 중", ["updated"] = AppState.UnixNow(),
                ["total"] = maxCells, ["done"] = 0, ["saved"] = 0, ["skipped"] = 0,
            };
        (CaptureLauncher ?? throw new InvalidOperationException("찍기를 시작할 수 없어요"))(this, maxCells);
    }

    static readonly string[] CAPTURE_KEYS = ["state", "code", "message", "done", "total", "saved", "skipped", "seconds", "capture_result", "stitch_ready"];

    /// <summary>py: update_capture(data) — 찍기 상태를 받는다. 앱에서 멈추기를 눌렀으면 true</summary>
    public bool UpdateCapture(JsonObject data)
    {
        bool autoStitch = false;
        bool stopped;
        JsonObject cap;
        lock (Lock)
        {
            cap = capture ?? new JsonObject();
            foreach (var k in CAPTURE_KEYS) if (data.TryGetPropertyValue(k, out var v)) cap[k] = v?.DeepClone();
            cap["updated"] = AppState.UnixNow();
            capture = cap;
            stopped = cap["stop"] is JsonValue sv && sv.GetValue<bool>();
            if (data.ContainsKey("capture_result") && cap["auto_stitch"] is null)
            {
                var result = new JsonObject { ["capture_result"] = cap["capture_result"]?.DeepClone(), ["status"] = "skipped" };
                string? cr = cap["capture_result"]?.ToString();
                if (stopped) result["reason"] = "user_stop";
                else if (cap["state"]?.ToString() != "done" || cr is not ("done" or "partial" or "limit" or "stuck" or "lost")) result["reason"] = "capture_not_completed";
                else if (!(cap["stitch_ready"] is JsonValue r && r.TryGetValue(out bool rb) && rb)) result["reason"] = "uploads_or_grid_not_ready";
                else if (Running()) result["reason"] = "already_running";
                else
                {
                    try
                    {
                        var grid = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "capture_grid.json"), Encoding.UTF8)) as JsonObject;
                        var images = Images();
                        var tiles = grid?["tiles"] as JsonObject;
                        int version = grid?["version"] is JsonValue gv ? gv.GetValue<int>() : 1;
                        int savedN = cap["saved"] is JsonValue s ? (int)s.GetValue<double>() : -1;
                        if (grid is null || tiles is null || version < 2 || images.Count < 2 || !new HashSet<string>(images).SetEquals(tiles.Select(t => t.Key)) || images.Count != savedN)
                            throw new FormatException("사진 수·이름과 격자 기록이 맞지 않음");
                        result["status"] = "starting";
                        autoStitch = true;
                    }
                    catch (Exception exc) when (exc is IOException or FormatException or System.Text.Json.JsonException or InvalidOperationException)
                    {
                        result["reason"] = "invalid_capture_files"; result["error"] = exc.Message;
                    }
                }
                cap["auto_stitch"] = result;
                Meta["auto_stitch"] = result.DeepClone();
                Save();
            }
        }
        if (autoStitch)
        {
            JsonObject status;
            try { StartStitch(); status = new JsonObject { ["status"] = "started" }; }
            catch (Exception exc) { status = new JsonObject { ["status"] = "failed", ["error"] = exc.Message }; }
            lock (Lock)
            {
                var a = (JsonObject)cap["auto_stitch"]!;
                foreach (var (k, v) in status) a[k] = v?.DeepClone();
                Meta["auto_stitch"] = a.DeepClone();
                Save();
            }
        }
        return stopped;
    }

    /// <summary>py: stop_capture()</summary>
    public void StopCapture() { lock (Lock) if (capture is not null) capture["stop"] = true; }

    /// <summary>찍기가 멈추라는 요청을 받았나(py Reporter.stop_requested — 파이썬은 상태를 보낼 때 답으로 알았다)</summary>
    public bool CaptureStopRequested() { lock (Lock) return capture?["stop"] is JsonValue v && v.GetValue<bool>(); }

    JsonNode? PreviewVersion(string kind)
    {
        string p = Path.Combine(PreviewDir, $"{kind}.jpg");
        return File.Exists(p) ? (long)Math.Floor((File.GetLastWriteTimeUtc(p) - DateTime.UnixEpoch).TotalSeconds) : null;
    }

    /// <summary>py: summary()</summary>
    public JsonObject Summary()
    {
        string? thumb = null;
        foreach (var kind in new[] { "trimmed", "stitched" })
            if (File.Exists(Path.Combine(PreviewDir, $"{kind}_small.jpg"))) { thumb = kind; break; }
        string? Status(string k) => (MetaGet(k) as JsonObject)?["status"]?.ToString();
        return new JsonObject
        {
            ["id"] = Id, ["created"] = Meta["created"]?.DeepClone(), ["count"] = Images().Count,
            ["stitched"] = Status("stitch") == "done", ["trimmed"] = Status("trim") == "done", ["thumb"] = thumb,
        };
    }

    // ---------- 스크린샷
    /// <summary>py: add_image(name, data) — 잘못이면 ArgumentException(py ValueError)</summary>
    public string AddImage(string name, byte[] data)
    {
        name = Path.GetFileName(name);
        if (!IMAGE_EXTS.Contains(Path.GetExtension(name).ToLowerInvariant())) throw new ArgumentException("그림 파일(png·jpg·bmp·webp)만 넣을 수 있어요");
        using Mat image = Cv2.ImDecode(data, ImreadModes.Color);
        if (image.Empty()) throw new ArgumentException("그림을 읽을 수 없어요");
        int h = image.Rows, w = image.Cols;
        lock (Lock)
        {
            var size = MetaGet("shot_size") as JsonArray;
            if (size is not null && Images().Count > 0 && (w != size[0]!.GetValue<int>() || h != size[1]!.GetValue<int>()))
                throw new ArgumentException($"사진 크기가 달라요: {w}×{h} (먼저 넣은 사진은 {size[0]}×{size[1]})");
            Directory.CreateDirectory(InputDir);
            Directory.CreateDirectory(ThumbDir);
            File.WriteAllBytes(Path.Combine(InputDir, name), data);
            using (var thumb = new Mat())
            {
                Cv2.Resize(image, thumb, new Size(SHOT_THUMB_W, (int)(SHOT_THUMB_W * (double)h / w)), 0, 0, InterpolationFlags.Area);
                AppState.ImwriteJpg(Path.Combine(ThumbDir, name + ".jpg"), thumb, 80);
            }
            AppState.WriteMosaicThumb(image, Path.Combine(MosaicDir, name + ".jpg"));
            Meta["shot_size"] = new JsonArray(w, h);
            InvalidateResults();
            Save();
        }
        return name;
    }

    /// <summary>py: remove_image(name) — 사진과 작은 사진을 지운다(사용자가 화면에서 빼기를 누른 것)</summary>
    public void RemoveImage(string name)
    {
        name = Path.GetFileName(name);
        lock (Lock)
        {
            foreach (var p in new[] { Path.Combine(InputDir, name), Path.Combine(ThumbDir, name + ".jpg"), Path.Combine(MosaicDir, name + ".jpg") })
                if (File.Exists(p)) File.Delete(p);
            if (Images().Count == 0) Meta["shot_size"] = null;
            InvalidateResults();
            Save();
        }
    }

    void InvalidateResults()
    {
        if (MetaGet("stitch") is JsonObject s) s["stale"] = true;
        if (MetaGet("trim") is JsonObject t) t["stale"] = true;
    }

    // ---------- 실행
    /// <summary>py: run(kind, cmd, parser, finish) — 같은 exe를 자식 프로세스로 띄우고 출력 줄을 읽는다</summary>
    void Run(string kind, IReadOnlyList<string> args, Action<string, JsonObject> parser, Action<JsonObject, List<string>> finish)
    {
        lock (Lock)
        {
            if (Running()) throw new InvalidOperationException("이미 다른 일을 하고 있어요. 끝난 뒤에 다시 눌러 주세요");
            cancelled = false;
            live = new JsonObject { ["kind"] = kind, ["phase"] = "시작하는 중", ["done"] = 0, ["total"] = 0, ["note"] = "", ["started"] = AppState.UnixNow() };
            var psi = new ProcessStartInfo(AppState.Exe)
            {
                WorkingDirectory = Dir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            try
            {
                proc = Process.Start(psi) ?? throw new InvalidOperationException("자식 프로세스를 못 띄움");
                if (kind == "stitch") try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            }
            catch (Exception exc)
            {
                live = null;
                var prev = MetaGet(kind) as JsonObject ?? new JsonObject();
                var m = (JsonObject)prev.DeepClone();
                m["status"] = "failed"; m["error"] = exc.Message; m["ended"] = AppState.NowText();
                Meta[kind] = m;
                Save();
                throw;
            }
        }
        var p = proc!;
        string logPath = Path.Combine(Dir, $"log_{kind}.txt");
        var lines = new List<string>();
        var log = new StreamWriter(logPath, false, new UTF8Encoding(false));
        void OnLine(string? raw)
        {
            if (raw is null) return;
            string line = raw.TrimEnd();
            lock (Lock)
            {
                log.WriteLine(line);
                lines.Add(line);
                if (live is not null) parser(line, live);
            }
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data); // py: stderr=STDOUT(한 줄기로 합침)
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        new Thread(() =>
        {
            p.WaitForExit(); // 스트림 끝까지 읽은 뒤 돌아온다
            int code = p.ExitCode;
            log.Dispose();
            double started = live?["started"]?.GetValue<double>() ?? AppState.UnixNow();
            var result = new JsonObject { ["ended"] = AppState.NowText(), ["seconds"] = Core.Py.Round(AppState.UnixNow() - started, 1) };
            lock (Lock)
            {
                if (cancelled) result["status"] = "cancelled";
                else if (code != 0) { result["status"] = "failed"; result["error"] = Parsers.LastErrorLine(lines); }
                else result["status"] = "done";
            }
            string status = result["status"]!.ToString();
            if (status == "done")
            {
                try
                {
                    lock (Lock) if (live is not null) live["phase"] = "미리보기 만드는 중";
                    finish(result, lines);
                }
                catch (Exception exc) { result["previewError"] = exc.Message; }
            }
            else if (kind == "stitch") finish(result, lines);
            lock (Lock)
            {
                var merged = (MetaGet(kind) as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
                foreach (var (k, v) in result) merged[k] = v?.DeepClone();
                merged["stale"] = false;
                Meta[kind] = merged;
                if (kind == "stitch") Meta["trim"] = status == "done" ? null : MetaGet("trim")?.DeepClone();
                live = null;
                proc = null;
                Save();
            }
            p.Dispose();
            if (kind == "stitch" && status == "done")
            {
                var opts = AppState.LoadSettings()["trim"] as JsonObject ?? new JsonObject();
                try { StartTrim(opts["margin"] ?? 70, opts["line"]?.ToString() ?? "cyan", opts["islands"]?.ToString() ?? "default"); }
                catch (Exception exc)
                {
                    lock (Lock)
                    {
                        Meta["trim"] = new JsonObject { ["status"] = "failed", ["error"] = $"자동 다듬기를 시작 못 함: {exc.Message}", ["ended"] = AppState.NowText() };
                        Save();
                    }
                }
            }
        }) { IsBackground = true, Name = $"{kind} 기다림" }.Start();
    }

    /// <summary>py: cancel()</summary>
    public void Cancel()
    {
        lock (Lock)
            if (Running())
            {
                cancelled = true;
                try { proc!.Kill(entireProcessTree: true); } catch (Exception) { }
            }
    }

    static int ToInt(JsonNode? n) => n is JsonValue v ? (v.TryGetValue(out int i) ? i : v.TryGetValue(out double d) ? (int)d : int.Parse(v.ToString())) : 0;

    List<string> ExcludeArgs()
    {
        var size = (JsonArray)Meta["shot_size"]!;
        int w = size[0]!.GetValue<int>(), h = size[1]!.GetValue<int>();
        var args = new List<string>();
        foreach (var (fx1, fy1, fx2, fy2) in EXTRA_UI_FRACS)
            args.AddRange(["--exclude", $"{(int)(fx1 * w)},{(int)(fy1 * h)},{(int)(fx2 * w)},{(int)(fy2 * h)}"]);
        return args;
    }

    // py: AUTO_STITCH_ARGS(옛 자동 캡처 'wide')·STITCH_ARGS(직접 넣은 사진 'engine')
    static readonly string[] AUTO_STITCH_ARGS = ["--devignette", "0.38", "--pair-search", "progressive", "--min-gap-before-stop", "12"];
    static readonly string[] STITCH_ARGS = ["--devignette", "0.38", "--pair-search", "progressive", "--min-gap-before-stop", "3"];

    /// <summary>py: start_stitch() — 끈 위치 기록(version 2)이 모든 사진에 있으면 격자 붙이기, 기록만 있으면 'wide', 없으면 'engine'(직접 넣은 사진)</summary>
    public void StartStitch()
    {
        var images = Images();
        if (images.Count < 2) throw new ArgumentException("사진이 2장 이상 있어야 합칠 수 있어요");
        string output = Path.Combine(Dir, "stitched.png"), grid = Path.Combine(Dir, "capture_grid.json");
        var record = File.Exists(grid) ? JsonNode.Parse(File.ReadAllText(grid, Encoding.UTF8)) as JsonObject : null;
        var tiles = record?["tiles"] as JsonObject;
        int version = record?["version"] is JsonValue gv ? gv.GetValue<int>() : 1;
        string mode;
        List<string> args;
        if (record is not null && version >= 2 && tiles is not null && images.All(tiles.ContainsKey))
        {
            args = ["stitch", InputDir, grid, "-o", output]; // 모든 사진에 끈 위치 기록이 있는 자동 캡처 → 격자 붙이기
            mode = "grid";
        }
        else if (record is not null) { args = ["engine", InputDir, "-o", output, .. AUTO_STITCH_ARGS]; mode = "wide"; }
        else { args = ["engine", InputDir, "-o", output, .. STITCH_ARGS]; mode = "engine"; }
        args.AddRange(ExcludeArgs());
        int n = images.Count;
        JsonObject? ms = null;
        if (mode == "grid")
        {
        // 실시간 붙이기(B안): 첫 사진은 찍은 자리에, 나머지는 붙이기가 맞춰 이을 때마다 그 자리에 나타난다
        string first = images[0];
        var size = (JsonArray)Meta["shot_size"]!;
        var (x1, y1, x2, y2) = AppState.MosaicBox(size[0]!.GetValue<int>(), size[1]!.GetValue<int>());
        ms = new JsonObject
        {
            ["v"] = 1, ["seq"] = 0, ["total"] = n, ["first"] = first, ["crop"] = new JsonArray(x1, y1, x2 - x1, y2 - y1),
            ["pos"] = new JsonObject { [first] = new JsonArray(tiles![first]!["x"]!.DeepClone(), tiles[first]!["y"]!.DeepClone()) },
            ["order"] = new JsonArray(first), ["recent"] = new JsonArray(), ["final"] = null, ["canvas"] = null,
        };
        var missing = images.Where(nm => !File.Exists(Path.Combine(MosaicDir, nm + ".jpg"))).ToList();
        if (missing.Count > 0)
            new Thread(() =>
            {
                foreach (var nm in missing)
                    try { using var im = AppState.Imread(Path.Combine(InputDir, nm)); if (im is not null) AppState.WriteMosaicThumb(im, Path.Combine(MosaicDir, nm + ".jpg")); }
                    catch (Exception) { }
            }) { IsBackground = true }.Start();
        }
        lock (Lock)
        {
            mosaic = ms;
            Meta["stitch"] = new JsonObject { ["status"] = "running", ["started"] = AppState.NowText(), ["count"] = n, ["mode"] = mode };
            Meta["trim"] = null;
            foreach (var kind in new[] { "stitched", "trimmed", "overlay" })
                foreach (var suffix in new[] { "", "_small" })
                {
                    string pth = Path.Combine(PreviewDir, $"{kind}{suffix}.jpg");
                    if (File.Exists(pth)) File.Delete(pth);
                }
            Save();
        }
        void Finish(JsonObject result, List<string> lines)
        {
            if (result["status"]?.ToString() == "done")
            {
                using var image = AppState.Imread(output) ?? throw new IOException("붙인 그림을 못 읽음");
                result["size"] = new JsonArray(image.Cols, image.Rows);
                WritePreviews("stitched", image);
                if (ms is not null) try
                {
                    var lg = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "grid_stitch_log.json"), Encoding.UTF8))!;
                    lock (Lock)
                    {
                        ms!["final"] = lg["positions"]?.DeepClone();
                        ms["canvas"] = lg["output_canvas"]?.DeepClone();
                        ms["v"] = ms["v"]!.GetValue<int>() + 1;
                    }
                }
                catch (Exception) { }
            }
            else if (result["status"]?.ToString() == "failed")
                result["unconnected"] = new JsonArray(Parsers.UnconnectedImages(images, lines).Select(s => (JsonNode?)s).ToArray());
        }
        Run("stitch", args, Parsers.StitchParser(n, showPercent: mode == "grid", mosaic: ms), Finish);
    }

    /// <summary>py: start_trim(margin, line, islands)</summary>
    public void StartTrim(JsonNode? marginNode, string line = "cyan", string islands = "default")
    {
        string stitched = Path.Combine(Dir, "stitched.png");
        if (!File.Exists(stitched)) throw new ArgumentException("먼저 사진을 합쳐야 다듬을 수 있어요");
        if (line is not ("cyan" or "gray" or "both") || !ISLAND_PRESETS.ContainsKey(islands)) throw new ArgumentException("설정 값이 올바르지 않아요");
        int margin = Math.Max(0, Math.Min(400, marginNode is null ? 70 : ToInt(marginNode)));
        AppState.SaveSettings("trim", new JsonObject { ["margin"] = margin, ["line"] = line, ["islands"] = islands });
        string debugDir = Path.Combine(Dir, "debug_trim"), output = Path.Combine(Dir, "trimmed.png");
        var args = new List<string> { "trim", stitched, "-o", output, "--target-line", line, "--margin", margin.ToString() };
        args.AddRange(TRIM_BASE_ARGS);
        args.AddRange(ISLAND_PRESETS[islands]);
        args.AddRange(["--debug", debugDir]);
        lock (Lock)
        {
            Meta["trim"] = new JsonObject
            {
                ["status"] = "running", ["started"] = AppState.NowText(),
                ["options"] = new JsonObject { ["margin"] = margin, ["line"] = line, ["islands"] = islands },
            };
            Save();
        }
        void Finish(JsonObject result, List<string> lines)
        {
            using var image = AppState.Imread(output) ?? throw new IOException("다듬은 그림을 못 읽음");
            result["size"] = new JsonArray(image.Cols, image.Rows);
            WritePreviews("trimmed", image);
            using (var overlay = AppState.Imread(Path.Combine(debugDir, "10_keep_overlay_full.jpg")))
                if (overlay is not null) WritePreviews("overlay", overlay, small: false);
            using var st = AppState.Imread(stitched);
            double? cut = st is null ? null : AppState.MapCutRatio(st, Path.Combine(debugDir, "09_keep_mask_full_after_island_filter.png"));
            if (cut is double c) result["mapCut"] = Core.Py.Round(c, 4);
        }
        Run("trim", args, Parsers.TrimParser(), Finish);
    }

    /// <summary>py: write_previews(kind, image, small)</summary>
    void WritePreviews(string kind, Mat image, bool small = true)
    {
        Directory.CreateDirectory(PreviewDir);
        var preview = AppState.ResizeMax(image, PREVIEW_MAX);
        try
        {
            AppState.ImwriteJpg(Path.Combine(PreviewDir, $"{kind}.jpg"), preview, 88);
            if (small)
            {
                var s = AppState.ResizeMax(preview, 360);
                AppState.ImwriteJpg(Path.Combine(PreviewDir, $"{kind}_small.jpg"), s, 80);
                if (!ReferenceEquals(s, preview)) s.Dispose();
            }
        }
        finally { if (!ReferenceEquals(preview, image)) preview.Dispose(); }
    }
}

/// <summary>py: server.JobStore</summary>
public sealed partial class JobStore
{
    readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    readonly object gate = new();
    [GeneratedRegex(@"^\d{8}_\d{6}$")] private static partial Regex IdPattern();

    public Job? Get(string? id)
    {
        if (id is null || !IdPattern().IsMatch(id)) return null;
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job))
            {
                if (!File.Exists(Path.Combine(AppState.WorkDir, id, "job.json"))) return null;
                jobs[id] = job = new Job(id);
            }
            return job;
        }
    }

    public Job Create()
    {
        var job = Job.Create();
        lock (gate) jobs[job.Id] = job;
        return job;
    }

    public JsonArray List()
    {
        if (!Directory.Exists(AppState.WorkDir)) return [];
        var ids = Directory.EnumerateDirectories(AppState.WorkDir).Where(d => File.Exists(Path.Combine(d, "job.json")))
            .Select(Path.GetFileName).Select(n => n!).OrderByDescending(n => n, StringComparer.Ordinal);
        return new JsonArray(ids.Select(Get).Where(j => j is not null).Select(j => (JsonNode?)j!.Summary()).ToArray());
    }

    /// <summary>py: trash(job) — 윈도우 휴지통으로(영구 삭제가 될 때는 윈도우가 물음). 시험은 TrashDir 폴더로 옮긴다</summary>
    public void Trash(Job job)
    {
        if (job.Running()) throw new InvalidOperationException("합치거나 찍는 중인 지도는 버릴 수 없어요");
        if (AppState.TrashDir is string td)
        {
            Directory.CreateDirectory(td);
            Directory.Move(job.Dir, Path.Combine(td, job.Id));
        }
        else if (!Win.RecycleBin.Send(job.Dir)) throw new InvalidOperationException("버리기를 그만뒀어요(휴지통에 안 들어가 영구 삭제를 물었을 때 취소)");
        lock (gate) jobs.Remove(job.Id);
    }

    public bool AnyRunning() { lock (gate) return jobs.Values.Any(j => j.Running() || j.CaptureActive()); }
}
