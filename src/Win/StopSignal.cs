namespace GenshinMap.Win;

/// <summary>py: USER_STOP(threading.Event)·STOP_HOW·user_stop(how) — 사용자가 멈춘 까닭(click·esc·end)</summary>
public static class StopSignal
{
    static volatile bool set;
    static string? how;
    static readonly object gate = new();

    public static bool IsSet => set;
    public static string? How { get { lock (gate) return how; } }

    /// <summary>py: user_stop(how) — 처음 까닭만 남긴다</summary>
    public static void UserStop(string why)
    {
        lock (gate) how ??= why;
        set = true;
    }

    /// <summary>py: MouseGuard.block 앞부분(USER_STOP.clear(); STOP_HOW["how"]=None)</summary>
    public static void Clear()
    {
        lock (gate) how = null;
        set = false;
    }
}

/// <summary>
/// py: KeyLatch — 키가 한 번이라도 눌렸나를 따로 지켜본다(0.02초마다). End와 Esc·마우스 클릭(StopSignal)도 멈춤으로 본다.
/// </summary>
public sealed class KeyLatch : IDisposable
{
    public const int VK_ESCAPE = 0x1B;
    readonly int vk;
    volatile bool hit, done;

    public bool Hit => hit;

    public KeyLatch(int vk)
    {
        this.vk = vk;
        new Thread(Watch) { IsBackground = true, Name = "키 감시" }.Start();
    }

    void Watch()
    {
        while (!done)
        {
            if (WinUtil.KeyDown(vk)) { StopSignal.UserStop("end"); hit = true; }
            else if (WinUtil.KeyDown(VK_ESCAPE)) { StopSignal.UserStop("esc"); hit = true; }
            else if (StopSignal.IsSet) hit = true;
            PreciseClock.Shared.Sleep(0.02);
        }
    }

    /// <summary>py: close()</summary>
    public void Dispose() => done = true;
}
