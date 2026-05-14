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
        MinimumSize = new Size(900, 600);

        // Restore last-known size if reasonable, else pick a sensible default
        // that fits comfortably in the working area.
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int defaultW = Math.Min(1180, workArea.Width  - 80);
        int defaultH = Math.Min(760,  workArea.Height - 80);
        int w = _settings.WindowWidth  >= MinimumSize.Width  && _settings.WindowWidth  <= workArea.Width
              ? _settings.WindowWidth  : defaultW;
        int h = _settings.WindowHeight >= MinimumSize.Height && _settings.WindowHeight <= workArea.Height
              ? _settings.WindowHeight : defaultH;
        ClientSize = new Size(w, h);

        BackColor = Color.FromArgb(26, 31, 41); // matches the dark-theme --bg

        Controls.Add(_web);
        _ = InitWebViewAsync();

        // Persist the size on the events that actually fire reliably:
        //   ResizeEnd  — after a drag resize
        //   Resize     — after a Maximize / Restore (doesn't fire ResizeEnd)
        //   FormClosing — final failsafe
        ResizeEnd += (_, _) => SaveWindowSize();
        Resize    += OnResizeMaybeSave;
        FormClosing += (_, _) => SaveWindowSize();
    }

    private FormWindowState _lastWindowState = FormWindowState.Normal;
    private void OnResizeMaybeSave(object? sender, EventArgs e)
    {
        // Resize fires very often during a drag; ResizeEnd handles those.
        // Only persist here when the WindowState actually changed (Restore /
        // Maximize / Minimize), since those don't raise ResizeEnd.
        if (WindowState == _lastWindowState) return;
        _lastWindowState = WindowState;
        SaveWindowSize();
    }

    private void SaveWindowSize()
    {
        // When maximised, ClientSize is the maximised area — useless for next
        // launch. Use RestoreBounds (which carries the size the window had
        // before maximise) and subtract the chrome to get an equivalent
        // ClientSize value.
        int w, h;
        if (WindowState == FormWindowState.Normal)
        {
            w = ClientSize.Width;
            h = ClientSize.Height;
        }
        else if (WindowState == FormWindowState.Maximized && !RestoreBounds.IsEmpty)
        {
            var chrome = Size - ClientSize;
            w = Math.Max(MinimumSize.Width,  RestoreBounds.Width  - chrome.Width);
            h = Math.Max(MinimumSize.Height, RestoreBounds.Height - chrome.Height);
        }
        else
        {
            return; // minimised — don't overwrite stored size
        }

        if (_settings.WindowWidth == w && _settings.WindowHeight == h) return;
        _settings.WindowWidth = w;
        _settings.WindowHeight = h;
        try { _settings.Save(); } catch { /* ignore */ }
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

            // Leave DevTools available — right-click → Inspect from anywhere.
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            // Construct the bridge BEFORE Navigate so its WebMessageReceived
            // subscription is in place when the loaded JS posts 'ui.ready'.
            // Doing it from NavigationCompleted loses that first message and
            // OnUiReady never runs — which means the OSC retry/log path
            // doesn't run either.
            _bridge ??= new WebViewBridge(_web, _service, _settings);

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
