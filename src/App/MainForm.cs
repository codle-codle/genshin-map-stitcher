using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GenshinMap.App;

/// <summary>
/// 앱 창. 화면(web/)은 그대로 두고 WebView2 요청 가로채기로 https://genshin.test/ 아래 정적 파일과 /api/...를 한 곳에서 처리한다
///. py: server.py(HTTP 서버) + Edge 앱 창(open_app_window, 1100×760).
/// 창을 닫아도 붙이기·다듬기·찍기가 돌고 있으면 숨긴 채 끝날 때까지 기다렸다 닫는다(py watchdog: 실행 중이면 기다림).
/// </summary>
sealed class MainForm : Form
{
    public const string Host = "https://genshin.test/";
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    CoreWebView2Environment? env;
    bool closingForReal;

    public MainForm()
    {
        Text = Win.WinUtil.APP_TITLE; // bring_app_front가 이 제목으로 찾는다
        ClientSize = new Size(1100, 760);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(AppState.Exe); } catch (Exception) { }
        Controls.Add(web);
        Shown += async (_, _) => await StartWeb();
        FormClosing += OnClosing;
    }

    async Task StartWeb()
    {
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenshinMap", "WebView2");
            env = await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions("--no-first-run"));
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnRequest;
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Host, StringComparison.Ordinal)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            web.Source = new Uri(Host);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "화면을 띄우지 못했어요(WebView2).\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    /// <summary>요청 하나: 본문은 UI 스레드에서 읽고, 처리는 다른 스레드에서(창이 얼지 않게), 응답은 UI 스레드에서 만든다</summary>
    async void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var req = e.Request;
            var uri = new Uri(req.Uri);
            string method = req.Method;
            byte[] body = [];
            if (req.Content is Stream s)
            {
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                body = ms.ToArray();
            }
            var res = await Task.Run(() => Api.Handle(method, uri, body));
            string reason = res.Status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 409 => "Conflict", _ => "Error" };
            string headers = $"Content-Type: {res.ContentType}\r\nCache-Control: {(res.Cache ? "max-age=31536000, immutable" : "no-cache")}";
            if (res.ContentType.StartsWith("application/json", StringComparison.Ordinal)) headers = $"Content-Type: {res.ContentType}\r\nCache-Control: no-store";
            e.Response = env!.CreateWebResourceResponse(new MemoryStream(res.Body), res.Status, reason, headers);
        }
        catch (Exception) { }
        finally { deferral.Complete(); }
    }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closingForReal || !Api.Store.AnyRunning()) return;
        // py watchdog: 실행 중인 작업이 있으면 끝날 때까지 서버를 안 끈다 → 창만 숨기고 기다린다
        e.Cancel = true;
        Hide();
        var timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += (_, _) =>
        {
            if (Api.Store.AnyRunning()) return;
            timer.Dispose();
            closingForReal = true;
            Close();
        };
        timer.Start();
    }
}
