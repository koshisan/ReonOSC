using System.Linq;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ReonOSC.Ble;

/// <summary>
/// Managed BLE wrapper for the Reon Pocket 3 using WinRT GATT APIs.
/// Handles connection, authentication, command writes, and notification streams.
/// </summary>
public sealed class ReonClient : IAsyncDisposable
{
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _authChar;
    private GattCharacteristic? _cmdChar;
    private GattCharacteristic? _telemChar;
    private GattCharacteristic? _statusChar;
    private bool _authed;

    public event EventHandler<Telemetry>? TelemetryReceived;
    public event EventHandler<StateEcho>? StateReceived;
    public event EventHandler<string>? Log;

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public static ulong ParseMac(string mac)
    {
        ulong result = 0;
        foreach (var part in mac.Split(':', '-'))
            result = (result << 8) | Convert.ToByte(part, 16);
        return result;
    }

    public static string FormatMac(ulong mac)
    {
        var bytes = new byte[6];
        for (int i = 5; i >= 0; i--) { bytes[i] = (byte)(mac & 0xFF); mac >>= 8; }
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>
    /// Scan for the Reon by advertised name <c>RNP-3</c> for at most <paramref name="timeout"/>.
    /// Returns the device's Bluetooth address as a ulong, or null if not found.
    /// </summary>
    public static Task<ulong?> FindReonAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<ulong?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        void Stop(ulong? result)
        {
            try { watcher.Stop(); } catch { }
            tcs.TrySetResult(result);
        }

        watcher.Received += (s, args) =>
        {
            var name = args.Advertisement?.LocalName;
            if (!string.IsNullOrEmpty(name) && name.StartsWith(ReonProtocol.DeviceNamePrefix, StringComparison.Ordinal))
                Stop(args.BluetoothAddress);
        };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        cts.Token.Register(() => Stop(null));

        watcher.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Open a connection, write the bond token to authenticate, and discover characteristics.
    /// </summary>
    public async Task ConnectAsync(ulong bluetoothAddress, byte[] token, CancellationToken ct = default)
    {
        if (token.Length != ReonProtocol.AuthTokenLength)
            throw new ArgumentException($"Token must be {ReonProtocol.AuthTokenLength} bytes.", nameof(token));

        Logf($"Connecting to {FormatMac(bluetoothAddress)} …");
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask(ct)
            ?? throw new InvalidOperationException("BluetoothLEDevice.FromBluetoothAddressAsync returned null.");

        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        var svcResult = await _device.GetGattServicesForUuidAsync(ReonProtocol.ServiceUuid, BluetoothCacheMode.Uncached).AsTask(ct);
        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            throw new InvalidOperationException($"Reon GATT service not found: {svcResult.Status}");
        var service = svcResult.Services[0];

        _authChar   = await GetCharAsync(service, ReonProtocol.CharAuth,   ct);
        _cmdChar    = await GetCharAsync(service, ReonProtocol.CharCmd,    ct);
        _telemChar  = await GetCharAsync(service, ReonProtocol.CharTelem,  ct);
        _statusChar = await GetCharAsync(service, ReonProtocol.CharStatus, ct);

        Logf("Writing auth token …");
        await WriteCharAsync(_authChar, token, ct);
        _authed = true;

        Logf("Subscribing to telemetry + state notifications …");
        await EnableNotifyAsync(_cmdChar, OnCmdNotify, ct);
        await EnableNotifyAsync(_telemChar, OnTelemNotify, ct);

        Logf("Connected and authed.");
    }

    /// <summary>
    /// Write an arbitrary 17-byte token while the device is in pair mode (long button press).
    /// Succeeds even without prior auth — that's the whole point of pair mode.
    /// </summary>
    public async Task PairAsync(ulong bluetoothAddress, byte[] newToken, CancellationToken ct = default)
    {
        if (newToken.Length != ReonProtocol.AuthTokenLength)
            throw new ArgumentException($"Token must be {ReonProtocol.AuthTokenLength} bytes.", nameof(newToken));

        Logf($"Connecting to {FormatMac(bluetoothAddress)} for pair …");
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask(ct)
            ?? throw new InvalidOperationException("BluetoothLEDevice.FromBluetoothAddressAsync returned null.");

        var svcResult = await _device.GetGattServicesForUuidAsync(ReonProtocol.ServiceUuid, BluetoothCacheMode.Uncached).AsTask(ct);
        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            throw new InvalidOperationException($"Reon GATT service not found: {svcResult.Status}");
        var authChar = await GetCharAsync(svcResult.Services[0], ReonProtocol.CharAuth, ct);

        Logf($"Writing new bond token: {Convert.ToHexString(newToken).ToLowerInvariant()}");
        await WriteCharAsync(authChar, newToken, ct);

        TokenStorage.Save(FormatMac(bluetoothAddress), newToken);
        Logf($"Token saved to {TokenStorage.TokenPath}.");
    }

    public Task SetCoolAsync(int level, CancellationToken ct = default) =>
        WriteCommandAsync(ReonProtocol.BuildCommand(ReonProtocol.Mode.Cool, level), ct);

    public Task SetHeatAsync(int level, CancellationToken ct = default) =>
        WriteCommandAsync(ReonProtocol.BuildCommand(ReonProtocol.Mode.Heat, level), ct);

    public Task StopAsync(CancellationToken ct = default) =>
        WriteCommandAsync(ReonProtocol.BuildCommand(ReonProtocol.Mode.Stop), ct);

    private async Task WriteCommandAsync(byte[] frame, CancellationToken ct)
    {
        if (_cmdChar is null || !_authed)
            throw new InvalidOperationException("Not connected/authenticated.");
        await WriteCharAsync(_cmdChar, frame, ct);
    }

    private static async Task<GattCharacteristic> GetCharAsync(GattDeviceService service, Guid uuid, CancellationToken ct)
    {
        var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(ct);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
            throw new InvalidOperationException($"Characteristic {uuid} not found: {result.Status}");
        return result.Characteristics[0];
    }

    private static async Task WriteCharAsync(GattCharacteristic ch, byte[] data, CancellationToken ct)
    {
        var writer = new DataWriter();
        writer.WriteBytes(data);
        var result = await ch.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask(ct);
        if (result.Status == GattCommunicationStatus.Success) return;

        var msg = $"GATT write failed on {ch.Uuid}: {result.Status}";
        if (result.Status == GattCommunicationStatus.ProtocolError && result.ProtocolError.HasValue)
        {
            byte code = result.ProtocolError.Value;
            var explanation = ReonProtocol.ExplainAttError(code) ?? "vendor application error";
            msg += $" (ATT 0x{code:x2} — {explanation})";
        }
        throw new InvalidOperationException(msg);
    }

    private static async Task EnableNotifyAsync(GattCharacteristic ch, TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler, CancellationToken ct)
    {
        var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"CCCD enable failed on {ch.Uuid}: {status}");
        ch.ValueChanged += handler;
    }

    private static byte[] BufferToBytes(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private void OnTelemNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = BufferToBytes(args.CharacteristicValue);
        var telem = ReonProtocol.DecodeTelemetry(data);
        if (telem is { } t) TelemetryReceived?.Invoke(this, t);
    }

    private void OnCmdNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = BufferToBytes(args.CharacteristicValue);
        var state = ReonProtocol.DecodeStateEcho(data);
        if (state is { } s) StateReceived?.Invoke(this, s);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        Logf($"Connection status: {sender.ConnectionStatus}");
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            _authed = false;
    }

    private void Logf(string message) => Log?.Invoke(this, message);

    public async ValueTask DisposeAsync()
    {
        try { if (IsConnected) await StopAsync().ConfigureAwait(false); } catch { }

        if (_cmdChar is not null)   _cmdChar.ValueChanged -= OnCmdNotify;
        if (_telemChar is not null) _telemChar.ValueChanged -= OnTelemNotify;

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }
        _authChar = _cmdChar = _telemChar = _statusChar = null;
        _authed = false;
    }
}
