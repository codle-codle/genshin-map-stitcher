using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using GenshinMap.Win;
using OpenCvSharp;
using Size = OpenCvSharp.Size;
using Rect = OpenCvSharp.Rect;

namespace GenshinMap.App;

/// <summary>응답 하나(상태·형식·본문·캐시)</summary>
public sealed record ApiResponse(int Status, string ContentType, byte[] Body, bool Cache = false)
{
    public static ApiResponse Json(JsonNode data, int status = 200)
        => new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(data.ToJsonString(AppState.Json)));
    public static ApiResponse Error(string message, int status = 400) => Json(new JsonObject { ["error"] = message }, status);
}

/// <summary>
/// py: server.Handler(do_GET·do_PUT·do_POST)의 주소 나누기를 그대로. 화면(app.js)은 안 고친다.
/// WebView2 요청 가로채기가 이 함수를 부른다(UI 스레드 밖에서).
/// </summary>
public static class Api
{
    public static readonly JobStore Store = new();
    static readonly object pingGate = new();
    public static double LastPing = AppState.UnixNow();
    public static bool Bye;

    static readonly HashSet<string> PreviewNames =
        [.. new[] { "stitched", "trimmed", "overlay" }.SelectMany(k => new[] { $"{k}.jpg", $"{k}_small.jpg" })];

    static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".ico" => "image/x-icon", ".svg" => "image/svg+xml",
        ".json" => "application/json", _ => "application/octet-stream",
    };

    /// <summary>py: send_file(path, cache)</summary>
    static ApiResponse File(string path, bool cache = false)
        => System.IO.File.Exists(path) ? new(200, Mime(path), System.IO.File.ReadAllBytes(path), cache) : ApiResponse.Error("없음", 404);

    /// <summary>py: route() — 경로를 조각으로(%풀기), 질의</summary>
    static (string[] Parts, Dictionary<string, string> Query) Route(Uri uri)
    {
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=', 2);
            string k = Uri.UnescapeDataString(p[0].Replace('+', ' '));
            if (!q.ContainsKey(k)) q[k] = p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "";
        }
        return (parts, q);
    }

    public static ApiResponse Handle(string method, Uri uri, byte[] body)
    {
        try
        {
            return method switch { "GET" => Get(uri), "PUT" => Put(uri, body), "POST" => Post(uri, body), _ => ApiResponse.Error("없는 주소예요", 404) };
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return ApiResponse.Error(e.Message, 500);
        }
    }

    static ApiResponse Get(Uri uri)
    {
        var (parts, _) = Route(uri);
        if (parts.Length == 0) return File(Path.Combine(AppState.WebDir, "index.html"));
        if (parts[0] == "static" && parts.Length == 2) return File(Path.Combine(AppState.WebDir, Path.GetFileName(parts[1])));
        if (parts.Length == 2 && parts[0] == "api" && parts[1] == "jobs") return ApiResponse.Json(new JsonObject { ["jobs"] = Store.List() });
        if (parts.Length == 2 && parts[0] == "api" && parts[1] == "settings") return ApiResponse.Json(new JsonObject { ["keys"] = AppState.GetKeys() });
        if (parts.Length >= 3 && parts[0] == "api" && parts[1] == "jobs")
        {
            var job = Store.Get(parts[2]);
            if (job is null) return ApiResponse.Error("작업이 없어요", 404);
            if (parts.Length == 3)
            {
                var state = job.State();
                foreach (var kind in new[] { "stitch", "trim" })
                    if (state[kind] is JsonObject k && k["error"] is JsonNode err)
                        k["message"] = Parsers.FriendlyError(err.ToString());
                return ApiResponse.Json(state);
            }
            if (parts.Length == 5)
            {
                string name = Path.GetFileName(parts[4]);
                switch (parts[3])
                {
                    case "preview":
                        return PreviewNames.Contains(parts[4]) ? File(Path.Combine(job.PreviewDir, parts[4]), cache: true) : ApiResponse.Error("없음", 404);
                    case "shot": return File(Path.Combine(job.ThumbDir, name + ".jpg"), cache: true);
                    case "mosaic": return File(Path.Combine(job.MosaicDir, name + ".jpg"), cache: true);
                    case "photo":
                    {
                        string path = Path.Combine(job.InputDir, name);
                        using var image = System.IO.File.Exists(path) ? AppState.Imread(path) : null;
                        if (image is null) return ApiResponse.Error("없음", 404);
                        var small = AppState.ResizeMax(image, 1920);
                        try
                        {
                            Cv2.ImEncode(".jpg", small, out byte[] buf, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 88)]);
                            return new(200, "image/jpeg", buf, true);
                        }
                        finally { if (!ReferenceEquals(small, image)) small.Dispose(); }
                    }
                }
            }
        }
        return ApiResponse.Error("없는 주소예요", 404);
    }

    static ApiResponse Put(Uri uri, byte[] body)
    {
        var (parts, query) = Route(uri);
        if (parts.Length == 4 && parts[0] == "api" && parts[1] == "jobs" && parts[3] == "files")
        {
            var job = Store.Get(parts[2]);
            string name = query.GetValueOrDefault("name") ?? "";
            if (job is null || name.Length == 0) return ApiResponse.Error("잘못된 요청");
            if (job.Running()) return ApiResponse.Error("합치는 중에는 사진을 바꿀 수 없어요", 409);
            try { return ApiResponse.Json(new JsonObject { ["name"] = job.AddImage(name, body) }); }
            catch (ArgumentException e) { return ApiResponse.Error(e.Message); }
        }
        return ApiResponse.Error("없는 주소예요", 404);
    }

    static JsonObject ReadJson(byte[] body) => body.Length > 0 ? JsonNode.Parse(Encoding.UTF8.GetString(body)) as JsonObject ?? new JsonObject() : new JsonObject();

    static ApiResponse Post(Uri uri, byte[] body)
    {
        var (parts, _) = Route(uri);
        if (parts is ["api", "ping"]) { lock (pingGate) { LastPing = AppState.UnixNow(); Bye = false; } return ApiResponse.Json(new JsonObject { ["ok"] = true }); }
        if (parts is ["api", "bye"]) { lock (pingGate) { LastPing = AppState.UnixNow(); Bye = true; } return ApiResponse.Json(new JsonObject { ["ok"] = true }); }
        if (parts is ["api", "settings"])
        {
            try { return ApiResponse.Json(new JsonObject { ["keys"] = AppState.SetKeys(ReadJson(body)["keys"]) }); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException) { return ApiResponse.Error(e.Message); }
        }
        if (parts is ["api", "jobs"]) return ApiResponse.Json(Store.Create().State());
        if (parts.Length == 4 && parts[0] == "api" && parts[1] == "jobs")
        {
            var job = Store.Get(parts[2]);
            if (job is null) return ApiResponse.Error("작업이 없어요", 404);
            try
            {
                var b = ReadJson(body);
                switch (parts[3])
                {
                    case "stitch": job.StartStitch(); break;
                    case "trim": job.StartTrim(b["margin"] ?? 70, b["line"]?.ToString() ?? "cyan", b["islands"]?.ToString() ?? "default"); break;
                    case "cancel": job.Cancel(); break;
                    case "autocapture": job.StartCapture(b["max_cells"] ?? 0); break;
                    case "capture": return ApiResponse.Json(new JsonObject { ["stop"] = job.UpdateCapture(b) });
                    case "capture-stop": job.StopCapture(); break;
                    case "remove":
                        if (job.Running()) throw new InvalidOperationException("합치는 중에는 사진을 바꿀 수 없어요");
                        job.RemoveImage(b["name"]?.ToString() ?? "");
                        break;
                    case "trash": Store.Trash(job); return ApiResponse.Json(new JsonObject { ["ok"] = true });
                    case "open": OpenInWindows(job, b["what"]?.ToString() ?? "folder"); break;
                    default: return ApiResponse.Error("없는 동작이에요", 404);
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or FileNotFoundException or FormatException) { return ApiResponse.Error(e.Message); }
            return ApiResponse.Json(job.State());
        }
        return ApiResponse.Error("없는 주소예요", 404);
    }

    /// <summary>py: open_in_windows(job, what) — 탐색기·사진 앱으로 열고 그 창을 앞으로</summary>
    static void OpenInWindows(Job job, string what)
    {
        string title;
        if (what == "folder")
        {
            string target = Path.Combine(job.Dir, "trimmed.png");
            if (!System.IO.File.Exists(target)) target = Path.Combine(job.Dir, "stitched.png");
            if (System.IO.File.Exists(target)) Process.Start(new ProcessStartInfo("explorer", $"/select,\"{target}\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(job.Dir) { UseShellExecute = true });
            title = Path.GetFileName(job.Dir);
        }
        else if (what is "stitched" or "trimmed")
        {
            string path = Path.Combine(job.Dir, $"{what}.png");
            if (!System.IO.File.Exists(path)) throw new FileNotFoundException("아직 그 결과가 없어요");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            title = Path.GetFileName(path);
        }
        else throw new ArgumentException("열 수 없는 대상이에요");
        new Thread(() => BringFrontByTitle(title)) { IsBackground = true }.Start();
    }

    /// <summary>py: bring_front_by_title(part, timeout) — 제목에 part가 든 창이 뜨면 맨 앞으로</summary>
    static void BringFrontByTitle(string part, double timeout = 8.0)
    {
        var end = DateTime.UtcNow.AddSeconds(timeout);
        while (DateTime.UtcNow < end)
        {
            if (WinUtil.BringAppFront(part, contains: true)) return;
            Thread.Sleep(200);
        }
    }
}
