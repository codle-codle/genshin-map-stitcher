using static GenshinMap.Core.Capture.CaptureConsts;

namespace GenshinMap.Core.Capture;

/// <summary>
/// 찍은 칸과 길(빠른 찍기). 벽이 바뀌면 합쳐진 칸을 다시 열 수 있게 처음 방문(visits)을 따로 둔다. py: auto_capture.FastGrid.
/// edges는 DIRS 순서(right, left, down, up)의 bool 네 개. visits·cells·touches는 파이썬 dict처럼 넣은 순서를 지킨다.
/// raw_*는 찍기 루프가 직접 고친다(파이썬과 같이 공개).
/// </summary>
public sealed class FastGrid
{
    public WallLimits Limits { get; }
    /// <summary>py: topology.step — 시작 보정이 줄일 수 있다</summary>
    public (double X, double Y) Step { get; set; }
    /// <summary>이웃 두 화면이 각자 이만큼 어긋나도 겹침을 잃지 않는다</summary>
    public (double X, double Y) Tolerance { get; set; }

    public OrderedDictionary<Cell, bool[]> Visits { get; } = new();
    public OrderedDictionary<Cell, bool[]> Cells { get; } = new();
    public HashSet<Cell> Blocked { get; } = new();
    public HashSet<(Cell A, Cell B)> Broken { get; } = new();
    public OrderedDictionary<(Cell C, int Axis, int Sign), int> Touches { get; } = new();
    public HashSet<Cell> RawBlocked { get; } = new();
    public HashSet<(Cell A, Cell B)> RawBroken { get; } = new();
    public OrderedDictionary<(Cell C, int Axis, int Sign), int> RawTouches { get; } = new();
    public HashSet<Cell> Pending { get; } = new();
    public HashSet<Cell> Removed { get; } = new();
    public int Ghosts { get; set; }

    public FastGrid(WallLimits limits, (double X, double Y) step, (double X, double Y) usable)
    {
        Limits = limits;
        Step = step;
        Tolerance = (Tol(step.X, usable.X), Tol(step.Y, usable.Y));
        static double Tol(double s, double u) => Py.Min(s / 2, Py.Max(0.0, (u - s) / 2));
    }

    FastGrid(WallLimits limits, (double X, double Y) step, (double X, double Y) tolerance, bool _)
    {
        Limits = limits;
        Step = step;
        Tolerance = tolerance;
    }

    /// <summary>시험용: 기록된 허용치로 바로 만든다(정답지 상태를 그대로 되살릴 때).</summary>
    public static FastGrid WithTolerance(WallLimits limits, (double X, double Y) step, (double X, double Y) tolerance)
        => new(limits, step, tolerance, true);

    double StepOf(int axis) => axis == 0 ? Step.X : Step.Y;
    double TolOf(int axis) => axis == 0 ? Tolerance.X : Tolerance.Y;

    /// <summary>py: canonical(cell) — 배운 벽 너머의 칸 번호를 벽 쪽 대표 칸으로 모은다</summary>
    public Cell Canonical(Cell cell)
    {
        Cell result = cell;
        foreach (var kv in Limits.Positions)
        {
            var (axis, sign) = kv.Key;
            double s = StepOf(axis), boundary = kv.Value - sign * WALL_MARGIN;
            long nearest = Py.CeilToLong(boundary / s - .5); // nearest_cell breaks exact ties by cell number.
            long representative;
            if (Math.Abs(nearest * s - boundary) <= TolOf(axis))
                representative = nearest;
            else
                representative = sign > 0 ? Py.CeilToLong(boundary / s) : Py.FloorToLong(boundary / s);
            if ((long)sign * cell[axis] * s >= sign * boundary - TolOf(axis))
                result = result.With(axis, checked((int)representative));
        }
        return result;
    }

    /// <summary>py: rebuild()</summary>
    public void Rebuild()
    {
        Cells.Clear();
        foreach (var kv in Visits)
        {
            Cell rep = Canonical(kv.Key);
            if (!Cells.TryGetValue(rep, out bool[]? edges))
            {
                edges = new bool[4];
                Cells.Add(rep, edges);
            }
            for (int d = 0; d < 4; d++) edges[d] |= kv.Value[d];
        }
        Blocked.Clear();
        foreach (Cell c in RawBlocked) Blocked.Add(Canonical(c));
        foreach (Cell c in Cells.Keys) Blocked.Remove(c);
        Broken.Clear();
        foreach (var (a, b) in RawBroken)
        {
            Cell ca = Canonical(a), cb = Canonical(b);
            if (ca != cb) Broken.Add((ca, cb));
        }
        Touches.Clear();
        foreach (var kv in RawTouches)
        {
            var key = (Canonical(kv.Key.C), kv.Key.Axis, kv.Key.Sign);
            int old = Touches.TryGetValue(key, out int o) ? o : 0;
            Touches[key] = Math.Max(kv.Value, old);
        }
    }

    /// <summary>py: record(cell, edges, guessed)</summary>
    public void Record(Cell cell, bool[] edges, bool guessed)
    {
        Visits[cell] = (bool[])edges.Clone();
        if (guessed) Pending.Add(cell);
        else Pending.Remove(cell);
        Rebuild();
    }

    /// <summary>py: correct(delta) — 추정 중 찍은 칸을 자리를 되찾은 뒤 어긋났으면 칸마다 한 번만 뺀다</summary>
    public void Correct((double X, double Y) delta)
    {
        if (Math.Abs(delta.X) > Tolerance.X || Math.Abs(delta.Y) > Tolerance.Y)
        {
            foreach (Cell cell in Pending)
            {
                // 칸마다 한 번만 뺀다. 또 어긋나면 찍은 칸으로 남겨 두고 계속 찍는다(찍다가 멈추지 않기, 사진은 붙이기가 판단).
                if (Visits.ContainsKey(cell) && !Removed.Contains(cell))
                {
                    Visits.Remove(cell);
                    Ghosts++;
                    Removed.Add(cell);
                }
            }
        }
        Pending.Clear();
        Rebuild();
    }

    /// <summary>py: confirm_outside(pos) — 배운 벽 바깥에 실제로 서 있으면 그 벽을 지운다</summary>
    public void ConfirmOutside((double X, double Y) pos)
    {
        foreach (var kv in Limits.Positions.ToList())
        {
            var (axis, sign) = kv.Key;
            double p = axis == 0 ? pos.X : pos.Y;
            if (sign * (p - kv.Value) > 0)
            {
                Limits.Positions.Remove(kv.Key);
                Limits.Candidates.Remove(kv.Key);
            }
        }
        Rebuild();
    }
}
