using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ReonOSC.Bridge;
using ReonOSC.Models;
using ReonOSC.Services;

namespace ReonOSC;

/// <summary>
/// Single-WebView2 host. All the UI lives in <c>web/index.html</c>; the
/// <see cref="WebViewBridge"/> talks to the C# backend over WebView2
/// postMessage.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ControlService _service;
    private readonly Settings _settings;
    private readonly StatusIcons _icons;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private WebViewBridge? _bridge;

    public MainForm(ControlService service, Settings settings, StatusIcons icons)
    {
        _service = service;
        _settings = settings;
        _icons = icons;

        Text = "ReonOSC";
        Icon = _icons.For(IconState.Off);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(960, 640);
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        ClientSize = new Size(
            Math.Min(1280, workArea.Width - 80),
            Math.Min(800,  workArea.Height - 80));
        BackColor = Color.FromArgb(26, 31, 41); // matches the dark-theme --bg

        Controls.Add(_web);
        _ = InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async();
            var core = _web.CoreWebView2;

            // Serve web/ via a virtual host so relative URLs and module loaders work.
            var webFolder = Path.Combine(AppContext.BaseDirectory, "web");
            core.SetVirtualHostNameToFolderMapping(
                "reonosc.local",
                webFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            // Lock down what the page can do — no devtools menu unless debugging.
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
#endif
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            // Wire the bridge AFTER the page is loaded so PostWebMessageAsJson
            // arrives at a listener that exists.
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) return;
                _bridge ??= new WebViewBridge(_web, _service, _settings);
            };

            core.Navigate("https://reonosc.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Failed to initialise the embedded browser (WebView2). " +
                "On Windows 10 you may need to install the WebView2 Runtime from Microsoft.\n\n" +
                ex.Message,
                "ReonOSC",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void UpdateIconState(IconState state)
    {
        if (!IsHandleCreated) { Icon = _icons.For(state); return; }
        BeginInvoke(() => Icon = _icons.For(state));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window only hides it; the tray icon keeps the app alive.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _bridge?.Dispose(); } catch { }
            try { _web.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
