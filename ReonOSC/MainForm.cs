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
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(620, 600);
        ClientSize = new Size(720, 880);

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
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoScroll = true,
            Padding = new Padding(8),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        void Add(GroupBox g)
        {
            if (g.Dock == DockStyle.None) g.Dock = DockStyle.Top;
            g.Margin = new Padding(0, 0, 0, 8);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(g);
        }

        Add(BuildConnectionGroup());
        Add(BuildOscGroup());
        Add(BuildManualGroup());
        Add(BuildPresetsGroup());
        Add(BuildStatusGroup());
        Add(BuildOptionsGroup());
        Add(BuildLogGroup());

        // Log gets the rest of the column at 100% so it stretches if the form grows.
        root.RowStyles[root.RowStyles.Count - 1] = new RowStyle(SizeType.Percent, 100f);
    }

    private static GroupBox MakeGroup(string title) => new()
    {
        Text = title,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(10, 6, 10, 10),
    };

    private static TableLayoutPanel MakeInnerTlp(int columns) => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = columns,
    };

    private static Label MakeLabel(string text, Padding? margin = null) => new()
    {
        Text = text,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = margin ?? new Padding(0, 6, 8, 4),
    };

    private GroupBox BuildConnectionGroup()
    {
        var g = MakeGroup("Reon connection");
        var t = MakeInnerTlp(3);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _statusLabel.Text = "Status: disconnected";
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(0, 4, 0, 8);
        t.Controls.Add(_statusLabel, 0, 0);
        t.SetColumnSpan(_statusLabel, 3);

        SetupButton(_connectButton, "Connect");
        SetupButton(_disconnectButton, "Disconnect");
        _disconnectButton.Enabled = false;
        SetupButton(_pairButton, "Pair (device in pair mode)…");
        t.Controls.Add(_connectButton, 0, 1);
        t.Controls.Add(_disconnectButton, 1, 1);
        t.Controls.Add(_pairButton, 2, 1);

        g.Controls.Add(t);
        return g;
    }

    private static void SetupButton(Button b, string text)
    {
        b.Text = text;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(10, 4, 10, 4);
        b.Margin = new Padding(0, 0, 8, 0);
    }

    private GroupBox BuildOscGroup()
    {
        var g = MakeGroup("OSC server");
        var t = MakeInnerTlp(2);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        // top row: UDP port input + Start/Stop button + status — packed into a FlowLayoutPanel
        var portRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8),
        };
        portRow.Controls.Add(MakeLabel("UDP port:"));
        _portInput.Minimum = 1;
        _portInput.Maximum = 65535;
        _portInput.Value = _settings.OscPort;
        _portInput.Width = 90;
        _portInput.Margin = new Padding(0, 3, 12, 0);
        portRow.Controls.Add(_portInput);
        SetupButton(_oscToggleButton, "Start");
        _oscToggleButton.Margin = new Padding(0, 0, 12, 0);
        portRow.Controls.Add(_oscToggleButton);
        _oscStatusLabel.AutoSize = true;
        _oscStatusLabel.Text = "(stopped)";
        _oscStatusLabel.Margin = new Padding(0, 6, 0, 0);
        portRow.Controls.Add(_oscStatusLabel);
        t.Controls.Add(portRow, 0, 0);
        t.SetColumnSpan(portRow, 2);

        var hint = MakeLabel("OSC addresses (edit to match your sender):", new Padding(0, 4, 0, 4));
        t.Controls.Add(hint, 0, 1);
        t.SetColumnSpan(hint, 2);

        int row = 2;
        AddAddrRow(t, _addrPfHot, "PFHotHigh:",     _settings.AddrPfHotHigh, ref row);
        AddAddrRow(t, _addrWater, "water (bool):",  _settings.AddrWater,     ref row);
        AddAddrRow(t, _addrCold,  "cold (float):",  _settings.AddrCold,      ref row);
        AddAddrRow(t, _addrHeat,  "heat (float):",  _settings.AddrHeat,      ref row);

        g.Controls.Add(t);
        return g;
    }

    private static void AddAddrRow(TableLayoutPanel t, TextBox tb, string label, string initial, ref int row)
    {
        t.Controls.Add(MakeLabel(label), 0, row);
        tb.Text = initial;
        tb.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        tb.Margin = new Padding(0, 3, 0, 4);
        t.Controls.Add(tb, 1, row);
        row++;
    }

    private GroupBox BuildManualGroup()
    {
        var g = MakeGroup("Manual control");
        var t = MakeInnerTlp(4);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _manualOverride.Text = "Manual override (ignore OSC)";
        _manualOverride.AutoSize = true;
        _manualOverride.Margin = new Padding(0, 4, 0, 8);
        t.Controls.Add(_manualOverride, 0, 0);
        t.SetColumnSpan(_manualOverride, 4);

        t.Controls.Add(MakeLabel("Mode:"), 0, 1);
        _manualMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _manualMode.Items.AddRange(new object[] { "Stop", "Cool", "Heat" });
        _manualMode.SelectedIndex = 0;
        _manualMode.Width = 110;
        _manualMode.Margin = new Padding(0, 3, 20, 4);
        t.Controls.Add(_manualMode, 1, 1);

        t.Controls.Add(MakeLabel("Level:"), 2, 1);
        _manualLevel.Minimum = ReonProtocol.LevelMin;
        _manualLevel.Maximum = ReonProtocol.LevelMax;
        _manualLevel.Value = 0;
        _manualLevel.Width = 80;
        _manualLevel.Margin = new Padding(0, 3, 0, 4);
        t.Controls.Add(_manualLevel, 3, 1);

        g.Controls.Add(t);
        return g;
    }

    private GroupBox BuildPresetsGroup()
    {
        var g = MakeGroup("Preset levels for OSC triggers");
        var t = MakeInnerTlp(2);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        t.Controls.Add(MakeLabel("Heat Touch level (when PFHotHigh = 1):"), 0, 0);
        _heatTouchInput.Minimum = 0;
        _heatTouchInput.Maximum = ReonProtocol.LevelMax;
        _heatTouchInput.Value = _settings.HeatTouchLevel;
        _heatTouchInput.Width = 80;
        _heatTouchInput.Margin = new Padding(0, 3, 0, 4);
        t.Controls.Add(_heatTouchInput, 1, 0);

        t.Controls.Add(MakeLabel("Cold Water level (when water = 1):"), 0, 1);
        _coldWaterInput.Minimum = 0;
        _coldWaterInput.Maximum = ReonProtocol.LevelMax;
        _coldWaterInput.Value = _settings.ColdWaterLevel;
        _coldWaterInput.Width = 80;
        _coldWaterInput.Margin = new Padding(0, 3, 0, 4);
        t.Controls.Add(_coldWaterInput, 1, 1);

        g.Controls.Add(t);
        return g;
    }

    private GroupBox BuildStatusGroup()
    {
        var g = MakeGroup("Live status");
        var t = MakeInnerTlp(1);
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _resolvedLabel.Text = "Current: idle";
        _resolvedLabel.AutoSize = true;
        _resolvedLabel.Font = new Font(Font, FontStyle.Bold);
        _resolvedLabel.Margin = new Padding(0, 0, 0, 6);
        t.Controls.Add(_resolvedLabel, 0, 0);

        _telemLabel.Text = "Plate: —   Sink: —   Board: —   Ambient: —";
        _telemLabel.AutoSize = true;
        _telemLabel.Margin = new Padding(0, 0, 0, 6);
        t.Controls.Add(_telemLabel, 0, 1);

        _inputsLabel.Text = "OSC inputs: PFHotHigh=0  water=0  cold=0.00  heat=0.00";
        _inputsLabel.AutoSize = true;
        _inputsLabel.Margin = new Padding(0, 0, 0, 0);
        t.Controls.Add(_inputsLabel, 0, 2);

        g.Controls.Add(t);
        return g;
    }

    private GroupBox BuildOptionsGroup()
    {
        var g = MakeGroup("Options");
        var t = MakeInnerTlp(1);

        _startMinimisedBox.Text = "Start minimised to tray";
        _startMinimisedBox.AutoSize = true;
        _startMinimisedBox.Checked = _settings.StartMinimised;
        _startMinimisedBox.Margin = new Padding(0, 0, 0, 4);
        t.Controls.Add(_startMinimisedBox, 0, 0);

        _autoConnectBox.Text = "Auto-connect to Reon on start";
        _autoConnectBox.AutoSize = true;
        _autoConnectBox.Checked = _settings.AutoConnectOnStart;
        _autoConnectBox.Margin = new Padding(0, 0, 0, 0);
        t.Controls.Add(_autoConnectBox, 0, 1);

        g.Controls.Add(t);
        return g;
    }

    private GroupBox BuildLogGroup()
    {
        // Log keeps a fixed height so the TLP cell can give it a stable footprint.
        var g = new GroupBox
        {
            Text = "Log",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 200,
            MinimumSize = new Size(0, 160),
            Padding = new Padding(10, 6, 10, 10),
        };
        _logBox.Multiline = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.ReadOnly = true;
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = new Font(FontFamily.GenericMonospace, 9f);
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
        bool connected = false;
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
            connected = true;
        }
        catch (Exception ex)
        {
            AppendLog($"Connect failed: {ex.Message}");
        }
        finally
        {
            // Only keep Connect disabled while a connection is actually live.
            if (!connected) _connectButton.Enabled = true;
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
            // Make sure Connect is usable even if a prior auto-connect attempt left it disabled.
            _connectButton.Enabled = !_service.Reon.IsConnected;
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
