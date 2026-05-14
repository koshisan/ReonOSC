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

    public TrayContext()
    {
        _settings = Settings.Load();
        _service = new ControlService(_settings);
        _form = new MainForm(_service, _settings);

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
            Icon = SystemIcons.Application,
            Text = "ReonOSC",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowForm();

        // Ensure the form handle exists even if we never show the window, so
        // BeginInvoke for log events works correctly.
        _ = _form.Handle;

        if (_settings.OscPort > 0)
        {
            try { _service.StartOsc(); }
            catch
            {
                // Port might be in use. The form's log will be empty at this point
                // because the handle isn't yet created; user will see no OSC running
                // and can retry from the GUI.
            }
        }

        if (!_settings.StartMinimised)
            ShowForm();

        // Defer auto-connect onto the UI thread post-construction so the log
        // captures it. Works whether the form is visible or hidden.
        if (_settings.AutoConnectOnStart)
            _form.BeginInvoke((Action)(async () => { try { await _form.ConnectAsync(); } catch { /* logged */ } }));
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
        base.ExitThreadCore();
    }
}
