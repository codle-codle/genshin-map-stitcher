using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace GenshinMap.Win;

/// <summary>
/// 원신 창 위에 잠깐 뜨는 알림 띠. py: band.py(개발자가 승인한 시안의 모양).
/// - 보통 창 하나를 맨 위에 띄운다. 클릭은 아래(원신)로 지나가고 포커스도 안 가져간다(TRANSPARENT·NOACTIVATE).
/// - 화면 캡처에서 빠진다(SetWindowDisplayAffinity). 그래도 찍는 동안에는 숨긴다.
/// - 띠에서 무슨 일이 나도 찍기를 막지 않는다: 오류는 삼키고 띠만 안 뜬다.
/// 파이썬은 Tk 창 + 라벨, C#은 WinForms 창 하나에 직접 그린다(크기 계산은 시안과 같은 비율).
/// </summary>
public sealed class Band : IDisposable
{
    static readonly Dictionary<string, (Color Color, string Mark)> KINDS = new()
    {
        ["go"] = (ColorTranslator.FromHtml("#2f9e6b"), "▶"), ["warn"] = (ColorTranslator.FromHtml("#d9822b"), "!"),
        ["done"] = (ColorTranslator.FromHtml("#3b82f6"), "✓"), ["info"] = (ColorTranslator.FromHtml("#3b82f6"), "i"),
    };
    static readonly Color BG = ColorTranslator.FromHtml("#10141c"), TEXT = Color.White, SUB = ColorTranslator.FromHtml("#cfd6e0");
    const double REF_H = 900.0;
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    readonly bool exclude;
    BandForm? form;
    public bool Ok { get; private set; }
    volatile bool visible;
    public bool Visible => visible;
    int token;
    public List<string> Errors { get; } = new();
    /// <summary>마지막으로 띄운 띠의 화면 자리(left, top, right, bottom). 시험용</summary>
    public (int L, int T, int R, int B)? BoxRect { get; private set; }

    public Band(bool excludeFromCapture = true)
    {
        exclude = excludeFromCapture;
        var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            try
            {
                form = new BandForm();
                var h = form.Handle; // 창을 만든다(보이지 않게)
                if (exclude) Native.SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
                try { int round = 2; Native.DwmSetWindowAttribute(h, 33, ref round, 4); } catch (Exception) { } // 윈도우 11 둥근 모서리
                Ok = true;
            }
            catch (Exception e) { Errors.Add(e.ToString()); ready.Set(); return; }
            ready.Set();
            try { Application.Run(); } catch (Exception e) { Errors.Add(e.ToString()); }
            Ok = false;
            try { form.Dispose(); } catch (Exception) { }
        }) { IsBackground = true, Name = "알림띠" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait(5000);
    }

    void Post(Action a)
    {
        if (!Ok || form is null) return;
        try { form.BeginInvoke(() => { try { a(); } catch (Exception e) { Errors.Add(e.ToString()); } }); }
        catch (Exception e) { Errors.Add(e.ToString()); }
    }

    /// <summary>py: show(kind, title, sub, rect, seconds). rect = (left, top, width, height): 원신 창 안쪽 화면 좌표</summary>
    public void Show(string kind, string title, string sub, (int Left, int Top, int W, int H) rect, double? seconds = null)
        => Post(() => ShowNow(kind, title, sub, rect, seconds));

    /// <summary>py: hide(wait) — 숨기고 잠깐(기본 0.2초) 숨었는지 기다린다</summary>
    public void Hide(double wait = 0.2)
    {
        if (!Ok) return;
        Post(HideNow);
        var end = DateTime.UtcNow.AddSeconds(wait);
        while (visible && DateTime.UtcNow < end) Thread.Sleep(10);
    }

    /// <summary>py: wait_hidden(timeout)</summary>
    public void WaitHidden(double timeout)
    {
        var end = DateTime.UtcNow.AddSeconds(timeout);
        while (visible && DateTime.UtcNow < end) Thread.Sleep(50);
    }

    /// <summary>py: close()</summary>
    public void Dispose() => Post(() => { HideNow(); Application.ExitThread(); });

    void ShowNow(string kind, string title, string sub, (int Left, int Top, int W, int H) rect, double? seconds)
    {
        var f = form!;
        var (left, top, w, h) = rect;
        double s = Math.Max(0.6, h / REF_H);
        var (color, mark) = KINDS.TryGetValue(kind, out var k) ? k : KINDS["warn"];
        int Px(double v) => Math.Max(1, (int)Math.Round(v * s, MidpointRounding.ToEven));
        f.Set(color, mark, title, sub, Px);
        var (reqW, reqH) = f.Required();
        int bw = Math.Min((int)(w * 0.9), Math.Max((int)(640 * s), reqW));
        int bh = reqH;
        int x = (int)(left + (w - bw) / 2.0), y = top + Px(28);
        Native.SetWindowPos(f.Handle, -1, x, y, bw, bh, 0x0010 | 0x0040); // 맨 위·활성화 안 함·보이기
        Native.ShowWindow(f.Handle, 4); // SW_SHOWNOACTIVATE
        f.Invalidate();
        f.Update();
        BoxRect = (x, y, x + bw, y + bh);
        visible = true;
        int tok = ++token;
        if (seconds is double sec && sec > 0)
        {
            var timer = new System.Windows.Forms.Timer { Interval = (int)(sec * 1000) };
            timer.Tick += (_, _) => { timer.Dispose(); if (token == tok) HideNow(); };
            timer.Start();
        }
    }

    void HideNow()
    {
        if (form is not null) Native.ShowWindow(form.Handle, 0);
        visible = false;
    }

    sealed class BandForm : Form
    {
        Color color = KINDS["go"].Color;
        string mark = "", title = "", sub = "";
        Func<double, int> px = v => (int)v;
        Font? titleFont, subFont, markFont;

        public BandForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = BG;
            Location = new Point(-32000, -32000);
            Size = new Size(10, 10);
            DoubleBuffered = true;
        }

        // LAYERED·TRANSPARENT·NOACTIVATE·TOOLWINDOW·TOPMOST(py WS_EX). 만들 때부터 달아 포커스를 안 뺏는다
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080 | 0x00000008; return cp; }
        }
        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayered(Handle, 240); // 투명 창은 투명도를 정해야 보인다(py: SetLayeredWindowAttributes(hwnd, 0, 240, 2))
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
        static void SetLayered(nint h, byte alpha) => SetLayeredWindowAttributes(h, 0, alpha, 2);

        public void Set(Color c, string m, string t, string s, Func<double, int> scale)
        {
            color = c; mark = m; title = t; sub = s; px = scale;
            titleFont?.Dispose(); subFont?.Dispose(); markFont?.Dispose();
            titleFont = new Font("Malgun Gothic", px(24), FontStyle.Bold, GraphicsUnit.Pixel);
            subFont = new Font("Malgun Gothic", px(18), FontStyle.Regular, GraphicsUnit.Pixel);
            markFont = new Font("Malgun Gothic", px(24), FontStyle.Bold, GraphicsUnit.Pixel);
        }

        int IconW => px(44) + px(18) + px(28);
        int IconH => px(44) + px(36);

        /// <summary>py: Tk가 정한 필요 크기(winfo_reqwidth·reqheight)와 같은 짜임: 막대 | 아이콘 | 글(오른쪽 여백 28, 위아래 16)</summary>
        public (int W, int H) Required()
        {
            var tsz = TextRenderer.MeasureText(title, titleFont);
            var ssz = TextRenderer.MeasureText(sub, subFont);
            int textW = Math.Max(tsz.Width, ssz.Width), textH = tsz.Height + ssz.Height;
            return (px(6) + IconW + textW + px(28), Math.Max(IconH, textH + 2 * px(16)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BG);
            using (var b = new SolidBrush(color)) g.FillRectangle(b, 0, 0, px(6), Height);
            int d = px(44), x0 = px(6) + px(28), y0 = (Height - IconH) / 2 + px(18);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, x0, y0, d, d);
            TextRenderer.DrawText(g, mark, markFont, new Rectangle(x0, y0, d, d), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int tx = px(6) + IconW;
            var tsz = TextRenderer.MeasureText(title, titleFont);
            var ssz = TextRenderer.MeasureText(sub, subFont);
            int ty = (Height - tsz.Height - ssz.Height) / 2;
            TextRenderer.DrawText(g, title, titleFont, new Point(tx, ty), TEXT);
            TextRenderer.DrawText(g, sub, subFont, new Point(tx, ty + tsz.Height), SUB);
        }
    }
}
