using System.Globalization;

namespace GenshinMap.App;

static class Program
{
    /// <summary>
    /// 인자가 없으면 앱 창(WebView2 화면 + /api, 5-7), 첫 인자가 stitch·trim이면 자식 프로세스 모드(붙이기·다듬기, 5-3·5-4).
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        // 소수점이 쉼표가 되는 윈도우 언어에서도 자식 프로세스 출력 줄이 깨지지 않게
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        if (args.Length > 0 && args[0] == "stitch") return GenshinMap.Core.Stitch.StitchCommand.Run(args[1..]);
        if (args.Length > 0 && args[0] == "trim") return GenshinMap.Core.Trim.EdgeTrimmer.Main(args[1..]);
        if (args.Length > 0 && args[0] == "engine") return GenshinMap.Core.Stitch.EngineStitch.Main(args[1..]);

        // 이미 켜져 있으면 그 창만 앞으로(py: 같은 포트가 열려 있으면 창만 다시 연다)
        using var single = new Mutex(true, @"Local\GenshinMapApp", out bool first);
        if (!first)
        {
            GenshinMap.Win.WinUtil.BringAppFront();
            return 0;
        }
        GenshinMap.Win.WinUtil.SetDpiAware();
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; // 끌기 도중 멈춤을 줄인다
        Directory.CreateDirectory(AppState.WorkDir);
        Job.CaptureLauncher = CaptureRunner.Launch;
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
