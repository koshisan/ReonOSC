using ReonOSC.Ble;
using ReonOSC.Models;
using ReonOSC.Services;

namespace ReonOSC;

public sealed class MainForm : Form
{
    private readonly ControlService _service;
    private Settings _settings;

    // Connection group
    private readonly Label _statusLabel = new();
    private readonly Button _connectButton = new();
    private readonly Button _disconnectButton = new();
    private readonly Button _pairButton = new();

    // OSC group
    private readonly NumericUpDown _portInput = new();
    private readonly Button _oscToggleButton = new();
    private readonly Label _oscStatusLabel = new();

    // OSC addresses
    private readonly TextBox _addrPfHot = new();
    private readonly TextBox _addrWater = new();
    private readonly TextBox _addrCold = new();
    private readonly TextBox _addrHeat = new();

    // Manual control
    private readonly CheckBox _manualOverride = new();
    private readonly ComboBox _manualMode = new();
    private readonly NumericUpDown _manualLevel = new();

    // Presets
    private readonly NumericUpDown _heatTouchInput = new();
    private readonly NumericUpDown _coldWaterInput = new();

    // Status / telemetry
    private readonly Label _resolvedLabel = new();
    private readonly Label _telemLabel = new();
    private readonly Label _inputsLabel = new();

    // Options
    private readonly CheckBox _startMinimisedBox = new();
    private readonly CheckBox _autoConnectBox = new();

    private readonly TextBox _logBox = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 250 };

    public MainForm(ControlService service, Settings settings)
    {
        _service = service;
        _settings = settings;

        Text = "ReonOSC";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(560, 720);
        ClientSize = new Size(560, 760);

        BuildLayout();
        BindControls();

        _service.Log += OnServiceLog;
        _service.CommandSent += OnCommandSent;
        _service.Reon.TelemetryReceived += OnTelemetry;

        _uiTimer.Tick += (_, _) => RefreshLiveLabels();
        _uiTimer.Start();
    }

    private void BuildLayout()
    {
        var root = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        Controls.Add(root);

        int y = 8;
        void Add(GroupBox g)
        {
            g.Location = new Point(8, y);
            g.Width = ClientSize.Width - 32; // leave space for vertical scrollbar
            g.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            root.Controls.Add(g);
            y += g.Height + 8;
        }

        Add(BuildConnectionGroup());
        Add(BuildOscGroup());
        Add(BuildManualGroup());
        Add(BuildPresetsGroup());
        Add(BuildStatusGroup());
        Add(BuildOptionsGroup());
        Add(BuildLogGroup());
    }

    private GroupBox BuildConnectionGroup()
    {
        var g = new GroupBox { Text = "Reon connection", Dock = DockStyle.Top, Height = 90 };
        _statusLabel.Text = "Status: disconnected";
        _statusLabel.AutoSize = true;
        _statusLabel.Location = new Point(12, 24);

        _connectButton.Text = "Connect";
        _connectButton.Location = new Point(12, 48);
        _connectButton.Size = new Size(120, 28);

        _disconnectButton.Text = "Disconnect";
        _disconnectButton.Location = new Point(140, 48);
        _disconnectButton.Size = new Size(120, 28);
        _disconnectButton.Enabled = false;

        _pairButton.Text = "Pair (device in pair mode)…";
        _pairButton.Location = new Point(270, 48);
        _pairButton.Size = new Size(200, 28);

        g.Controls.AddRange(new Control[] { _statusLabel, _connectButton, _disconnectButton, _pairButton });
        return g;
    }

    private GroupBox BuildOscGroup()
    {
        var g = new GroupBox { Text = "OSC server", Dock = DockStyle.Top, Height = 180 };

        var portLabel = new Label { Text = "UDP port:", AutoSize = true, Location = new Point(12, 28) };
        _portInput.Location = new Point(80, 24);
        _portInput.Size = new Size(80, 24);
        _portInput.Minimum = 1;
        _portInput.Maximum = 65535;
        _portInput.Value = _settings.OscPort;

        _oscToggleButton.Text = "Start";
        _oscToggleButton.Location = new Point(170, 22);
        _oscToggleButton.Size = new Size(80, 28);

        _oscStatusLabel.AutoSize = true;
        _oscStatusLabel.Location = new Point(260, 28);
        _oscStatusLabel.Text = "(stopped)";

        var addrLabel = new Label
        {
            Text = "OSC addresses (edit to match your sender):",
            AutoSize = true,
            Location = new Point(12, 56),
        };

        SetupAddrRow(_addrPfHot, "PFHotHigh:", 12, 80, _settings.AddrPfHotHigh);
        SetupAddrRow(_addrWater, "water (bool):", 12, 104, _settings.AddrWater);
        SetupAddrRow(_addrCold,  "cold (float):", 12, 128, _settings.AddrCold);
        SetupAddrRow(_addrHeat,  "heat (float):", 12, 152, _settings.AddrHeat);

        g.Controls.AddRange(new Control[]
        {
            portLabel, _portInput, _oscToggleButton, _oscStatusLabel, addrLabel,
            _addrPfHot.Tag as Label ?? new Label(), _addrPfHot,
            _addrWater.Tag as Label ?? new Label(), _addrWater,
            _addrCold.Tag as Label ?? new Label(), _addrCold,
            _addrHeat.Tag as Label ?? new Label(), _addrHeat,
        });
        return g;
    }

    private void SetupAddrRow(TextBox tb, string label, int x, int y, string initial)
    {
        var lbl = new Label { Text = label, Location = new Point(x, y + 4), Size = new Size(100, 20) };
        tb.Location = new Point(x + 110, y);
        tb.Size = new Size(380, 22);
        tb.Text = initial;
        tb.Tag = lbl;
    }

    private GroupBox BuildManualGroup()
    {
        var g = new GroupBox { Text = "Manual control", Dock = DockStyle.Top, Height = 80 };
        _manualOverride.Text = "Manual override (ignore OSC)";
        _manualOverride.Location = new Point(12, 24);
        _manualOverride.AutoSize = true;

        var modeLabel = new Label { Text = "Mode:", Location = new Point(12, 50), AutoSize = true };
        _manualMode.Location = new Point(60, 46);
        _manualMode.Size = new Size(80, 24);
        _manualMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _manualMode.Items.AddRange(new object[] { "Stop", "Cool", "Heat" });
        _manualMode.SelectedIndex = 0;

        var lvlLabel = new Label { Text = "Level:", Location = new Point(160, 50), AutoSize = true };
        _manualLevel.Location = new Point(210, 46);
        _manualLevel.Size = new Size(60, 24);
        _manualLevel.Minimum = ReonProtocol.LevelMin;
        _manualLevel.Maximum = ReonProtocol.LevelMax;
        _manualLevel.Value = 0;

        g.Controls.AddRange(new Control[]
        {
            _manualOverride, modeLabel, _manualMode, lvlLabel, _manualLevel
        });
        return g;
    }

    private GroupBox BuildPresetsGroup()
    {
        var g = new GroupBox { Text = "Preset levels for OSC triggers", Dock = DockStyle.Top, Height = 60 };

        var lbl1 = new Label { Text = "Heat Touch (PFHotHigh=1):", Location = new Point(12, 28), AutoSize = true };
        _heatTouchInput.Location = new Point(200, 24);
        _heatTouchInput.Size = new Size(50, 24);
        _heatTouchInput.Minimum = 0;
        _heatTouchInput.Maximum = ReonProtocol.LevelMax;
        _heatTouchInput.Value = _settings.HeatTouchLevel;

        var lbl2 = new Label { Text = "Cold Water (water=1):", Location = new Point(280, 28), AutoSize = true };
        _coldWaterInput.Location = new Point(420, 24);
        _coldWaterInput.Size = new Size(50, 24);
        _coldWaterInput.Minimum = 0;
        _coldWaterInput.Maximum = ReonProtocol.LevelMax;
        _coldWaterInput.Value = _settings.ColdWaterLevel;

        g.Controls.AddRange(new Control[] { lbl1, _heatTouchInput, lbl2, _coldWaterInput });
        return g;
    }

    private GroupBox BuildStatusGroup()
    {
        var g = new GroupBox { Text = "Live status", Dock = DockStyle.Top, Height = 110 };
        _resolvedLabel.Text = "Current: idle";
        _resolvedLabel.Location = new Point(12, 24);
        _resolvedLabel.AutoSize = true;
        _resolvedLabel.Font = new Font(Font, FontStyle.Bold);

        _telemLabel.Text = "Plate: —   Sink: —   Board: —   Ambient: —";
        _telemLabel.Location = new Point(12, 48);
        _telemLabel.AutoSize = true;

        _inputsLabel.Text = "OSC inputs: PFHotHigh=0  water=0  cold=0.00  heat=0.00";
        _inputsLabel.Location = new Point(12, 72);
        _inputsLabel.AutoSize = true;

        g.Controls.AddRange(new Control[] { _resolvedLabel, _telemLabel, _inputsLabel });
        return g;
    }

    private GroupBox BuildOptionsGroup()
    {
        var g = new GroupBox { Text = "Options", Dock = DockStyle.Top, Height = 80 };
        _startMinimisedBox.Text = "Start minimised to tray";
        _startMinimisedBox.Location = new Point(12, 24);
        _startMinimisedBox.AutoSize = true;
        _startMinimisedBox.Checked = _settings.StartMinimised;

        _autoConnectBox.Text = "Auto-connect to Reon on start";
        _autoConnectBox.Location = new Point(12, 48);
        _autoConnectBox.AutoSize = true;
        _autoConnectBox.Checked = _settings.AutoConnectOnStart;

        g.Controls.AddRange(new Control[] { _startMinimisedBox, _autoConnectBox });
        return g;
    }

    private GroupBox BuildLogGroup()
    {
        var g = new GroupBox { Text = "Log", Dock = DockStyle.Top, Height = 160 };
        _logBox.Multiline = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.ReadOnly = true;
        _logBox.Location = new Point(12, 22);
        _logBox.Size = new Size(520, 130);
        _logBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _logBox.Font = new Font(FontFamily.GenericMonospace, 8.5f);
        g.Controls.Add(_logBox);
        return g;
    }

    private void BindControls()
    {
        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += async (_, _) => await DisconnectAsync();
        _pairButton.Click += async (_, _) => await PairAsync();

        _oscToggleButton.Click += (_, _) =>
        {
            if (_service.Osc.IsRunning)
            {
                _service.StopOsc();
            }
            else
            {
                try { _service.StartOsc(); }
                catch (Exception ex)
                {
                    AppendLog($"OSC start failed: {ex.Message}");
                    MessageBox.Show(this, ex.Message, "Cannot start OSC listener",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            RefreshOscButton();
        };

        _portInput.ValueChanged += (_, _) => { _settings.OscPort = (int)_portInput.Value; PersistSettings(); };
        _addrPfHot.Leave += (_, _) => { _settings.AddrPfHotHigh = _addrPfHot.Text; PersistSettings(); };
        _addrWater.Leave += (_, _) => { _settings.AddrWater = _addrWater.Text; PersistSettings(); };
        _addrCold.Leave  += (_, _) => { _settings.AddrCold  = _addrCold.Text;  PersistSettings(); };
        _addrHeat.Leave  += (_, _) => { _settings.AddrHeat  = _addrHeat.Text;  PersistSettings(); };

        _heatTouchInput.ValueChanged += (_, _) => { _settings.HeatTouchLevel = (int)_heatTouchInput.Value; PersistSettings(); };
        _coldWaterInput.ValueChanged += (_, _) => { _settings.ColdWaterLevel = (int)_coldWaterInput.Value; PersistSettings(); };

        _manualOverride.CheckedChanged += async (_, _) => { _service.ManualOverride = _manualOverride.Checked; await _service.ReconcileAsync(); };
        _manualMode.SelectedIndexChanged += async (_, _) => { UpdateManualCommand(); await _service.ReconcileAsync(); };
        _manualLevel.ValueChanged += async (_, _) => { UpdateManualCommand(); await _service.ReconcileAsync(); };

        _startMinimisedBox.CheckedChanged += (_, _) => { _settings.StartMinimised = _startMinimisedBox.Checked; PersistSettings(); };
        _autoConnectBox.CheckedChanged += (_, _) => { _settings.AutoConnectOnStart = _autoConnectBox.Checked; PersistSettings(); };
    }

    private void UpdateManualCommand()
    {
        var mode = _manualMode.SelectedIndex switch
        {
            1 => ReonProtocol.Mode.Cool,
            2 => ReonProtocol.Mode.Heat,
            _ => ReonProtocol.Mode.Stop,
        };
        _service.ManualCommand = new ResolvedCommand(mode, (int)_manualLevel.Value);
    }

    private void PersistSettings()
    {
        _service.ApplySettings(_settings);
        try { _settings.Save(); } catch { /* TODO: surface to log */ }
    }

    public async Task ConnectAsync()
    {
        _connectButton.Enabled = false;
        try
        {
            ulong? addr = null;
            if (!string.IsNullOrWhiteSpace(_settings.LastKnownMac))
            {
                try { addr = ReonClient.ParseMac(_settings.LastKnownMac); }
                catch { addr = null; }
            }
            if (addr is null)
            {
                AppendLog("Scanning for RNP-3 …");
                addr = await ReonClient.FindReonAsync(TimeSpan.FromSeconds(8));
                if (addr is null) { AppendLog("Reon not found."); return; }
                _settings.LastKnownMac = ReonClient.FormatMac(addr.Value);
                PersistSettings();
            }

            var stored = TokenStorage.Load();
            if (stored is null)
            {
                AppendLog("No bond token saved. Use the Pair button while the device is in pair mode.");
                return;
            }

            await _service.Reon.ConnectAsync(addr.Value, stored.TokenBytes);
            _statusLabel.Text = $"Status: connected to {ReonClient.FormatMac(addr.Value)}";
            _disconnectButton.Enabled = true;
        }
        catch (Exception ex)
        {
            AppendLog($"Connect failed: {ex.Message}");
            _connectButton.Enabled = true;
        }
    }

    public async Task DisconnectAsync()
    {
        try { await _service.Reon.DisposeAsync(); }
        catch (Exception ex) { AppendLog($"Disconnect: {ex.Message}"); }
        _statusLabel.Text = "Status: disconnected";
        _connectButton.Enabled = true;
        _disconnectButton.Enabled = false;
    }

    private async Task PairAsync()
    {
        var result = MessageBox.Show(this,
            "Make sure your Reon is in pair mode (long-press the button until the LED " +
            "indicates pairing) and your phone's Bluetooth is OFF so the Sony app can't " +
            "grab the device.\n\nProceed?",
            "ReonOSC pair", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (result != DialogResult.OK) return;

        _pairButton.Enabled = false;
        try
        {
            AppendLog("Scanning for RNP-3 …");
            var addr = await ReonClient.FindReonAsync(TimeSpan.FromSeconds(8));
            if (addr is null) { AppendLog("Reon not found."); return; }

            var token = new byte[ReonProtocol.AuthTokenLength];
            token[0] = 0x01; // version-marker prefix matching what the Sony app uses
            System.Security.Cryptography.RandomNumberGenerator.Fill(token.AsSpan(1));

            await using (var temp = new ReonClient())
            {
                temp.Log += OnServiceLog;
                await temp.PairAsync(addr.Value, token);
            }

            _settings.LastKnownMac = ReonClient.FormatMac(addr.Value);
            PersistSettings();
            AppendLog("Paired. You can now Connect.");
        }
        catch (Exception ex)
        {
            AppendLog($"Pair failed: {ex.Message}");
        }
        finally
        {
            _pairButton.Enabled = true;
        }
    }

    private void RefreshOscButton()
    {
        if (_service.Osc.IsRunning)
        {
            _oscToggleButton.Text = "Stop";
            _oscStatusLabel.Text = $"listening on UDP {_service.Osc.Port}";
        }
        else
        {
            _oscToggleButton.Text = "Start";
            _oscStatusLabel.Text = "(stopped)";
        }
    }

    private void RefreshLiveLabels()
    {
        var cmd = _service.LastSentCommand;
        _resolvedLabel.Text = $"Current: {cmd.Mode}" + (cmd.Mode == ReonProtocol.Mode.Stop ? "" : $" @ L{cmd.Level}");
        _inputsLabel.Text =
            $"OSC inputs: PFHotHigh={_service.GetInput(InputSource.PfHotHigh):0}  " +
            $"water={_service.GetInput(InputSource.Water):0}  " +
            $"cold={_service.GetInput(InputSource.Cold):0.00}  " +
            $"heat={_service.GetInput(InputSource.Heat):0.00}";

        RefreshOscButton();
    }

    private void OnTelemetry(object? sender, Telemetry t)
    {
        BeginInvoke(() =>
        {
            _telemLabel.Text =
                $"Plate(skin): {t.SkinPlate:0.00} °C   Sink: {t.Heatsink:0.00} °C   " +
                $"Board: {t.Board:0.00} °C   Ambient: {t.Ambient:0.00} °C";
        });
    }

    private void OnCommandSent(object? sender, ResolvedCommand cmd)
    {
        BeginInvoke(() => AppendLog($"-> {cmd.Mode}" + (cmd.Mode == ReonProtocol.Mode.Stop ? "" : $" L{cmd.Level}")));
    }

    private void OnServiceLog(object? sender, string message)
    {
        if (IsHandleCreated) BeginInvoke(() => AppendLog(message));
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}";
        _logBox.AppendText(line);
        if (_logBox.TextLength > 16_384)
            _logBox.Text = _logBox.Text[^8_192..];
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window only hides it; the tray icon keeps the app alive.
        if (e.CloseReason == CloseReason.UserClosing && _service is not null)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
