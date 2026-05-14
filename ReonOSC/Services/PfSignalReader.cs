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

        if (!CreateD3D11Device()) return;

        // Don't fail Start if no backend is currently available — the loop
        // continuously probes for one, so launching ReonOSC before VRChat /
        // SteamVR is fine. The current backend is exposed via ActiveBackend.
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => Loop(_cts.Token));
        Logf($"PF signal reader started (~{1000 / Math.Max(1, PollInterval.TotalMilliseconds):0} Hz, probing backends).");
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

    private string? _lastDxgiInitErrorReason; // rate-limit the "not found" / failure log

    private bool TryInitDesktopDuplication()
    {
        try
        {
            _vrchatHwnd = FindVrChatWindow();
            if (_vrchatHwnd == IntPtr.Zero)
            {
                var processCount = SafeProcessCount("VRChat");
                var reason = processCount == 0
                    ? "no VRChat process running"
                    : $"VRChat process is running (x{processCount}) but its main window handle wasn't found";
                if (_lastDxgiInitErrorReason != reason)
                {
                    _lastDxgiInitErrorReason = reason;
                    Logf($"PF signal (Desktop fallback): {reason}.");
                }
                return false;
            }
            _lastDxgiInitErrorReason = null;

            // Vortice's COM methods take an out-param and return a Result; that's
            // the canonical shape in v3.6.x.
            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            var hr = dxgiDevice.GetAdapter(out var adapter);
            if (hr.Failure || adapter is null) { Logf($"PF signal: dxgiDevice.GetAdapter failed: 0x{hr.Code:x8}"); return false; }

            try
            {
                int outIdx = FindOutputContainingWindow(adapter, _vrchatHwnd);
                hr = adapter.EnumOutputs((uint)outIdx, out var output);
                if (hr.Failure || output is null) { Logf($"PF signal: adapter.EnumOutputs failed: 0x{hr.Code:x8}"); return false; }
                try
                {
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    _duplication = output1.DuplicateOutput(_device);
                    return true;
                }
                finally { output.Dispose(); }
            }
            finally { adapter.Dispose(); }
        }
        catch (Exception ex)
        {
            var reason = $"Desktop Duplication init exception: {ex.GetType().Name}: {ex.Message}";
            if (_lastDxgiInitErrorReason != reason)
            {
                _lastDxgiInitErrorReason = reason;
                Logf($"PF signal: {reason}");
            }
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

    private static readonly TimeSpan BackendRecheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>Cheap process-level probe — avoids paying the cost of an OpenVR
    /// Init() handshake when SteamVR is clearly not even running.</summary>
    private static bool IsSteamVrRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("vrserver").Length > 0; }
        catch { return false; }
    }

    private async Task Loop(CancellationToken ct)
    {
        DateTime nextBackendCheck = DateTime.MinValue;
        int consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Periodically re-pick the backend. Two situations to handle:
                //   - We have no backend and one is now available.
                //   - We're on DXGI but SteamVR just came up — switch to OpenVR,
                //     which is preferred (cheaper, no permission UI, exact pixel
                //     coords from the compositor instead of cropped client rect).
                if (DateTime.UtcNow >= nextBackendCheck)
                {
                    nextBackendCheck = DateTime.UtcNow + BackendRecheckInterval;
                    PickBestBackend();
                }

                bool ok = ActiveBackend switch
                {
                    CaptureBackend.OpenVrMirror       => TrySampleOpenVR(out var s) && Handle(s),
                    CaptureBackend.DesktopDuplication => TrySampleDxgi(out var s2) && Handle(s2),
                    _ => false,
                };

                if (ok) consecutiveFailures = 0;
                else
                {
                    consecutiveFailures++;
                    // ~1s of failures on an active backend → drop it so the
                    // next backend check can re-init from scratch (handles
                    // VRChat closing, SteamVR exit, etc.).
                    if (consecutiveFailures >= 25 && ActiveBackend != CaptureBackend.None)
                    {
                        Logf($"PF: {ActiveBackend} stopped delivering frames. Re-probing …");
                        DropCurrentBackend();
                        nextBackendCheck = DateTime.MinValue;
                        consecutiveFailures = 0;
                    }
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

    /// <summary>
    /// Choose / upgrade the capture backend. Priority is OpenVR &gt; DXGI &gt; none.
    /// If we're already on the highest-priority available backend, no-op.
    /// </summary>
    private void PickBestBackend()
    {
        // Already on OpenVR — best possible, no work needed.
        if (ActiveBackend == CaptureBackend.OpenVrMirror) return;

        // OpenVR became available — switch up.
        if (IsSteamVrRunning() && TryInitOpenVR())
        {
            if (ActiveBackend == CaptureBackend.DesktopDuplication)
            {
                Logf("SteamVR came up — switching from Desktop Duplication to OpenVR mirror.");
                try { _duplication?.Dispose(); } catch { }
                _duplication = null;
                _vrchatHwnd = IntPtr.Zero;
            }
            else
            {
                Logf("OpenVR mirror backend active.");
            }
            ActiveBackend = CaptureBackend.OpenVrMirror;
            return;
        }

        // Already on DXGI — fine, keep going.
        if (ActiveBackend == CaptureBackend.DesktopDuplication) return;

        // Nothing yet — try DXGI.
        if (TryInitDesktopDuplication())
        {
            ActiveBackend = CaptureBackend.DesktopDuplication;
            Logf("Desktop Duplication backend active (capturing VRChat window).");
        }
    }

    /// <summary>Tear down whichever backend is currently active so PickBestBackend
    /// can start fresh. Doesn't touch the shared D3D11 device.</summary>
    private void DropCurrentBackend()
    {
        switch (ActiveBackend)
        {
            case CaptureBackend.OpenVrMirror:
                ShutdownOpenVR();
                break;
            case CaptureBackend.DesktopDuplication:
                try { _duplication?.Dispose(); } catch { }
                _duplication = null;
                _vrchatHwnd = IntPtr.Zero;
                break;
        }
        ActiveBackend = CaptureBackend.None;
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
        // Process-based lookup first — most reliable, doesn't care about the
        // window title string. The VRChat exe runs as 'VRChat.exe'.
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("VRChat"))
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                }
                catch { /* access denied or process exited */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* fall through to title scan */ }

        // Fallback: EnumWindows by title, broad match. Some VRChat builds
        // include a version suffix, an emoji indicator etc.
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.IndexOf("vrchat", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static int SafeProcessCount(string name)
    {
        try { return System.Diagnostics.Process.GetProcessesByName(name).Length; }
        catch { return -1; }
    }

    private static int FindOutputContainingWindow(IDXGIAdapter adapter, IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var client)) return 0;
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin)) return 0;
        int centerX = origin.X + (client.Right - client.Left) / 2;
        int centerY = origin.Y + (client.Bottom - client.Top) / 2;

        for (uint i = 0; i < 8; i++)   // hard cap: no machine has >8 monitors plugged into one adapter
        {
            var hr = adapter.EnumOutputs(i, out var output);
            if (hr.Failure || output is null) return 0;
            try
            {
                var r = output.Description.DesktopCoordinates;
                if (centerX >= r.Left && centerX < r.Right && centerY >= r.Top && centerY < r.Bottom)
                    return (int)i;
            }
            finally { output.Dispose(); }
        }
        return 0;
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
