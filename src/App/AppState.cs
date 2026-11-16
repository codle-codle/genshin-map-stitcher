using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCvSharp;
using Size = OpenCvSharp.Size;
using Rect = OpenCvSharp.Rect;

namespace GenshinMap.App;

/// <summary>py: server.py 모듈 상수·경로·설정(load_settings·get_keys·set_keys·save_settings)과 그림 도우미</summary>
public static class AppState
{
    public static readonly string AppDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
    public static readonly string WebDir = Path.Combine(AppDir, "web");
    /// <summary>작업 폴더(py WORK_DIR). 시험 도구만 바꾼다</summary>
    public static string WorkDir = Path.Combine(AppDir, "작업");
    public static string SettingsPath => Path.Combine(WorkDir, "설정.json");
    /// <summary>버린 작업을 옮길 폴더. null(기본)이면 윈도우 휴지통으로 보낸다(개발자 결정 — py는 앱 폴더 부모의
    /// 임시휴지통 폴더로 옮겼다). 시험 도구만 폴더를 준다(휴지통 창이 뜨지 않게)</summary>
    public static string? TrashDir = null;
    public static readonly string Exe = Environment.ProcessPath!;

    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions JsonIndent2 = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true, IndentSize = 2 };
    public static readonly JsonSerializerOptions JsonIndent1 = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true, IndentSize = 1 };
    static readonly object settingsGate = new();

    // ---- 설정
    public static JsonObject LoadSettings()
    {
        try { return JsonNode.Parse(File.ReadAllText(SettingsPath, Encoding.UTF8)) as JsonObject ?? new JsonObject(); }
        catch (Exception) { return new JsonObject(); }
    }

    public static void SaveSettings(string key, JsonNode value)
    {
        lock (settingsGate)
        {
            var data = LoadSettings();
            data[key] = value;
            try
            {
                Directory.CreateDirectory(WorkDir);
                File.WriteAllText(SettingsPath, data.ToJsonString(JsonIndent1), new UTF8Encoding(false));
            }
            catch (IOException) { }
        }
    }

    static JsonObject DefaultKeys() => new()
    {
        ["start"] = new JsonObject { ["vk"] = 0x24, ["name"] = "Home" },
        ["stop"] = new JsonObject { ["vk"] = 0x23, ["name"] = "End" },
    };
    static readonly HashSet<int> RESERVED_VK = [0x01, 0x02, 0x04, 0x05, 0x06, 0x1B];

    /// <summary>py: get_keys()</summary>
    public static JsonObject GetKeys()
    {
        var keys = LoadSettings()["keys"] as JsonObject ?? new JsonObject();
        var o = new JsonObject();
        foreach (var (k, def) in DefaultKeys())
        {
            var merged = (JsonObject)def!.DeepClone();
            if (keys[k] is JsonObject user) foreach (var (kk, vv) in user) merged[kk] = vv?.DeepClone();
            o[k] = merged;
        }
        return o;
    }

    /// <summary>py: set_keys(keys) — 잘못이면 ArgumentException(py ValueError)</summary>
    public static JsonObject SetKeys(JsonNode? keys)
    {
        var clean = new JsonObject();
        foreach (var (k, def) in DefaultKeys())
        {
            var item = (keys as JsonObject)?[k] as JsonObject ?? (JsonObject)def!;
            int vk;
            try { vk = item["vk"] is JsonValue v ? (int)v.GetValue<double>() : 0; } catch (Exception) { vk = 0; }
            string name = (item["name"]?.ToString() ?? "").Trim();
            if (name.Length > 20) name = name[..20];
            if (!(0 < vk && vk < 255) || RESERVED_VK.Contains(vk) || name.Length == 0) throw new ArgumentException("그 키는 쓸 수 없어요");
            clean[k] = new JsonObject { ["vk"] = vk, ["name"] = name };
        }
        if (clean["start"]!["vk"]!.GetValue<int>() == clean["stop"]!["vk"]!.GetValue<int>()) throw new ArgumentException("시작 키와 멈추기 키는 달라야 해요");
        SaveSettings("keys", clean.DeepClone());
        return clean;
    }

    public static string NowText() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public static double UnixNow() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    // ---- 그림 도우미
    /// <summary>py: imread(path) — 컬러로 읽기(없으면 null)</summary>
    public static Mat? Imread(string path) => File.Exists(path) ? Core.ImageIO.ImreadUnicode(path) : null;

    /// <summary>py: imwrite_jpg(path, image, quality)</summary>
    public static bool ImwriteJpg(string path, Mat image, int quality = 85)
    {
        Cv2.ImEncode(".jpg", image, out byte[] buf, [new ImageEncodingParam(ImwriteFlags.JpegQuality, quality)]);
        File.WriteAllBytes(path, buf);
        return true;
    }

    /// <summary>py: resize_max(image, max_side) — 줄일 때만(INTER_AREA, 크기는 int로 자름). 안 줄이면 같은 Mat</summary>
    public static Mat ResizeMax(Mat image, int maxSide)
    {
        int h = image.Rows, w = image.Cols;
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(h, w));
        if (scale >= 1.0) return image;
        var o = new Mat();
        Cv2.Resize(image, o, new Size((int)(w * scale), (int)(h * scale)), 0, 0, InterpolationFlags.Area);
        return o;
    }

    public const int MOSAIC_W = 320;
    static readonly (double, double, double, double) MOSAIC_CROP = (520 / 2560.0, 212 / 1440.0, 2037 / 2560.0, 1112 / 1440.0);

    /// <summary>py: mosaic_box(w, h)</summary>
    public static (int X1, int Y1, int X2, int Y2) MosaicBox(int w, int h)
    {
        var (x1, y1, x2, y2) = MOSAIC_CROP;
        return ((int)(x1 * w), (int)(y1 * h), (int)(x2 * w), (int)(y2 * h));
    }

    /// <summary>py: write_mosaic_thumb(image, path) — 화면 가운데 지도만 잘라 가로 320px</summary>
    public static void WriteMosaicThumb(Mat image, string path)
    {
        var (x1, y1, x2, y2) = MosaicBox(image.Cols, image.Rows);
        using var crop = new Mat(image, new Rect(x1, y1, x2 - x1, y2 - y1));
        using var small = new Mat();
        Cv2.Resize(crop, small, new Size(MOSAIC_W, Core.Py.RoundEvenInt(MOSAIC_W * (double)(y2 - y1) / (x2 - x1))), 0, 0, InterpolationFlags.Area);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ImwriteJpg(path, small, 82);
    }

    /// <summary>py: map_cut_ratio(stitched, keep_mask_path) — 다듬기가 잘라 낸 지도 점 비율</summary>
    public static double? MapCutRatio(Mat stitched, string keepMaskPath)
    {
        using Mat? keep0 = File.Exists(keepMaskPath) ? Core.ImageIO.ImreadUnicode(keepMaskPath, ImreadModes.Grayscale) : null;
        if (keep0 is null) return null;
        using var small = new Mat();
        Cv2.Resize(stitched, small, new Size(0, 0), 0.25, 0.25, InterpolationFlags.Area);
        using var keepS = new Mat();
        Cv2.Resize(keep0, keepS, new Size(small.Cols, small.Rows), 0, 0, InterpolationFlags.Nearest);
        using var hsv = new Mat();
        Cv2.CvtColor(small, hsv, ColorConversionCodes.BGR2HSV);
        hsv.GetArray(out Vec3b[] hv);
        keepS.GetArray(out byte[] kv);
        long content = 0, cut = 0;
        for (int p = 0; p < hv.Length; p++)
        {
            byte S = hv[p].Item1, V = hv[p].Item2;
            bool c = V > 80 || (V >= 58 && S >= 150);
            if (!c) continue;
            content++;
            if (kv[p] <= 127) cut++;
        }
        return (double)cut / Math.Max(1, content);
    }
}
