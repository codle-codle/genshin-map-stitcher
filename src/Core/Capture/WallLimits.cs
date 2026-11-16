using static GenshinMap.Core.Capture.CaptureConsts;

namespace GenshinMap.Core.Capture;

/// <summary>py: 끌기 판정 결과 중 WallLimits.observe가 보는 것(ver.confirmed, ver.wall, ver.pos)</summary>
public readonly record struct WallVerdict(bool Confirmed, bool Wall, (double X, double Y)? Pos);

/// <summary>
/// 배운 지도 끝(벽) 자리. py: auto_capture.WallLimits.
/// 열쇠 (축, 부호) → 벽 자리. 파이썬 dict는 넣은 순서로 돌고 clamp·canonical이 그 순서로 고치므로 넣은 순서를 지키는 사전을 쓴다.
/// </summary>
public sealed class WallLimits
{
    public OrderedDictionary<(int Axis, int Sign), double> Positions { get; } = new();
    public OrderedDictionary<(int Axis, int Sign), double> Candidates { get; } = new();

    /// <summary>py: clamp(pos) — 배운 벽 안쪽(WALL_MARGIN 여유)으로 자리를 당긴다</summary>
    public (double X, double Y) Clamp((double X, double Y) pos)
    {
        double[] result = [pos.X, pos.Y];
        foreach (var kv in Positions)
        {
            var (axis, sign) = kv.Key;
            double inside = kv.Value - sign * WALL_MARGIN;
            if (sign * result[axis] > sign * inside) result[axis] = inside;
        }
        return (result[0], result[1]);
    }

    /// <summary>py: observe(old, vector, ver, settled) — 목표보다 덜 간 정착에서 벽 후보를 배우고, 두 번 같은 자리(12px 안)거나 벽 판정이면 벽으로</summary>
    public void Observe((double X, double Y) old, (double X, double Y) vector, WallVerdict ver, bool settled)
    {
        if (!settled || !ver.Confirmed) return;
        double[] v = [vector.X, vector.Y], o = [old.X, old.Y];
        int axis = Math.Abs(v[1]) > Math.Abs(v[0]) ? 1 : 0;
        if (v[axis] == 0.0) return;
        int sign = v[axis] > 0 ? 1 : -1;
        var key = (axis, sign);
        var pos = ver.Pos ?? throw new InvalidOperationException("confirmed verdict without pos");
        double p = axis == 0 ? pos.X : pos.Y;
        // 정상 이동의 같은 열·행은 벽 근거가 아니다. 목표보다 덜 간 정착만 비교한다.
        double shortBy = sign * (o[axis] + v[axis] - p);
        if (shortBy <= Py.Max(ARRIVE_PX, .12 * Py.Hypot(vector.X, vector.Y))) return;
        bool hasPrev = Candidates.TryGetValue(key, out double previous);
        if (ver.Wall || (hasPrev && Math.Abs(previous - p) <= 12.0))
            Positions.TryAdd(key, p); // py: setdefault
        Candidates[key] = p;
    }
}
