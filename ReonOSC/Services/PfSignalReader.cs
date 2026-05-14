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

            EnsureStaging(desc);
            // CopyResource over a same-size staging texture — wasteful but
            // avoids Vortice version drift over where Box lives. At 25 Hz on
            // a ~2160x2400 HMD texture, this is roughly 500 MB/s of GPU<->CPU
            // copy bandwidth, which a modern PCIe bus shrugs off.
            _context.CopyResource(_staging!, src);

            var map = _context.Map(_staging!, 0, MapMode.Read);
            try
            {
                sample = AveragePixel(map, desc.Format, sx, sy);
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

    private void EnsureStaging(Texture2DDescription sourceDesc)
    {
        if (_staging is not null)
        {
            var sd = _staging.Description;
            if (sd.Format == sourceDesc.Format && sd.Width == sourceDesc.Width && sd.Height == sourceDesc.Height)
                return;
            _staging.Dispose();
        }
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = sourceDesc.Width,
            Height = sourceDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = sourceDesc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    private static PfSignalSample AveragePixel(MappedSubresource map, Format format, int sx, int sy)
    {
        int n = SampleRegion * SampleRegion;
        long r = 0, g = 0, b = 0;
        unsafe
        {
            byte* basePtr = (byte*)map.DataPointer;
            for (int y = 0; y < SampleRegion; y++)
            {
                byte* row = basePtr + (sy + y) * map.RowPitch;
                byte* p = row + sx * 4;
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

public enum PfThermalMode { Off, Cool, Hot }

/// <summary>
/// Decoded Pebble-Feel thermal state. Levels are 1..4 (Cool has 4, Hot has 3);
/// 0 means Off / no signal. Maps onto the Reon levels directly: PF level N
/// corresponds to Reon wire-level (N-1), because PF starts counting from 1
/// (Low/Mid/High/FastHigh) whereas Reon starts at 0.
/// </summary>
public readonly record struct PfThermalState(PfThermalMode Mode, int Level)
{
    public static readonly PfThermalState Off = new(PfThermalMode.Off, 0);
    public override string ToString() =>
        Mode == PfThermalMode.Off ? "Off" : $"{Mode} L{Level}";
}

public static class PfSignalDecoder
{
    /// <summary>
    /// Decode a sampled pixel into the Pebble-Feel thermal state it encodes.
    /// Reference colours from net.shiftall.pfsignal's PFSignal*Material.mat:
    ///   Off          (  0,   0,   0)
    ///   CoolLow      (  0,   0, 255)
    ///   CoolMid      (  0,  64, 255)
    ///   CoolHigh     (  0, 128, 255)
    ///   CoolFastHigh (  0, 192, 255)
    ///   HotLow       (255,   0,   0)
    ///   HotMid       (255,  64,   0)
    ///   HotHigh      (255, 128,   0)
    ///
    /// We use loose thresholds because the mirror texture path may dither or
    /// blend slightly, and the 4x4 sample area can clip the finder pixels.
    /// </summary>
    public static PfThermalState Decode(PfSignalSample s)
    {
        bool isHot  = s.R >= 200 && s.B <= 64;
        bool isCool = s.B >= 200 && s.R <= 64;

        if (!isHot && !isCool) return PfThermalState.Off;

        // Level inferred from green channel: 0=Low, 64=Mid, 128=High, 192=FastHigh.
        int level =
            s.G < 32  ? 1 :
            s.G < 96  ? 2 :
            s.G < 160 ? 3 :
                        4;

        // Hot doesn't have a FastHigh; clamp to High.
        if (isHot && level > 3) level = 3;

        return new PfThermalState(isHot ? PfThermalMode.Hot : PfThermalMode.Cool, level);
    }
}
