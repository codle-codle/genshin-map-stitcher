using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GenshinMap.Core;
using GenshinMap.Core.Capture;
using OpenCvSharp;
using static GenshinMap.Core.Capture.CaptureConsts;

namespace GenshinMap.Win;

/// <summary>
/// 실제 원신 창(py: auto_capture.GenshinScreen): 클라이언트 영역을 찍고, SendInput으로 지도를 끈다.
/// 순서(입력 직전 전면 확인 → 창 검증 → 보냄, 전면 이탈이면 내 버튼만 놓고 복귀 대기)는 파이썬 그대로.
/// allowedExes: 대상 실행 파일 이름. 기본은 원신 둘. 시험할 때는 원신 대신 시험 창의 실행 파일을 넘긴다.
/// </summary>
public sealed class GenshinScreen : IScreen
{
    public static readonly string[] GENSHIN_EXES = ["genshinimpact.exe", "yuanshen.exe"];
    public const double LEAD_PX = 24, GLIDE_S = 0.30, HOLD_S = 0.40, HOLD_SHOT = 0.20;
    public static readonly (double X, double Y) PARK_AT = (40, 850);

    public nint Hwnd { get; }
    readonly uint pid;
    readonly HashSet<string> exes;
    readonly IClock time;
    public int Left { get; }
    public int Top { get; }
    public int W { get; }
    public int H { get; }
    readonly int vx, vy, vw, vh;
    readonly (int W, int H) cursorSize;
    bool owned;
    Action? onPress;
    public bool DragStarted { get; set; }
    public (double X, double Y) DragStart { get; private set; }
    public Func<bool>? StopCheck { get; set; }
    public bool WasPaused { get; set; }
    bool indicatorBusy, indicatorFinished, indicatorDisabled;
    double indicatorLast = double.NegativeInfinity;
    /// <summary>시험용: (사건 이름, 시각) — 누름·이끌기 끝·hold·놓음 시각을 잰다. 실제 판은 null</summary>
    public Action<string, double>? Probe { get; set; }

    public bool MovesByItself => true;
    public (double X, double Y)? Shown => null;
    public bool Blind() => false;
    public bool IsReal => true;

    public GenshinScreen(nint hwnd, IEnumerable<string>? allowedExes = null, IClock? clock = null)
    {
        Hwnd = hwnd;
        exes = new HashSet<string>(allowedExes ?? GENSHIN_EXES, StringComparer.Ordinal);
        time = clock ?? PreciseClock.Shared;
        Native.GetWindowThreadProcessId(hwnd, out pid);
        Native.GetClientRect(hwnd, out var rect);
        var origin = new Native.POINT();
        Native.ClientToScreen(hwnd, ref origin);
        Left = origin.x; Top = origin.y;
        W = rect.right; H = rect.bottom;
        vx = Native.GetSystemMetrics(76); vy = Native.GetSystemMetrics(77);
        vw = Native.GetSystemMetrics(78); vh = Native.GetSystemMetrics(79);
        cursorSize = (Native.GetSystemMetrics(13), Native.GetSystemMetrics(14));
    }

    bool Stop() => StopCheck?.Invoke() ?? false;

    /// <summary>py: geometry()</summary>
    (int, int, int, int) Geometry()
    {
        var origin = new Native.POINT();
        if (!Native.GetClientRect(Hwnd, out var rect) || !Native.ClientToScreen(Hwnd, ref origin))
            throw new CaptureHalt("원신 창 정보를 읽지 못해 멈췄어요.");
        return (origin.x, origin.y, rect.right, rect.bottom);
    }

    /// <summary>py: validate()</summary>
    void Validate()
    {
        if (Geometry() != (Left, Top, W, H)) throw new CaptureHalt("원신 창 위치나 크기가 바뀌어 멈췄어요. 다시 시작해 주세요.");
        if (Stop()) throw new Stopped();
        Native.GetWindowThreadProcessId(Hwnd, out uint p);
        if (p != pid) throw new CaptureHalt("대상 원신 창이 바뀌어 멈췄어요.");
        if (!exes.Contains(WinUtil.ProcessName(Hwnd))) throw new CaptureHalt("대상 원신 창이 바뀌어 멈췄어요.");
    }

    /// <summary>py: wait_resume() — 전면을 벗어났으면 내 버튼만 놓고, 5초 안정되게 돌아올 때까지(최대 120초) 기다린다</summary>
    public void WaitResume()
    {
        ReleaseOwn();
        double started = time.Monotonic();
        double? stable = null;
        while (time.Monotonic() - started < 120)
        {
            if (Stop()) throw new Stopped();
            if (StillHere())
            {
                Validate();
                stable ??= time.Monotonic();
                if (time.Monotonic() - stable.Value >= 5) { WasPaused = true; return; }
            }
            else stable = null;
            time.Sleep(.05);
        }
        throw new Stopped("원신 창으로 120초 안에 돌아오지 않아 멈췄어요.");
    }

    /// <summary>py: ready()</summary>
    public void Ready()
    {
        if (!StillHere()) WaitResume();
        Validate();
        try { IndicateWorking(); } catch (Exception) { }
    }

    static bool AnyButton() => new[] { 1, 2, 4, 5, 6 }.Any(WinUtil.KeyDown);

    /// <summary>py: indicate_working() — 찍는 중 표시(왼쪽 가림 띠 안에서 포인터를 작게 돌림). 같은 찍기 스레드의 틈에서만</summary>
    void IndicateWorking()
    {
        try
        {
            if (indicatorBusy || owned || indicatorFinished || indicatorDisabled) return;
            double now = time.Monotonic();
            if (now - indicatorLast < .05) return;
            double x = PARK_AT.X / REF_W * W, y = PARK_AT.Y / REF_H * H;
            var (cw, ch) = cursorSize;
            if (!(16 < x && x < 115 / REF_W * W - 12 - Math.Max(32, cw) && 16 < y && y < H - 12 - Math.Max(32, ch)))
            {
                indicatorDisabled = true;
                return;
            }
            if (!StillHere() || AnyButton()) return;
            indicatorLast = now;
            double angle = now * 2 * Math.PI;
            MoveTo(x + 12 * Math.Cos(angle), y + 12 * Math.Sin(angle));
        }
        catch (Exception) { indicatorDisabled = true; }
    }

    /// <summary>py: indicate_finished() — 끝나면 포인터를 가운데로</summary>
    public void IndicateFinished()
    {
        if (indicatorFinished) return;
        indicatorFinished = true;
        try
        {
            if (owned || !StillHere() || AnyButton()) return;
            var stopped = StopCheck;
            try
            {
                StopCheck = () => false;
                MoveTo(W / 2.0, H / 2.0);
            }
            finally { StopCheck = stopped; }
        }
        catch (Exception) { }
    }

    /// <summary>py: cursor() — 클라이언트 좌표</summary>
    public (double X, double Y) Cursor()
    {
        if (!Native.GetCursorPos(out var p)) throw new CaptureHalt("커서 위치를 읽지 못해 멈췄어요.");
        return (p.x - Left, p.y - Top);
    }

    void Inject(Native.INPUT e)
    {
        if (Native.SendInput(1, ref e, Marshal.SizeOf<Native.INPUT>()) != 1) throw new CaptureHalt("원신에 입력을 보내지 못해 멈췄어요.");
    }

    /// <summary>py: release_own() — 내가 누른 왼쪽 버튼만 놓는다</summary>
    public void ReleaseOwn()
    {
        if (owned)
        {
            Inject(new Native.INPUT { type = 0, ii = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dwFlags = Native.LEFTUP } } });
            owned = false;
        }
    }

    /// <summary>py: still_here()</summary>
    public bool StillHere() => Native.GetForegroundWindow() == Hwnd;

    /// <summary>py: _send(flags, x, y)</summary>
    void Send(uint flags, int x = 0, int y = 0)
    {
        if ((flags & Native.LEFTUP) != 0) { ReleaseOwn(); return; }
        if (!StillHere()) throw new FocusPause();
        Validate();
        if ((flags & Native.LEFTDOWN) != 0 && (owned || WinUtil.KeyDown(0x01))) throw new CaptureHalt("왼쪽 버튼이 이미 눌려 있어 멈췄어요.");
        int dx = x, dy = y;
        if ((flags & Native.ABSOLUTE) != 0)
        {
            if (!(0 <= x && x < W && 0 <= y && y < H)) throw new CaptureHalt("안전한 끌기 경로가 없어 멈췄어요.");
            int sx = Left + x, sy = Top + y;
            dx = (int)Py.RoundEven((sx - vx) * 65535.0 / Math.Max(1, vw - 1));
            dy = (int)Py.RoundEven((sy - vy) * 65535.0 / Math.Max(1, vh - 1));
            flags |= Native.VIRTUALDESK;
        }
        Inject(new Native.INPUT { type = 0, ii = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } } });
        if ((flags & Native.LEFTDOWN) != 0)
        {
            owned = DragStarted = true;
            Probe?.Invoke("down", time.PerfCounter());
            onPress?.Invoke();
        }
    }

    /// <summary>py: move_to(x, y) — int()로 자른 클라이언트 좌표</summary>
    public void MoveTo(double x, double y) => Send(Native.MOVE | Native.ABSOLUTE, (int)x, (int)y);

    /// <summary>py: _glide(x1, y1, x2, y2, seconds, ease) — 8ms 걸음. ease: 끝으로 갈수록 느려짐(1-(1-t)³)</summary>
    void Glide(double x1, double y1, double x2, double y2, double seconds, bool ease = false)
    {
        int steps = Math.Max(2, (int)(seconds / 0.008));
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            if (ease) t = 1 - Math.Pow(1 - t, 3);
            MoveTo(x1 + (x2 - x1) * t, y1 + (y2 - y1) * t);
            time.Sleep(seconds / steps);
        }
    }

    /// <summary>py: drag(x1, y1, x2, y2, on_hold) — 처음 24px를 0.06초에(문턱) → 0.05초 쉼 → 나머지를 끝으로 갈수록 느리게 0.30초 →
    /// 0.40초 멈췄다 놓기. onHold: 멈춘 지 0.20초에. onPress: 누른 직후(py: screen.on_press). 오류가 나도 마우스는 꼭 놓는다.</summary>
    public void Drag(double x1, double y1, double x2, double y2, Action? onPress, Action? onHold)
    {
        indicatorBusy = true;
        DragStarted = false;
        DragStart = (x1, y1);
        this.onPress = onPress;
        try
        {
            MoveTo(x1, y1);
            time.Sleep(0.08);
            Send(Native.LEFTDOWN);
            time.Sleep(0.05);
            double length = Py.Hypot(x2 - x1, y2 - y1);
            double lead = length != 0 ? Math.Min(1.0, LEAD_PX / length) : 1.0;
            double lx = x1 + (x2 - x1) * lead, ly = y1 + (y2 - y1) * lead;
            Glide(x1, y1, lx, ly, 0.06);
            Probe?.Invoke("lead_end", time.PerfCounter());
            time.Sleep(0.05);
            Glide(lx, ly, x2, y2, GLIDE_S, ease: true);
            double heldAt = time.Time();
            Probe?.Invoke("glide_end", time.PerfCounter());
            if (onHold is not null)
            {
                time.Sleep(HOLD_SHOT);
                Probe?.Invoke("hold_call", time.PerfCounter());
                onHold();
            }
            time.Sleep(Math.Max(0, HOLD_S - (time.Time() - heldAt)));
        }
        finally
        {
            this.onPress = null;
            try { ReleaseOwn(); Probe?.Invoke("up", time.PerfCounter()); }
            finally { indicatorBusy = false; }
        }
    }

    /// <summary>py: park_cursor()</summary>
    public void ParkCursor() => MoveTo(PARK_AT.X / REF_W * W, PARK_AT.Y / REF_H * H);

    /// <summary>py: grab() — 클라이언트 영역 BGR. GDI 영역 복사(SourceCopy만: 겹친 창은 안 들어감, PIL ImageGrab과 같음)</summary>
    public Mat Grab()
    {
        if (!StillHere()) throw new FocusPause();
        Validate();
        try { IndicateWorking(); } catch (Exception) { }
        var img = GrabRect(Left, Top, W, H);
        if (!StillHere()) { img.Dispose(); throw new FocusPause(); }
        Validate();
        return img;
    }

    /// <summary>화면 좌표 사각형을 BGR Mat으로(GDI BitBlt SRCCOPY)</summary>
    public static Mat GrabRect(int left, int top, int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var bgra = Mat.FromPixelData(h, w, MatType.CV_8UC4, data.Scan0, data.Stride);
            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            return bgr;
        }
        finally { bmp.UnlockBits(data); }
    }
}
