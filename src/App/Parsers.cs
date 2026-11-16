using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenshinMap.App;

/// <summary>py: server.py의 자식 프로세스 출력 줄 읽기(make_stitch_parser·make_trim_parser·mosaic_pair·unconnected_images·last_error_line·friendly_error)</summary>
public static partial class Parsers
{
    // 격자 붙이기의 예상 시간 비중(초). py STITCH_WORK, FEATURE_LINES_START
    const double W_FEATURES = 0.35, W_PAIR = 0.335, W_MERGE = 0.38, W_SAVE = 0.21, FEATURE_LINES_START = 0.65;
    public const int MOSAIC_RECENT = 6;

    static double Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue(out double d) ? d : 0.0;
    static void Upd(JsonObject o, params (string K, JsonNode? V)[] kv) { foreach (var (k, v) in kv) o[k] = v; }

    /// <summary>py: mosaic_pair(mosaic, pending, a, b, ok, ox, oy) — [live] 한 쌍을 모자이크에. 먼저 온 연결을 따른다</summary>
    public static void MosaicPair(JsonObject mosaic, Dictionary<string, List<(string, double, double)>> pending, string a, string b, bool ok, double ox, double oy)
    {
        int seq = mosaic["seq"]!.GetValue<int>() + 1;
        mosaic["seq"] = seq;
        var recent = mosaic["recent"]!.AsArray();
        recent.Add(new JsonArray(a, b, ok ? 1 : 0, seq));
        while (recent.Count > MOSAIC_RECENT) recent.RemoveAt(0);
        if (!ok) return;
        var pos = mosaic["pos"]!.AsObject();
        var start = new List<(string, double, double)>();
        bool hasA = pos.ContainsKey(a), hasB = pos.ContainsKey(b);
        if (hasA && !hasB) start.Add((b, pos[a]![0]!.GetValue<double>() + ox, pos[a]![1]!.GetValue<double>() + oy));
        else if (hasB && !hasA) start.Add((a, pos[b]![0]!.GetValue<double>() - ox, pos[b]![1]!.GetValue<double>() - oy));
        else if (!hasA && !hasB)
        {
            (pending.TryGetValue(a, out var la) ? la : pending[a] = new()).Add((b, ox, oy));
            (pending.TryGetValue(b, out var lb) ? lb : pending[b] = new()).Add((a, -ox, -oy));
            return;
        }
        else return;
        while (start.Count > 0)
        {
            var (name, x, y) = start[^1];
            start.RemoveAt(start.Count - 1);
            if (pos.ContainsKey(name)) continue;
            pos[name] = new JsonArray(Core.Py.Round(x, 1), Core.Py.Round(y, 1));
            mosaic["order"]!.AsArray().Add(name);
            if (pending.Remove(name, out var others))
                foreach (var (other, dx, dy) in others)
                    if (!pos.ContainsKey(other)) start.Add((other, x + dx, y + dy));
        }
        mosaic["v"] = mosaic["v"]!.GetValue<int>() + 1;
    }

    [GeneratedRegex(@"(\d+)쌍")] private static partial Regex PairCount();
    [GeneratedRegex(@"components=(\d+)")] private static partial Regex Components();

    /// <summary>py: make_stitch_parser(n, show_percent, mosaic). 반환한 함수는 잠금 안에서 줄마다 부른다</summary>
    public static Action<string, JsonObject> StitchParser(int n, bool showPercent = false, JsonObject? mosaic = null)
    {
        int features = 0, places = 0;
        double pairs = Math.Max(1, 3 * n), tested = 0, began = AppState.UnixNow();
        var pending = new Dictionary<string, List<(string, double, double)>>();
        double Total() => n * (W_FEATURES + W_MERGE + W_SAVE) + pairs * W_PAIR;
        void SetPercent(JsonObject live, double value, double? to = null, double seconds = 0.0)
        {
            if (!showPercent) return;
            value = Math.Min(99.0, Math.Max(Num(live, "percent"), value));
            Upd(live, ("percent", value), ("percent_to", Math.Max(value, Math.Min(99.0, to ?? value))), ("percent_at", AppState.UnixNow()), ("percent_seconds", seconds));
        }
        double DoneWork() => n * W_FEATURES + tested * W_PAIR;
        double Speed() => (AppState.UnixNow() - began) / Math.Max(1.0, DoneWork());

        return (line, live) =>
        {
            if (line.StartsWith("[input]", StringComparison.Ordinal))
            {
                began = AppState.UnixNow();
                double part = FEATURE_LINES_START * n * W_FEATURES;
                SetPercent(live, 0.0, 100 * part / Total(), part);
            }
            else if (line.StartsWith("[features]", StringComparison.Ordinal))
            {
                features++;
                Upd(live, ("phase", "사진 특징 읽는 중"), ("done", features), ("total", n), ("note", ""));
                double share = FEATURE_LINES_START + (1 - FEATURE_LINES_START) * features / n;
                SetPercent(live, 100 * share * n * W_FEATURES / Total());
            }
            else if (line.StartsWith("[pair-search] gap=", StringComparison.Ordinal))
            {
                string gap = line.Split('=', 2)[1].Trim();
                Upd(live, ("phase", "겹치는 곳 찾는 중"), ("done", 0), ("total", 0));
                live["gap"] = gap;
                var ms = PairCount().Matches(line.Split("(한도")[0]);
                if (ms.Count > 0) pairs = Math.Max(1, ms.Sum(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
            }
            else if (line.StartsWith("[live] ", StringComparison.Ordinal) && mosaic is not null)
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 6 && double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double ox)
                    && double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double oy))
                    MosaicPair(mosaic, pending, parts[1], parts[2], parts[3] == "1", ox, oy);
            }
            else if (line.StartsWith("[match]", StringComparison.Ordinal))
            {
                live["tested"] = (int)Num(live, "tested") + 1;
                tested++;
                pairs = Math.Max(pairs, tested);
                if (!line.EndsWith("failed", StringComparison.Ordinal)) live["valid"] = (int)Num(live, "valid") + 1;
                live["note"] = $"{(int)Num(live, "tested")}쌍 비교 · {(int)Num(live, "valid")}쌍 겹침";
                SetPercent(live, 100 * DoneWork() / Total());
            }
            else if (showPercent && line.StartsWith("[grid]", StringComparison.Ordinal))
            {
                pairs = Math.Max(1, tested);
                Upd(live, ("phase", "한 장으로 합치는 중"), ("done", 0), ("total", 0), ("note", ""));
                double merge = n * W_MERGE;
                SetPercent(live, 100 * DoneWork() / Total(), 100 * (DoneWork() + merge) / Total(), merge * Speed());
            }
            else if (showPercent && line.StartsWith("[seam]", StringComparison.Ordinal))
            {
                Upd(live, ("phase", "큰 그림 저장하는 중"), ("done", 0), ("total", 0), ("note", ""));
                double start = 100 * (DoneWork() + n * W_MERGE) / Total();
                SetPercent(live, start, 99.0, n * W_SAVE * Speed());
            }
            else if (line.Contains("components=") && line.StartsWith("[pair-search]", StringComparison.Ordinal))
            {
                var m = Components().Match(line);
                if (m.Success) live["groups"] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("[pair-search] fallback", StringComparison.Ordinal)) live["phase"] = "먼 사진까지 비교하는 중";
            else if (line.StartsWith("[place]", StringComparison.Ordinal))
            {
                places++;
                Upd(live, ("phase", "한 장으로 합치는 중"), ("done", places), ("total", n), ("note", ""));
                if (places == n) Upd(live, ("phase", "큰 그림 저장하는 중"), ("done", 0), ("total", 0));
            }
            else if (line.StartsWith("[done]", StringComparison.Ordinal))
            {
                Upd(live, ("phase", "저장 끝"), ("done", 0), ("total", 0));
                SetPercent(live, 99.0);
            }
        };
    }

    static readonly (string Key, string Label)[] TRIM_STEPS =
    [
        ("HSV/Canny", "경계선 찾는 중"), ("connected-component", "잡티 거르는 중"), ("reconstructing boundary", "경계 잇는 중"),
        ("flood filling", "바깥 칠하는 중"), ("preserving enclosed", "안쪽 지키는 중"), ("removing disconnected", "떨어진 얼룩 지우는 중"),
        ("upscaling", "원본 크기로 키우는 중"), ("final close", "가장자리 다듬는 중"), ("applying original-resolution margin", "여백 넣는 중"),
        ("smoothing final", "가장자리 다듬는 중"), ("applying keep mask", "잘라내는 중"),
    ];

    /// <summary>py: make_trim_parser()</summary>
    public static Action<string, JsonObject> TrimParser() => (line, live) =>
    {
        if (line.StartsWith("[step]", StringComparison.Ordinal))
        {
            for (int i = 0; i < TRIM_STEPS.Length; i++)
                if (line.Contains(TRIM_STEPS[i].Key))
                {
                    Upd(live, ("phase", TRIM_STEPS[i].Label), ("done", i + 1), ("total", TRIM_STEPS.Length + 1));
                    break;
                }
        }
        else if (line.StartsWith("[done]", StringComparison.Ordinal))
            Upd(live, ("phase", "확인용 그림 저장하는 중"), ("done", TRIM_STEPS.Length), ("total", TRIM_STEPS.Length + 1));
    };

    [GeneratedRegex(@"^\[match\] (.+?) -> (.+?): dx=")] private static partial Regex MatchLine();

    /// <summary>py: unconnected_images(images, lines) — [match] 성공 줄로 묶음을 다시 세서 가장 큰 묶음에 못 낀 사진</summary>
    public static List<string> UnconnectedImages(IReadOnlyList<string> images, IEnumerable<string> lines)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < images.Count; i++) index[images[i]] = i;
        var parent = Enumerable.Range(0, images.Count).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (string line in lines)
        {
            var m = MatchLine().Match(line);
            if (m.Success && index.TryGetValue(m.Groups[1].Value, out int ia) && index.TryGetValue(m.Groups[2].Value, out int ib))
            {
                int a = Find(ia), b = Find(ib);
                if (a != b) parent[b] = a;
            }
        }
        var groups = new Dictionary<int, List<string>>();
        var orderOfRoots = new List<int>();
        for (int i = 0; i < images.Count; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out var g)) { groups[r] = g = new(); orderOfRoots.Add(r); }
            g.Add(images[i]);
        }
        if (groups.Count <= 1) return [];
        var main = groups[orderOfRoots[0]];
        foreach (int r in orderOfRoots) if (groups[r].Count > main.Count) main = groups[r]; // py: max(..., key=len) — 같으면 앞의 것
        var set = new HashSet<string>(main, StringComparer.Ordinal);
        return images.Where(n => !set.Contains(n)).ToList();
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*m")] private static partial Regex Ansi();

    /// <summary>py: last_error_line(lines)</summary>
    public static string LastErrorLine(IReadOnlyList<string> lines)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
            if (lines[i].Trim().Length > 0 && !lines[i].StartsWith(' ')) return Ansi().Replace(lines[i], "").Trim();
        return "알 수 없는 오류";
    }

    /// <summary>py: friendly_error(text)</summary>
    public static string FriendlyError(string text)
    {
        if (text.Contains("Could not connect all images")) return "몇 장이 나머지와 이어지지 않았어요";
        if (text.Contains("No valid image pairs")) return "서로 겹치는 사진을 하나도 못 찾았어요";
        if (text.Contains("same size")) return "사진 크기가 서로 달라요";
        return text;
    }
}
