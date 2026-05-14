using System.Runtime.InteropServices;
using System.Text;
using SharpGen.Runtime;
using Valve.VR;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ReonOSC.Services;

/// <summary>
/// Reads the Pebble Feel signal pixel from whatever surface is currently rendering
/// VRChat, raising events when its colour changes.
///
/// Two capture backends are tried in order at <see cref="Start"/>:
///   1. OpenVR compositor mirror — works when SteamVR is running.
///   2. DXGI Output Duplication of the desktop, cropped to the VRChat window —
///      works when VRChat runs in desktop mode (no SteamVR).
///
/// The PFSignal shader (net.shiftall.pfsignal) renders the signal pixel at
/// (hCenter + s.x*0.04, s.y - s.y*0.03) in Unity screen-space pixel coords.
/// In top-origin (DirectX / Windows desktop) coords that's (w*0.54, h*0.03).
/// Sample region is 4x4 to stay inside the 8x8 PFSignal block.
/// </summary>
public sealed class PfSignalReader : IDisposable
{
    private const float SamplePointX = 0.5f + 0.04f;  // 0.54
    private const float SamplePointY = 0.03f;
    private const int SampleRegion   = 4;

    public event EventHandler<PfSignalSample>? SignalChanged;
    public event EventHandler<string>? Log;

    public bool IsRunning => _loopTask is not null && !_loopTask.IsCompleted;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(40); // ~25 Hz

    public enum CaptureBackend { None, OpenVrMirror, DesktopDuplication }
    public CaptureBackend ActiveBackend { get; private set; } = CaptureBackend.None;

    private CVRSystem? _vr;
    private CVRCompositor? _compositor;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private IDXGIOutputDuplication? _duplication;
    private IntPtr _vrchatHwnd;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private PfSignalSample _last = PfSignalSample.None;

    public void Start()
    {
        if (IsRunning) return;

        // A D3D11 device is needed by both backends — create once.
        if (!CreateD3D11Device()) return;

        // Prefer the OpenVR mirror — zero permission UI, no extra desktop copy.
        if (TryInitOpenVR())
        {
            ActiveBackend = CaptureBackend.OpenVrMirror;
        }
        else if (TryInitDesktopDuplication())
        {
            ActiveBackend = CaptureBackend.DesktopDuplication;
        }
        else
        {
            Logf("PF signal: no capture backend available. " +
                 "Start SteamVR for the VR path, or launch VRChat in desktop mode for the fallback.");
            Cleanup();
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => Loop(_cts.Token));
        Logf($"PF signal reader started ({ActiveBackend}, ~{1000 / Math.Max(1, PollInterval.TotalMilliseconds):0} Hz poll).");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        Cleanup();
        Logf("PF signal reader stopped.");
    }

    public void Dispose() => Stop();

    // -------- D3D11 device, shared between both backends --------------------

    private bool CreateD3D11Device()
    {
        try
        {
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
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logf($"PF signal D3D11 init error: {ex.Message}");
            return false;
        }
    }

    // -------- OpenVR mirror backend -----------------------------------------

    private bool TryInitOpenVR()
    {
        try
        {
            EVRInitError err = EVRInitError.None;
            _vr = OpenVR.Init(ref err, EVRApplicationType.VRApplication_Background);
            if (err != EVRInitError.None || _vr is null)
            {
                Logf($"OpenVR init failed: {err}. Trying desktop fallback …");
                _vr = null;
                return false;
            }
            _compositor = OpenVR.Compositor;
            if (_compositor is null) { ShutdownOpenVR(); return false; }
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (Exception ex) { Logf($"OpenVR init error: {ex.Message}. Trying desktop fallback …"); return false; }
    }

    private void ShutdownOpenVR()
    {
        if (_vr is null) return;
        try { OpenVR.Shutdown(); } catch { }
        _vr = null; _compositor = null;
    }

    private bool TrySampleOpenVR(out PfSignalSample sample)
    {
        sample = PfSignalSample.None;
        if (_compositor is null || _device is null || _context is null) return false;

        IntPtr srvPtr = IntPtr.Zero;
        var err = _compositor.GetMirrorTextureD3D11(EVREye.Eye_Left, _device.NativePointer, ref srvPtr);
        if (err != EVRCompositorError.None || srvPtr == IntPtr.Zero) return false;

        try
        {
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
            _context.CopyResource(_staging!, src);

            var map = _context.Map(_staging!, 0, MapMode.Read);
            try { sample = AveragePixel(map, desc.Format, sx, sy); return true; }
            finally { _context.Unmap(_staging!, 0); }
        }
        finally
        {
            _compositor.ReleaseMirrorTextureD3D11(srvPtr);
        }
    }

    // -------- Desktop Duplication backend (VRChat desktop mode) -------------

    private bool TryInitDesktopDuplication()
    {
        try
        {
            _vrchatHwnd = FindVrChatWindow();
            if (_vrchatHwnd == IntPtr.Zero)
            {
                Logf("PF signal: VRChat window not found. Start VRChat first, then re-enable the hook.");
                return false;
            }

            // Pick the monitor the VRChat window is currently on so we only
            // duplicate the relevant output.
            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            var outputIndex = FindOutputContainingWindow(adapter, _vrchatHwnd);
            using var output = adapter.GetOutput(outputIndex);
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(_device);

            return true;
        }
        catch (Exception ex)
        {
            Logf($"PF signal: Desktop Duplication init failed: {ex.Message}");
            return false;
        }
    }

    private bool TrySampleDxgi(out PfSignalSample sample)
    {
        sample = PfSignalSample.None;
        if (_duplication is null || _device is null || _context is null) return false;
        if (_vrchatHwnd == IntPtr.Zero || !IsWindow(_vrchatHwnd))
        {
            // VRChat went away — bail out gracefully so the loop can stop.
            return false;
        }

        IDXGIResource? desktopResource = null;
        try
        {
            var ar = _duplication.AcquireNextFrame(100, out var _, out desktopResource);
            if (ar.Failure || desktopResource is null) return false;

            using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            var deskDesc = desktopTexture.Description;

            // VRChat client area in desktop coordinates.
            if (!GetClientRect(_vrchatHwnd, out var clientRect)) return false;
            var origin = new POINT();
            if (!ClientToScreen(_vrchatHwnd, ref origin)) return false;
            int cw = clientRect.Right - clientRect.Left;
            int ch = clientRect.Bottom - clientRect.Top;
            if (cw <= 0 || ch <= 0) return false;

            int sx = origin.X + (int)(cw * SamplePointX) - SampleRegion / 2;
            int sy = origin.Y + (int)(ch * SamplePointY) - SampleRegion / 2;
            sx = Math.Clamp(sx, 0, (int)deskDesc.Width  - SampleRegion);
            sy = Math.Clamp(sy, 0, (int)deskDesc.Height - SampleRegion);

            EnsureStaging(deskDesc);
            _context.CopyResource(_staging!, desktopTexture);

            var map = _context.Map(_staging!, 0, MapMode.Read);
            try { sample = AveragePixel(map, deskDesc.Format, sx, sy); return true; }
            finally { _context.Unmap(_staging!, 0); }
        }
        finally
        {
            if (desktopResource is not null) { try { _duplication.ReleaseFrame(); } catch { } }
            desktopResource?.Dispose();
        }
    }

    // -------- shared loop / staging / averaging -----------------------------

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool ok = ActiveBackend switch
                {
                    CaptureBackend.OpenVrMirror      => TrySampleOpenVR(out var s) && Handle(s),
                    CaptureBackend.DesktopDuplication => TrySampleDxgi(out var s) && Handle(s),
                    _ => false,
                };
                _ = ok; // suppress warning
            }
            catch (Exception ex)
            {
                Logf($"PF sample error: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private bool Handle(PfSignalSample sample)
    {
        if (sample != _last)
        {
            _last = sample;
            SignalChanged?.Invoke(this, sample);
        }
        return true;
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
        try { _duplication?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        ShutdownOpenVR();
        _staging = null; _duplication = null; _context = null; _device = null;
        _vrchatHwnd = IntPtr.Zero;
        ActiveBackend = CaptureBackend.None;
        _cts?.Dispose();
        _cts = null; _loopTask = null;
    }

    private void Logf(string msg) => Log?.Invoke(this, msg);

    // -------- Win32 plumbing for the desktop fallback -----------------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private static IntPtr FindVrChatWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            // VRChat's window title starts with 'VRChat' — sometimes followed by
            // a build number. Be a little permissive.
            if (title.StartsWith("VRChat", StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false; // stop enumerating
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static int FindOutputContainingWindow(IDXGIAdapter adapter, IntPtr hwnd)
    {
        // Fallback to output 0 if we can't determine the right monitor.
        if (!GetClientRect(hwnd, out var client)) return 0;
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin)) return 0;
        int centerX = origin.X + (client.Right - client.Left) / 2;
        int centerY = origin.Y + (client.Bottom - client.Top) / 2;

        for (int i = 0; ; i++)
        {
            try
            {
                using var output = adapter.GetOutput(i);
                var desc = output.Description;
                var r = desc.DesktopCoordinates;
                if (centerX >= r.Left && centerX < r.Right && centerY >= r.Top && centerY < r.Bottom)
                    return i;
            }
            catch (SharpGenException) { return 0; }
        }
    }
}

/// <summary>One RGB sample from the capture source. (0,0,0) means "off / no signal".</summary>
public readonly record struct PfSignalSample(byte R, byte G, byte B)
{
    public static readonly PfSignalSample None = new(0, 0, 0);
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

public enum PfThermalMode { Off, Cool, Hot }

/// <summary>
/// Decoded Pebble-Feel thermal state. Levels are 1..4 (Cool has 4, Hot has 3);
/// 0 means Off / no signal. The level here is the SOURCE-side intensity; the
/// resolver clamps to the connected Reon's <c>caps.coolMax</c> / <c>heatMax</c>
/// before sending — so CoolFastHigh (L4) on an RNP-3 becomes Cool L3.
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
    /// Reference colours from net.shiftall.pfsignal's PFSignal*Material.mat.
    /// Tolerant thresholds because mirror / desktop capture may dither slightly.
    /// </summary>
    public static PfThermalState Decode(PfSignalSample s)
    {
        bool isHot  = s.R >= 200 && s.B <= 64;
        bool isCool = s.B >= 200 && s.R <= 64;

        if (!isHot && !isCool) return PfThermalState.Off;

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
