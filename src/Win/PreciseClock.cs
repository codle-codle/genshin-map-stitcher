using System.Diagnostics;
using GenshinMap.Core.Capture;

namespace GenshinMap.Win;

/// <summary>
/// 실제 판 시계. py: time.monotonic·time.time·time.perf_counter·time.sleep.
/// 파이썬 3.11+는 윈도우에서 time.sleep을 고해상도 대기 타이머(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)로 잔다 —
/// 끌기 한 걸음 8ms가 거의 그대로 자는 까닭. C#의 Thread.Sleep은 15.6ms 단위라 같은 타이머를 직접 쓴다.
/// </summary>
public sealed class PreciseClock : IClock
{
    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003, INFINITE = 0xFFFFFFFF;
    static readonly double Tick = 1.0 / Stopwatch.Frequency;

    public double Monotonic() => Stopwatch.GetTimestamp() * Tick;
    public double PerfCounter() => Stopwatch.GetTimestamp() * Tick;
    public double Time() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>py: time.sleep(seconds) — CPython pysleep과 같이 부를 때마다 고해상도 타이머를 만들어 잔다</summary>
    public void Sleep(double seconds)
    {
        if (!(seconds > 0)) return;
        long due = -(long)Math.Ceiling(seconds * 1e7); // 100ns 단위, 음수 = 지금부터
        nint timer = Native.CreateWaitableTimerExW(0, 0, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (timer == 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds)); // 아주 옛 윈도우(1803 이전)
            return;
        }
        try
        {
            if (!Native.SetWaitableTimer(timer, ref due, 0, 0, 0, false)) { Thread.Sleep(TimeSpan.FromSeconds(seconds)); return; }
            Native.WaitForSingleObject(timer, INFINITE);
        }
        finally { Native.CloseHandle(timer); }
    }

    public static readonly PreciseClock Shared = new();
}
