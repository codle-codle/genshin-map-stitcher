using System.Runtime.InteropServices;
using System.Text;

namespace GenshinMap.Win;

/// <summary>py: auto_capture의 user32·kernel32 ctypes 선언. INPUT은 x64에서 40바이트(확인함)</summary>
internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION ii; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nuint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; public uint lPrivate; }

    public const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, ref INPUT input, int size);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hwnd, ref POINT p);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint hwnd, StringBuilder buf, int n);
    public delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, nint lParam);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);

    public delegate nint HookProc(int code, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookExW(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint thread, uint msg, nuint w, nint l);

    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern nint GetModuleHandleW(nint name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageNameW(nint h, uint flags, StringBuilder buf, ref uint size);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint CreateWaitableTimerExW(nint attr, nint name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetWaitableTimer(nint timer, ref long due, int period, nint routine, nint arg, bool resume);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(nint h, uint ms);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}

/// <summary>py: auto_capture 윈도우 도우미(process_name, key_down, set_dpi_aware, bring_app_front)</summary>
public static class WinUtil
{
    /// <summary>py: process_name(hwnd) — 실행 파일 이름(소문자). 못 읽으면 ""</summary>
    public static string ProcessName(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        nint h = Native.OpenProcess(0x1000, false, pid);
        if (h == 0) return "";
        try
        {
            var buf = new StringBuilder(520);
            uint size = 520;
            Native.QueryFullProcessImageNameW(h, 0, buf, ref size);
            return Path.GetFileName(buf.ToString()).ToLowerInvariant();
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>py: key_down(vk)</summary>
    public static bool KeyDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>py: set_dpi_aware() — 모니터별 v2(실패하면 그대로. 앱은 manifest에도 적었다)</summary>
    public static void SetDpiAware()
    {
        try { Native.SetProcessDpiAwarenessContext(-4); } catch (EntryPointNotFoundException) { }
    }

    public const string APP_TITLE = "원신 지도 합치기";

    /// <summary>py: bring_app_front() — 다 찍었으면 앱 창을 맨 앞으로. 안 되면 false(작업 표시줄이 깜빡일 수는 있음)</summary>
    public static bool BringAppFront(string title = APP_TITLE) => BringFront(title, false, out _);

    /// <summary>py: bring_app_front(제목이 title로 시작) / server.bring_front_by_title(제목에 title이 듦, contains). found: 창을 찾았나</summary>
    public static bool BringAppFront(string title, bool contains) { BringFront(title, contains, out bool found); return found; }

    static bool BringFront(string title, bool contains, out bool foundAny)
    {
        foundAny = false;
        try
        {
            var found = new List<nint>();
            Native.EnumWindows((hwnd, _) =>
            {
                if (Native.IsWindowVisible(hwnd))
                {
                    int n = Native.GetWindowTextLengthW(hwnd);
                    var buf = new StringBuilder(n + 1);
                    Native.GetWindowTextW(hwnd, buf, n + 1);
                    string t = buf.ToString();
                    if (contains ? t.Contains(title, StringComparison.Ordinal) : t.StartsWith(title, StringComparison.Ordinal)) found.Add(hwnd);
                }
                return true;
            }, 0);
            if (found.Count == 0) return false;
            foundAny = true;
            nint target = found[0];
            nint fg = Native.GetForegroundWindow();
            uint me = Native.GetCurrentThreadId();
            uint other = Native.GetWindowThreadProcessId(fg, out _);
            bool attached = other != 0 && other != me && Native.AttachThreadInput(me, other, true);
            try
            {
                if (Native.IsIconic(target)) Native.ShowWindow(target, 9);
                Native.BringWindowToTop(target);
                return Native.SetForegroundWindow(target);
            }
            finally { if (attached) Native.AttachThreadInput(me, other, false); }
        }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// 지난 작업 버리기: 윈도우 휴지통으로 보낸다. 휴지통에 못 들어가 영구 삭제가 될 때는
/// 윈도우가 묻게 한다(FOF_WANTNUKEWARNING). 개발 PC에서 휴지통 API가 긴 경로를 묻지 않고 영구 삭제한 일이 있어서다.
/// </summary>
public static class RecycleBin
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public nint hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags;
        public bool fAnyOperationsAborted; public nint hNameMappings; public string? lpszProgressTitle;
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>폴더를 휴지통으로. 사용자가 영구 삭제 경고에서 취소하면 false(아무것도 안 지움). 실패면 IOException</summary>
    public static bool Send(string path)
    {
        int rc = 0; bool aborted = false;
        var t = new Thread(() =>
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE, pFrom = Path.GetFullPath(path) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING,
            };
            rc = SHFileOperationW(ref op);
            aborted = op.fAnyOperationsAborted;
        });
        t.SetApartmentState(ApartmentState.STA); // 셸 동작은 STA에서
        t.Start();
        t.Join();
        if (aborted) return false;
        if (rc != 0) throw new IOException($"휴지통으로 못 보냄(코드 {rc})");
        return true;
    }
}
