using System.IO;
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
    private readonly MqttPublisher? _mqtt;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly StatusIcons _icons = new();

    // Boot trace — surfaces phase markers through three channels so a silent
    // "no tray icon, no error" startup is always diagnosable:
    //   1. Debug.WriteLine  → VS Output window when running in the debugger
    //   2. %APPDATA%\reon\bootlog.txt → survives a full release-mode run
    //   3. %TEMP%\reonosc_bootlog.txt → fallback if ConfigDir is unwritable
    // All file writes are best-effort and never throw.
    private static readonly string BootLogPath = SafeCombine(Settings.ConfigDir, "bootlog.txt");
    private static readonly string FallbackBootLogPath = SafeCombine(Path.GetTempPath(), "reonosc_bootlog.txt");
    private static string SafeCombine(string a, string b)
    {
        try { return Path.Combine(a, b); } catch { return b; }
    }
    private static void Trace(string phase)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [tid {Environment.CurrentManagedThreadId}] {phase}";
        try { System.Diagnostics.Debug.WriteLine($"[ReonOSC boot] {line}"); } catch { }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BootLogPath) ?? ".");
            File.AppendAllText(BootLogPath, line + Environment.NewLine);
        }
        catch
        {
            try { File.AppendAllText(FallbackBootLogPath, line + Environment.NewLine); } catch { }
        }
    }

    // Static ctor — confirms the type itself loaded. If this line never lands in
    // either log it tells us TrayContext's class init is failing (TypeInitializationException).
    static TrayContext()
    {
        try { File.WriteAllText(FallbackBootLogPath, $"{DateTime.Now:HH:mm:ss.fff} [static-cctor] TrayContext type init OK; APPDATA bootlog target={BootLogPath}{Environment.NewLine}"); } catch { }
    }

    public TrayContext()
    {
        Trace("--- TrayContext ctor begin ---");
        _settings = Settings.Load();
        Trace($"settings loaded (oscPort={_settings.OscPort} startMin={_settings.StartMinimised} mqtt={_settings.MqttEnabled})");
        _service = new ControlService(_settings);
        Trace("ControlService constructed");
        // Construct MQTT defensively: a broken MQTTnet load on a host without
        // the DLL alongside the exe must NOT prevent the tray icon from coming
        // up. The bridge tolerates a null publisher.
        try { _mqtt = new MqttPublisher(_service, _settings); Trace("MqttPublisher constructed"); }
        catch (Exception ex) { Trace($"MQTT init failed: {ex.GetType().Name}: {ex.Message}"); _mqtt = null; }
        _form = new MainForm(_service, _settings, _icons, _mqtt);
        Trace("MainForm constructed");

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

        Trace("Building NotifyIcon");
        _tray = new NotifyIcon
        {
            Icon = _icons.For(IconState.Off),
            Text = "ReonOSC — disconnected",
            Visible = true,
            ContextMenuStrip = menu,
        };
        Trace($"NotifyIcon visible={_tray.Visible} hasIcon={_tray.Icon != null}");
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
        try { _ = _form.Handle; Trace("form handle realized"); }
        catch (Exception ex) { Trace($"form handle FAILED: {ex.GetType().Name}: {ex.Message}"); }

        // OSC listener always starts on boot. Sanity-check the port first so a
        // bad persisted value doesn't leave the listener silent forever.
        if (_settings.OscPort < 1024 || _settings.OscPort > 65535)
        {
            _settings.OscPort = 9001;
            try { _settings.Save(); } catch { }
        }
        try { _service.StartOsc(); Trace("OSC start kicked"); }
        catch (Exception ex) { Trace($"OSC start FAILED: {ex.GetType().Name}: {ex.Message}"); }
        if (_settings.EnablePfSignalHook)
        {
            try { _service.PfSignal.Start(); Trace("PfSignal start kicked"); }
            catch (Exception ex) { Trace($"PfSignal start FAILED: {ex.GetType().Name}: {ex.Message}"); }
        }
        // MQTT publisher autostarts only when the user has configured a broker
        // and toggled it on. Failures land in the GUI log via the bridge.
        if (_mqtt is not null)
        {
            try { _ = _mqtt.StartAsync(); Trace("MQTT start kicked"); }
            catch (Exception ex) { Trace($"MQTT start FAILED: {ex.GetType().Name}: {ex.Message}"); }
        }

        Trace($"deciding ShowForm — StartMinimised={_settings.StartMinimised}");
        if (!_settings.StartMinimised)
            ShowForm();
        Trace("--- TrayContext ctor end ---");

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
        try { if (_mqtt is not null) _ = _mqtt.DisposeAsync(); } catch { }
        try { _ = _service.DisposeAsync(); } catch { }
        try { _icons.Dispose(); } catch { }
        base.ExitThreadCore();
    }
}
