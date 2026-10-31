namespace ReonOSC.Ble;

/// <summary>
/// One heated sock as the rest of the app sees it, independent of how the
/// link is held: <see cref="SockClient"/> talks BLE from this PC,
/// <see cref="MqttSockLink"/> goes through the sockbridge on a Linux box.
/// </summary>
public interface ISockLink : IAsyncDisposable
{
    ulong? Address { get; }
    bool IsConnected { get; }
    double? BatteryVolts { get; }
    int? BatteryPercent { get; }
    SockClient.LinkHealth Health { get; }

    /// <summary>Only used by the local BLE link; the bridge enforces its own
    /// command timeout.</summary>
    int DeadManMinutes { get; set; }

    event EventHandler<int>? BatteryChanged;
    event EventHandler<bool>? ConnectionChanged;
    event EventHandler<SockClient.LinkHealth>? HealthChanged;
    event EventHandler<string>? Log;

    Task BindAsync(ulong address, CancellationToken ct = default);
    Task UnbindAsync();
    Task<bool> SetTemperatureAsync(byte tempC, CancellationToken ct = default);
    Task<bool> TurnOffAsync(CancellationToken ct = default);
}
