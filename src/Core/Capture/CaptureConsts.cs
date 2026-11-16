namespace GenshinMap.Core.Capture;

/// <summary>
/// 찍기 상수. py: auto_capture 모듈 상수(이름·값 그대로). 값마다 실측 근거는 README 4·5장.
/// 화면 너비에 따라 바뀌는 px 기준(SCALED_PX)만 static 필드다: ApplyScreenScale이 바꾼다(실제 원신 창에서만 부른다).
/// </summary>
public static class CaptureConsts
{
    public const double STEP_X = 0.45, STEP_Y = 0.35;
    public const double DRAG_SCALE = 1.0152, DRAG_LOSS = 19.1;
    public const double REF_W = 2048.0, REF_H = 1152.0;
    public const double SCREEN_REF_W = 2560.0;
    public static readonly (int X1, int Y1, int X2, int Y2) CONTENT_BOX = (145, 170, 1630, 890);
    /// <summary>py: grid_stitch.FIXED_UI(왼쪽 위 공지 패널 고정 금지 구역, 화면 비율)</summary>
    public static readonly (double W, double H) FIXED_UI = (0.1875, 0.398);
    public static readonly int[] PRESS_SHIFTS = [0, 120, -120, 240, -240, 360, -360];
    public static readonly int[] PRESS_ALONG = [0, 160, -160, 320, -320];

    // ---- 화면 너비에 비례하는 px 기준(py: SCALED_PX). 기본값은 2560 너비에서 정한 것.
    public const double BASE_SLIDE_PX = 80, BASE_SETTLE_PX = 3.0, BASE_SETTLE_SMALL = 1.0, BASE_HOLD_PHASE_MAX = 400.0,
        BASE_WALL_MARGIN = 3.0, BASE_BOUNCE_PX = 40, BASE_ARRIVE_PX = 60, BASE_NO_MOVE_PX = 40;
    public static double SLIDE_PX = BASE_SLIDE_PX, SETTLE_PX = BASE_SETTLE_PX, SETTLE_SMALL = BASE_SETTLE_SMALL,
        HOLD_PHASE_MAX = BASE_HOLD_PHASE_MAX, WALL_MARGIN = BASE_WALL_MARGIN, BOUNCE_PX = BASE_BOUNCE_PX,
        ARRIVE_PX = BASE_ARRIVE_PX, NO_MOVE_PX = BASE_NO_MOVE_PX;

    /// <summary>py: apply_screen_scale(width). 2560이면 그대로, 1920이면 0.75배. 배율(0.3~2.0)을 돌려준다.
    /// (파이썬은 capture_judge 상수도 바꾸지만 그 파일은 옛 길 전용이라 안 옮긴다)</summary>
    public static double ApplyScreenScale(double width)
    {
        double s = Py.Max(0.3, Py.Min(2.0, width / SCREEN_REF_W));
        SLIDE_PX = BASE_SLIDE_PX * s;
        SETTLE_PX = BASE_SETTLE_PX * s;
        SETTLE_SMALL = BASE_SETTLE_SMALL * s;
        HOLD_PHASE_MAX = BASE_HOLD_PHASE_MAX * s;
        WALL_MARGIN = BASE_WALL_MARGIN * s;
        BOUNCE_PX = BASE_BOUNCE_PX * s;
        ARRIVE_PX = BASE_ARRIVE_PX * s;
        NO_MOVE_PX = BASE_NO_MOVE_PX * s;
        return s;
    }

    /// <summary>시험용: 기본값(2560)으로 되돌린다.</summary>
    public static void ResetScreenScale() => ApplyScreenScale(SCREEN_REF_W);

    /// <summary>py: DIRS = {"right": (1, 0), "left": (-1, 0), "down": (0, 1), "up": (0, -1)} — 이 순서가 길 찾기 순서다.</summary>
    public static readonly string[] DIR_NAMES = ["right", "left", "down", "up"];
    public static readonly (int Dx, int Dy)[] DIRS = [(1, 0), (-1, 0), (0, 1), (0, -1)];
}

/// <summary>찍기 칸 번호(정수 두 개). py: 튜플 (x, y)</summary>
public readonly record struct Cell(int X, int Y) : IComparable<Cell>
{
    public int this[int axis] => axis == 0 ? X : Y;
    public Cell With(int axis, int v) => axis == 0 ? this with { X = v } : this with { Y = v };
    /// <summary>파이썬 튜플 비교(앞에서부터)</summary>
    public int CompareTo(Cell o) => X != o.X ? X.CompareTo(o.X) : Y.CompareTo(o.Y);
    public override string ToString() => $"({X}, {Y})";
}
