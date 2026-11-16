using OpenCvSharp;
using static GenshinMap.Core.Capture.CaptureConsts;

namespace GenshinMap.Core.Capture;

/// <summary>끌기 보정 모델(py: model["g"], model["d0"]). 지도 이동 = g × (마우스 이동 − d0)</summary>
public readonly record struct DragModel(double G, double D0);

/// <summary>화면 좌표 계산과 끌기 좌표. py: auto_capture.content_box·fixed_ui_rect·map_view_rects·drag_points·press_spot</summary>
public static class Geometry
{
    /// <summary>py: content_box(w, h) — UI를 빼고 지도만 보이는 영역</summary>
    public static (int X1, int Y1, int X2, int Y2) ContentBox(int w, int h)
    {
        var (x1, y1, x2, y2) = CONTENT_BOX;
        return ((int)(x1 / REF_W * w), (int)(y1 / REF_H * h), (int)(x2 / REF_W * w), (int)(y2 / REF_H * h));
    }

    /// <summary>py: fixed_ui_rect(w, h) — 고정 금지 구역의 화면 px 사각형</summary>
    public static (int X1, int Y1, int X2, int Y2) FixedUiRect(int w, int h)
        => (0, 0, Py.RoundEvenInt(FIXED_UI.W * w), Py.RoundEvenInt(FIXED_UI.H * h));

    /// <summary>py: map_view_rects(w, h) — content_box에서 고정 금지 구역을 뺀 L자(겹치는 사각형 둘)</summary>
    public static (int X1, int Y1, int X2, int Y2)[] MapViewRects(int w, int h)
    {
        var (x1, y1, x2, y2) = ContentBox(w, h);
        var (_, _, ux, uy) = FixedUiRect(w, h);
        return [(Math.Max(x1, ux), y1, x2, y2), (x1, Math.Max(y1, uy), x2, y2)];
    }

    /// <summary>py: drag_points(w, h, vx, vy, shift, along, model) — 보는 자리를 (vx, vy)만큼 옮기는 끌기의 시작점·끝점.
    /// 지도는 커서를 따라가므로 마우스는 반대로. 잘못된 값이면 ArgumentException(파이썬 ValueError).</summary>
    public static ((double X, double Y) Start, (double X, double Y) End) DragPoints(int w, int h, double vx, double vy,
        double shift = 0.0, double along = 0.0, DragModel? model = null)
    {
        double length = Py.Hypot(vx, vy);
        if (!double.IsFinite(length) || length <= 0)
            throw new ArgumentException("drag target must be a finite nonzero vector");
        if (model is DragModel m0 && (!double.IsFinite(m0.G) || m0.G <= 0 || !double.IsFinite(m0.D0) || m0.D0 < 0))
            throw new ArgumentException("invalid drag calibration");
        double mouse = model is DragModel m ? length / m.G + m.D0 : (length + DRAG_LOSS) / DRAG_SCALE;
        double ux = -vx / length, uy = -vy / length;
        double mx = ux * mouse, my = uy * mouse;
        var (x1, y1, x2, y2) = ContentBox(w, h);
        double cx = (x1 + x2) / 2.0 - uy * shift + ux * along;
        double cy = (y1 + y2) / 2.0 + ux * shift + uy * along;
        return ((cx - mx / 2, cy - my / 2), (cx + mx / 2, cy + my / 2));
    }

    /// <summary>py: press_spot(image, vx, vy, tried, model) — 누를 자리 (shift, along). 끌기가 지도 영역 안에 들어가는 후보 중
    /// 누르는 곳 둘레가 가장 민무늬인 곳. tried(이미 눌러 본 시작점)와 150px 안인 후보는 뺀다. 후보가 없으면 null.
    /// uiFull: py STATIC_UI["full"](배운 화면 요소, 원래 크기, 0이 아니면 요소). 파이썬은 모듈 전역, C#은 인자로 받는다.</summary>
    public static (double Shift, double Along)? PressSpot(Mat image, double vx, double vy,
        IReadOnlyList<(double X, double Y)> tried, DragModel? model, Mat? uiFull)
    {
        int h = image.Rows, w = image.Cols;
        var (x1, y1, x2, y2) = ContentBox(w, h);
        var (_, _, ux, uy) = FixedUiRect(w, h);
        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        gray.GetArray(out byte[] g);
        byte[]? ui = null;
        if (uiFull is not null && uiFull.Rows == h && uiFull.Cols == w) uiFull.GetArray(out ui);

        (double, double) best = (0.0, 0.0);
        double? bestScore = null;
        foreach (int along in PRESS_ALONG)
        {
            foreach (int shift in PRESS_SHIFTS)
            {
                var (start, end) = DragPoints(w, h, vx, vy, shift, along, model);
                if (!(Inside(start) && Inside(end))) continue;
                // 공지 패널 자리(고정 금지 구역, 둘레 36px 여유)는 누르지도 놓지도 않는다(설계 3-6)
                if ((start.X < ux + 36 && start.Y < uy + 36) || (end.X < ux + 36 && end.Y < uy + 36)) continue;
                if (ui is not null && (UiNear(start) || UiNear(end))) continue;
                bool near = false;
                foreach (var t in tried)
                    if (Py.Hypot(start.X - t.X, start.Y - t.Y) < 150) { near = true; break; }
                if (near) continue;
                int px = (int)start.X, py = (int)start.Y;
                var (r0, r1) = Py.Slice(py - 36, py + 36, h);
                var (c0, c1) = Py.Slice(px - 36, px + 36, w);
                double score = Py.StdU8(g, w, r0, r1, c0, c1) + 0.004 * (Math.Abs(shift) + Math.Abs(along));
                if (bestScore is null || score < bestScore.Value)
                {
                    best = (shift, along);
                    bestScore = score;
                }
            }
        }
        return bestScore is not null ? best : null;

        bool Inside((double X, double Y) p) => x1 + 24 <= p.X && p.X <= x2 - 24 && y1 + 24 <= p.Y && p.Y <= y2 - 24;

        bool UiNear((double X, double Y) p)
        {
            // py: ui[max(0,int(p[1])-24):min(h,int(p[1])+25), max(0,int(p[0])-24):min(w,int(p[0])+25)].any()
            var (r0, r1) = Py.Slice(Math.Max(0, (int)p.Y - 24), Math.Min(h, (int)p.Y + 25), h);
            var (c0, c1) = Py.Slice(Math.Max(0, (int)p.X - 24), Math.Min(w, (int)p.X + 25), w);
            for (int y = r0; y < r1; y++)
                for (int x = c0; x < c1; x++)
                    if (ui![y * w + x] != 0) return true;
            return false;
        }
    }
}
