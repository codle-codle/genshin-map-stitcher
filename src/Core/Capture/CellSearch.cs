using static GenshinMap.Core.Capture.CaptureConsts;

namespace GenshinMap.Core.Capture;

/// <summary>
/// 칸 찾기. py: run_capture_fast 안의 닫힌 함수 nearest_cell(pos)·open_side(cell, d)·nearest_frontier().
/// 파이썬은 바깥 변수(topology, limits, cells, blocked, broken, sx, sy, st["cur"])를 같이 쓰고, C#은 FastGrid를 인자로 받는다
/// (sx, sy = grid.Step, limits = grid.Limits, cells·blocked·broken = grid의 것. 파이썬에서도 같은 객체다).
/// </summary>
public static class CellSearch
{
    /// <summary>py: nearest_cell(pos) — 지금 자리에서 가장 가까운 (대표) 칸. 열쇠: (벽 안으로 당긴 칸 중심까지 거리, 칸 중심까지 거리, 칸 번호)</summary>
    public static Cell NearestCell(FastGrid grid, (double X, double Y) pos)
    {
        double sx = grid.Step.X, sy = grid.Step.Y;
        int cx = Py.RoundEvenInt(pos.X / sx), cy = Py.RoundEvenInt(pos.Y / sy);
        var cand = new HashSet<Cell>();
        foreach (Cell c in grid.Cells.Keys) cand.Add(grid.Canonical(c));
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                cand.Add(grid.Canonical(new Cell(cx + dx, cy + dy)));
        Cell best = default;
        (double, double, Cell)? bestKey = null;
        foreach (Cell cell in cand)
        {
            (double, double) center = (cell.X * sx, cell.Y * sy);
            var key = (Py.Dist(grid.Limits.Clamp(center), pos), Py.Dist(center, pos), cell);
            if (bestKey is null || Less(key, bestKey.Value)) { bestKey = key; best = cell; }
        }
        return best;

        static bool Less((double, double, Cell) a, (double, double, Cell) b)
        {
            if (a.Item1 != b.Item1) return a.Item1 < b.Item1;
            if (a.Item2 != b.Item2) return a.Item2 < b.Item2;
            return a.Item3.CompareTo(b.Item3) < 0;
        }
    }

    /// <summary>py: open_side(cell, d) — 그 쪽으로 새 칸이 열려 있나(그 칸, 열림). 벽 안으로 당기면 같은 자리인 이웃은 안 열림.
    /// 칸이 찍은 칸이 아니면 파이썬처럼 KeyNotFoundException.</summary>
    public static (bool Open, Cell Neighbor) OpenSide(FastGrid grid, Cell cell, int d)
    {
        double sx = grid.Step.X, sy = grid.Step.Y;
        cell = grid.Canonical(cell);
        var (dx, dy) = DIRS[d];
        Cell nb = grid.Canonical(new Cell(cell.X + dx, cell.Y + dy));
        if (grid.Limits.Clamp((cell.X * sx, cell.Y * sy)) == grid.Limits.Clamp((nb.X * sx, nb.Y * sy)))
            return (false, nb);
        bool open = grid.Cells[cell][d] && !grid.Cells.ContainsKey(nb) && !grid.Blocked.Contains(nb) && !grid.Broken.Contains((cell, nb));
        return (open, nb);
    }

    /// <summary>py: nearest_frontier() — 지금 칸(cur)에서 찍은 칸만 밟아 가장 가까운 열린 쪽을 넓이 우선으로 찾는다.
    /// (가는 길: 처음 칸을 뺀 칸들, 새 칸, 방향 번호) 또는 null.</summary>
    public static (List<Cell> Path, Cell Neighbor, int Dir)? NearestFrontier(FastGrid grid, Cell cur)
    {
        Cell start = cur;
        var prev = new Dictionary<Cell, Cell?> { [start] = null };
        var q = new Queue<Cell>();
        q.Enqueue(start);
        while (q.Count > 0)
        {
            Cell u = q.Dequeue();
            for (int d = 0; d < 4; d++)
            {
                var (ok, nb) = OpenSide(grid, u, d);
                if (ok)
                {
                    var path = new List<Cell>();
                    Cell? w = u;
                    while (w is Cell c) { path.Add(c); w = prev[c]; }
                    path.Reverse();
                    path.RemoveAt(0);
                    return (path, nb, d);
                }
            }
            foreach (var (dx, dy) in DIRS)
            {
                Cell v = grid.Canonical(new Cell(u.X + dx, u.Y + dy));
                if (grid.Cells.ContainsKey(v) && !prev.ContainsKey(v) && !grid.Broken.Contains((u, v)))
                {
                    prev[v] = u;
                    q.Enqueue(v);
                }
            }
        }
        return null;
    }
}
