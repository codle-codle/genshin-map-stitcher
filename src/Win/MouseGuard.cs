using System.Runtime.InteropServices;

namespace GenshinMap.Win;

/// <summary>
/// py: MouseGuard — Home을 누른 뒤 찍는 동안 사용자가 직접 넣는 마우스 입력(움직임·휠·버튼)을 막는다.
/// 앱이 넣는 끌기(SendInput)는 '주입됨'(LLMHF_INJECTED) 표시가 있어 그대로 통과한다. 사용자가 클릭하면 그 클릭은 막고 바로 멈춤.
/// 훅 안에서는 깃발만 세운다(오래 걸리면 윈도우가 훅을 말없이 뗀다). 훅 대리자는 필드에 잡아 둔다(안 잡으면 치워져 앱이 죽는다).
/// </summary>
public sealed class MouseGuard : IDisposable
{
    static readonly uint[] DOWNS = [0x0201, 0x0204, 0x0207, 0x020B];
    const uint MOVE = 0x0200;
    static readonly uint[] WHEELS = [0x020A, 0x020E];

    readonly Action? onNudge;
    volatile bool active;
    public bool Ok { get; private set; }
    uint tid;
    nint hid;
    double moved, moveT, lastNudge = -1e9;
    readonly Native.HookProc proc; // 쓰레기로 치워지지 않게 붙잡아 둔다
    readonly ManualResetEventSlim ready = new();

    public MouseGuard(Action? onNudge = null)
    {
        this.onNudge = onNudge;
        proc = Hook;
        new Thread(Run) { IsBackground = true, Name = "마우스막기" }.Start();
        ready.Wait(3000);
    }

    void Run()
    {
        try
        {
            hid = Native.SetWindowsHookExW(14, proc, Native.GetModuleHandleW(0), 0); // WH_MOUSE_LL
            tid = Native.GetCurrentThreadId();
            Ok = hid != 0;
        }
        finally { ready.Set(); }
        if (!Ok) return;
        while (Native.GetMessageW(out _, 0, 0, 0) > 0) { }
        Native.UnhookWindowsHookEx(hid);
    }

    nint Hook(int code, nuint wParam, nint lParam)
    {
        try
        {
            if (code == 0 && active)
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                if ((info.flags & 0x1) == 0) // 사용자가 직접 넣은 입력
                {
                    uint w = (uint)wParam;
                    if (DOWNS.Contains(w))
                    {
                        active = false; // 클릭 = 멈추고 마우스 돌려줌(이 클릭은 게임에 안 감)
                        StopSignal.UserStop("click");
                        return 1;
                    }
                    double now = PreciseClock.Shared.Monotonic();
                    bool nudge;
                    if (w == MOVE)
                    {
                        Native.GetCursorPos(out var cur);
                        if (now - moveT > 1.5) moved = 0.0;
                        moveT = now;
                        moved += Math.Abs(info.pt.x - cur.x) + Math.Abs(info.pt.y - cur.y);
                        nudge = moved > 120;
                    }
                    else nudge = WHEELS.Contains(w);
                    if (nudge && now - lastNudge > 6 && onNudge is not null)
                    {
                        lastNudge = now;
                        moved = 0.0;
                        var a = onNudge;
                        new Thread(() => { try { a(); } catch (Exception) { } }) { IsBackground = true }.Start();
                    }
                    return 1; // 움직임·휠·버튼 뗌은 막는다
                }
            }
        }
        catch (Exception) { }
        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>py: block()</summary>
    public void Block()
    {
        StopSignal.Clear();
        active = Ok;
    }

    /// <summary>py: release()</summary>
    public void Release() => active = false;

    /// <summary>py: close()</summary>
    public void Dispose()
    {
        active = false;
        if (tid != 0) Native.PostThreadMessageW(tid, 0x0012, 0, 0); // WM_QUIT
    }
}
