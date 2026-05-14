using System.Runtime.InteropServices;
using Valve.VR;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ReonOSC.Services;

/// <summary>
/// Reads the Pebble Feel signal pixel out of the SteamVR compositor mirror
/// texture and raises events when its colour changes.
///
/// Pebble Feel encodes thermal state as a pixel rendered by the VRChat world
/// at a fixed screen position (X = w/2 + w*0.04, Y = h*0.03). Their proprietary
/// software polls the same pixel via screen capture; we read it directly from
/// the compositor's mirror texture instead — about as cheap as it gets.
/// </summary>
public sealed class PfSignalReader : IDisposable
{
    /// <summary>Sample point as a fraction of the mirror texture's width/height.</summary>
    private const float SamplePointX = 0.5f + 0.04f;  // 0.54
    private const float SamplePointY = 0.03f;
    private const int SampleRegion   = 4;             // 4×4 area around the sample point

    public event EventHandler<PfSignalSample>? SignalChanged;
    public event EventHandler<string>? Log;

    public bool IsRunning => _loopTask is not null && !_loopTask.IsCompleted;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(40); // ~25 Hz

    private CVRSystem? _vr;
    private CVRCompositor? _compositor;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private PfSignalSample _last = PfSignalSample.None;

    public void Start()
    {
        if (IsRunning) return;
        if (!TryInit()) return;

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => Loop(_cts.Token));
        Logf($"PF signal reader started (~{1000 / Math.Max(1, PollInterval.TotalMilliseconds):0} Hz poll).");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        Cleanup();
        Logf("PF signal reader stopped.");
    }

    public void Dispose() => Stop();

    private bool TryInit()
    {
        try
        {
            EVRInitError err = EVRInitError.None;
            _vr = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
            if (err != EVRInitError.None || _vr is null)
            {
                Logf($"OpenVR init failed: {err}. PF signal disabled — SteamVR not running?");
                _vr = null;
                return false;
            }

            _compositor = OpenVR.Compositor;
            if (_compositor is null)
            {
                Logf("OpenVR compositor not available. PF signal disabled.");
                Cleanup();
                return false;
            }

            var result = D3D11.D3D11CreateDevice(
                null,
                Vortice.Direct3D.DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                null,
                out _device,
                out _context);
            if (result.Failure || _device is null || _context is null)
            {
                Logf($"D3D11 device create failed: 0x{result.Code:x8}. PF signal disabled.");
                Cleanup();
                return false;
            }

            return true;
        }
        catch (DllNotFoundException dll)
        {
            Logf($"Native dependency missing ({dll.Message}). PF signal disabled.");
            Cleanup();
            return false;
        }
        catch (Exception ex)
        {
            Logf($"PF signal init error: {ex.Message}. Disabled.");
            Cleanup();
            return false;
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (TrySample(out var sample) && sample != _last)
                {
                    _last = sample;
                    SignalChanged?.Invoke(this, sample);
                }
            }
            catch (Exception ex)
            {
                Logf($"PF sample error: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private bool TrySample(out PfSignalSample sample)
    {
        sample = PfSignalSample.None;
        if (_compositor is null || _device is null || _context is null) return false;

        IntPtr srvPtr = IntPtr.Zero;
        var err = _compositor.GetMirrorTextureD3D11(EVREye.Eye_Left, _device.NativePointer, ref srvPtr);
        if (err != EVRCompositorError.None || srvPtr == IntPtr.Zero) return false;

        try
        {
            // QueryInterface for the SRV bumps the refcount so Vortice's
            // Dispose can release once without stepping on
            // ReleaseMirrorTextureD3D11.
            var iidSrv = typeof(ID3D11ShaderResourceView).GUID;
            int hr = Marshal.QueryInterface(srvPtr, ref iidSrv, out var srvOwned);
            if (hr != 0 || srvOwned == IntPtr.Zero) return false;

            using var srv = new ID3D11ShaderResourceView(srvOwned);
            using var resource = srv.Resource;
            using var src = resource.QueryInterface<ID3D11Texture2D>();
            var desc = src.Description;
            if (desc.Width == 0 || desc.Height == 0) return false;

            int sx = Math.Clamp((int)(desc.Width  * SamplePointX) - SampleRegion / 2, 0, (int)desc.Width  - SampleRegion);
            int sy = Math.Clamp((int)(desc.Height * SamplePointY) - SampleRegion / 2, 0, (int)desc.Height - SampleRegion);

            EnsureStaging(desc.Format);
            _context.CopySubresourceRegion(
                _staging!, 0, 0, 0, 0,
                src, 0,
                new Box(sx, sy, 0, sx + SampleRegion, sy + SampleRegion, 1));

            var map = _context.Map(_staging!, 0, MapMode.Read);
            try
            {
                sample = AveragePixel(map, desc.Format);
                return true;
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }
        finally
        {
            _compositor.ReleaseMirrorTextureD3D11(srvPtr);
        }
    }

    private void EnsureStaging(Format format)
    {
        if (_staging is not null && _staging.Description.Format == format) return;
        _staging?.Dispose();
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = SampleRegion,
            Height = SampleRegion,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    private static PfSignalSample AveragePixel(MappedSubresource map, Format format)
    {
        int n = SampleRegion * SampleRegion;
        long r = 0, g = 0, b = 0;
        unsafe
        {
            byte* row = (byte*)map.DataPointer;
            for (int y = 0; y < SampleRegion; y++)
            {
                byte* p = row + y * map.RowPitch;
                for (int x = 0; x < SampleRegion; x++)
                {
                    byte b0 = p[0], b1 = p[1], b2 = p[2];
                    if (format == Format.B8G8R8A8_UNorm || format == Format.B8G8R8X8_UNorm)
                    {
                        b += b0; g += b1; r += b2;
                    }
                    else
                    {
                        r += b0; g += b1; b += b2;
                    }
                    p += 4;
                }
            }
        }
        return new PfSignalSample((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private void Cleanup()
    {
        try { _staging?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        if (_vr is not null) { try { OpenVR.Shutdown(); } catch { } }
        _staging = null; _context = null; _device = null;
        _vr = null; _compositor = null;
        _cts?.Dispose();
        _cts = null; _loopTask = null;
    }

    private void Logf(string msg) => Log?.Invoke(this, msg);
}

/// <summary>One RGB sample from the compositor mirror. (0,0,0) means "off / no signal".</summary>
public readonly record struct PfSignalSample(byte R, byte G, byte B)
{
    public static readonly PfSignalSample None = new(0, 0, 0);
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
