using OpenCvSharp;

namespace GenshinMap.Core.Capture;

/// <summary>
/// 찍기 루프가 보는 시계. 실제 판은 고해상도 대기 타이머(Win), 시뮬 대조는 파이썬 시뮬 서버의 가짜 시계.
/// py: auto_capture가 쓰는 time.monotonic·time.time·time.perf_counter·time.sleep
/// </summary>
public interface IClock
{
    double Monotonic();
    double Time();
    double PerfCounter();
    void Sleep(double seconds);
}

/// <summary>
/// 찍기 루프가 보는 화면(py: GenshinScreen / SimulatedScreen이 빠른 찍기에 내주는 것).
/// Drag: onPress는 누른 직후·끌기 전, onHold는 끈 뒤 놓기 전에 부른다(py: screen.on_press, drag(on_hold=…)).
/// </summary>
public interface IScreen
{
    int W { get; }
    int H { get; }
    Mat Grab();
    (double X, double Y) Cursor();
    void Drag(double x1, double y1, double x2, double y2, Action? onPress, Action? onHold);
    void ParkCursor();
    bool StillHere();
    /// <summary>py: SimulatedScreen.moves_by_itself(시뮬만 뜻이 있음, 실제 판은 true)</summary>
    bool MovesByItself { get; }
    bool DragStarted { get; set; }
    /// <summary>py: screen.drag_start — 마지막 끌기의 시작점(중단된 끌기 기록용)</summary>
    (double X, double Y) DragStart { get; }
    /// <summary>시뮬의 정답 자리(마지막 grab 때). 실제 판은 null. py: tile["truth"] = screen.shown</summary>
    (double X, double Y)? Shown { get; }
    /// <summary>시뮬 --blind-wall: 지금 자리를 못 잰다고 칠까(파이썬 시험 도구가 LiveMap.locate를 가로채던 것). 실제 판은 false</summary>
    bool Blind();

    // ---- 실제 원신 창에서만 뜻이 있는 것(py: isinstance(screen, GenshinScreen)). 시뮬은 IsReal=false, 나머지는 아무 일 안 함
    bool IsReal { get; }
    /// <summary>py: GenshinScreen.ready() — 전면 창·창 크기 확인(문제면 FocusPause 등 예외)</summary>
    void Ready();
    bool WasPaused { get; set; }
    /// <summary>py: GenshinScreen.wait_resume — 전면에서 벗어났다가 돌아올 때까지 기다림</summary>
    void WaitResume();
    void ReleaseOwn();
    void IndicateFinished();
    /// <summary>py: screen.stop_check(실제 창이 끌기 도중 멈출지 묻는 함수)</summary>
    Func<bool>? StopCheck { get; set; }
}

/// <summary>py: Reporter(앱 서버와 연락). C#은 앱 안 함수 호출. 흉내 화면 시험은 아무 일 안 하는 것을 꽂는다</summary>
public interface IReporter
{
    bool StopRequested { get; }
    void Status(IReadOnlyDictionary<string, object?> data);
    void Upload(string name, Mat image);
}
