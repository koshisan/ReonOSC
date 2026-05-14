using ReonOSC.Ble;
using ReonOSC.Models;
using ReonOSC.Services;

namespace ReonOSC;

/// <summary>
/// Lifetime container for the whole app: builds settings, the control service,
/// the main form, and the tray icon. Application stays alive as long as the
/// tray icon is present, even when the form is hidden.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly Settings _settings;
    private readonly ControlService _service;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly StatusIcons _icons = new();

    public TrayContext()
    {
        _settings = Settings.Load();
        _service = new ControlService(_settings);
        _form = new MainForm(_service, _settings, _icons);

        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show window") { Font = new Font(menu.Font, FontStyle.Bold) };
        showItem.Click += (_, _) => ShowForm();
        var stopItem = new ToolStripMenuItem("Stop device");
        stopItem.Click += async (_, _) =>
        {
            try { await _service.Reon.StopAsync(); } catch { /* emergency stop is best-effort */ }
        };
        menu.Items.Add(showItem);
        menu.Items.Add(stopItem);
        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("Exit ReonOSC");
        exitItem.Click += (_, _) => ExitThread();
        menu.Items.Add(exitItem);

        _tray = new NotifyIcon
        {
            Icon = _icons.For(IconState.Off),
            Text = "ReonOSC — disconnected",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowForm();

        // Reflect device state in the tray icon and tooltip.
        _service.CommandSent += (_, cmd) =>
        {
            var iconState = cmd.Mode switch
            {
                ReonProtocol.Mode.Cool => IconState.Cool,
                ReonProtocol.Mode.Heat => IconState.Heat,
                ReonProtocol.Mode.Smart => IconState.Smart,
                _ => IconState.Off,
            };
            _tray.Icon = _icons.For(iconState);
            _tray.Text = cmd.Mode == ReonProtocol.Mode.Stop
                ? "ReonOSC — idle"
                : $"ReonOSC — {cmd.Mode} L{cmd.Level}";
            _form.UpdateIconState(iconState);
        };

        // Ensure the form handle exists even if we never show the window, so
        // the WebView2 can initialise and log events have somewhere to land.
        _ = _form.Handle;

        if (_settings.OscPort > 0)
        {
            try { _service.StartOsc(); } catch { /* surfaced via bridge log */ }
        }
        if (_settings.EnablePfSignalHook)
        {
            try { _service.PfSignal.Start(); } catch { /* surfaced via bridge log */ }
        }

        if (!_settings.StartMinimised)
            ShowForm();

        // Auto-connect is now triggered by the bridge when the UI signals
        // ui.ready (see WebViewBridge.OnUiReady).
    }

    private void ShowForm()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.BringToFront();
        _form.Activate();
    }

    protected override void ExitThreadCore()
    {
        try
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        catch { }
        try { _ = _service.DisposeAsync(); } catch { }
        try { _icons.Dispose(); } catch { }
        base.ExitThreadCore();
    }
}
