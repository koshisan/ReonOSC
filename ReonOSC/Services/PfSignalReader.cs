using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
    // Shader-side positions (Unity bottom-origin screen space — see
    // PixelsOverlay.cginc): the signal pixel and the BLACK finder live at
    // Unity-y = 0.97 (visual top of the screen), the WHITE finder at
    // Unity-y = 0.03 (visual bottom). Whether that maps to memory-row 0.03
    // or 0.97 in the mirror texture depends on the runtime's vertical
    // orientation — OpenVR is free to hand us a Y-flipped mirror vs. what
    // Unity submitted. PrintWindow on the desktop preview, by contrast, is
    // always top-origin "as displayed".
    //
    // Rather than hard-code one orientation, we sample both possible finder
    // positions on each frame and lock to whichever pair matches the
    // expected black/white pattern.
    private const float SamplePointX = 0.5f + 0.04f;   // 0.54 horizontally for the signal pixel
    private const int SampleRegion   = 4;
    private const float FinderXCenter = 0.5f;          // finders are column-centred
    private const float FinderTopY    = 0.03f;         // candidate y A
    private const float FinderBotY    = 0.97f;         // candidate y B
    // Tolerances. Shader writes exact 0 or 255 to a single 8x8 block. With
    // 4x4 inset sampling we shouldn't see any edge bleed, so we can be
    // strict — this is the main defence against scene content (a dark
    // wall, a sunlit white surface) passing as a finder.
    private const int FinderBlackMax = 32;
    private const int FinderWhiteMin = 224;

    /// <summary>How the mirror texture is oriented relative to the shader's
    /// "Unity-y = top" convention. Top means memory-row 0.03 holds the
    /// black + signal blocks (PrintWindow's desktop preview always behaves
    /// this way). Flipped means memory-row 0.97 holds them (some OpenVR
    /// runtimes flip the mirror).</summary>
    private enum Orientation { Unknown, TopOrigin, Flipped }
    private Orientation _orientation = Orientation.Unknown;

    public event EventHandler<PfSignalSample>? SignalChanged;
    public event EventHandler<string>? Log;

    public bool IsRunning => _loopTask is not null && !_loopTask.IsCompleted;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(40); // ~25 Hz

    public enum CaptureBackend { None, OpenVrMirror, WindowCapture }
    public CaptureBackend ActiveBackend { get; private set; } = CaptureBackend.None;

    /// <summary>Outcome of one TrySample call. The loop distinguishes
    /// "backend is alive but we're not seeing the finders" (NoLock, normal
    /// when the user's gaze is off the PFSignal mesh) from "backend died"
    /// (NoData, triggers the re-probe path).</summary>
    private enum SampleResult { NoData, NoLock, Locked }

    private CVRSystem? _vr;
    private CVRCompositor? _compositor;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private IntPtr _vrchatHwnd;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private PfSignalSample _last = PfSignalSample.None;
    private Format _loggedSrcFormat = Format.Unknown; // log mirror texture format once per session/reconnect
    private bool _lastFinderState; // false=invisible/no-lock, true=quad is in view; logged on transitions only
    private int _captureCounter; // monotonic, incremented per CaptureToFile call so the user can verify each click is unique
    // D3D11 immediate context is NOT thread-safe. Loop() samples on a thread-
    // pool thread; CaptureToFile() runs on the bridge / UI thread. Serialising
    // here keeps CopyResource/Map from racing each other and producing the
    // stale data the user observed across multiple capture clicks.
    private readonly object _ctxLock = new();

    /// <summary>Latest sample from the candidate finder position at y=0.03.
    /// Surfaced for the Capture diagnostic so the user can see exactly what
    /// each probe is reading without having to attach a debugger.</summary>
    public PfSignalSample LastFinderTop { get; private set; }
    public PfSignalSample LastFinderBottom { get; private set; }
    public string LastOrientation => _orientation.ToString();

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

    private SampleResult TrySampleOpenVR(out PfSignalSample sample)
    {
        sample = PfSignalSample.None;
        if (_compositor is null || _device is null || _context is null) return SampleResult.NoData;

        IntPtr srvPtr = IntPtr.Zero;
        var err = _compositor.GetMirrorTextureD3D11(EVREye.Eye_Left, _device.NativePointer, ref srvPtr);
        if (err != EVRCompositorError.None || srvPtr == IntPtr.Zero) return SampleResult.NoData;

        try
        {
            var iidSrv = typeof(ID3D11ShaderResourceView).GUID;
            int hr = Marshal.QueryInterface(srvPtr, ref iidSrv, out var srvOwned);
            if (hr != 0 || srvOwned == IntPtr.Zero) return SampleResult.NoData;

            using var srv = new ID3D11ShaderResourceView(srvOwned);
            using var resource = srv.Resource;
            using var src = resource.QueryInterface<ID3D11Texture2D>();
            var desc = src.Description;
            if (desc.Width == 0 || desc.Height == 0) return SampleResult.NoData;

            int w = (int)desc.Width, h = (int)desc.Height;
            lock (_ctxLock)
            {
                EnsureStaging(desc);
                _context.CopyResource(_staging!, src);

                if (_loggedSrcFormat != desc.Format)
                {
                    _loggedSrcFormat = desc.Format;
                    Logf($"PF mirror format: {desc.Format} {desc.Width}x{desc.Height}");
                }

                var map = _context.Map(_staging!, 0, MapMode.Read);
                try
                {
                    // Sample BOTH candidate finder positions; one of them is
                    // the black block, the other is the white block
                    // (orientation depends on the runtime). Whichever pair
                    // matches tells us the orientation and therefore where
                    // the signal lives.
                    var atTop = SampleAtFraction(map, desc.Format, w, h, FinderXCenter, FinderTopY);
                    var atBot = SampleAtFraction(map, desc.Format, w, h, FinderXCenter, FinderBotY);
                    LastFinderTop = atTop;
                    LastFinderBottom = atBot;

                    Orientation o = DetectOrientation(atTop, atBot);
                    if (o == Orientation.Unknown)
                    {
                        NoteFinderState(false, atTop, atBot);
                        sample = PfSignalSample.None;
                        return SampleResult.NoLock;
                    }
                    if (o != _orientation)
                    {
                        _orientation = o;
                        Logf($"PF orientation locked: {(o == Orientation.TopOrigin ? "top-origin (signal at y=0.03)" : "flipped (signal at y=0.97)")}");
                    }
                    NoteFinderState(true, atTop, atBot);
                    float sigY = o == Orientation.TopOrigin ? FinderTopY : FinderBotY;
                    sample = SampleAtFraction(map, desc.Format, w, h, SamplePointX, sigY);
                    return SampleResult.Locked;
                }
                finally { _context.Unmap(_staging!, 0); }
            }
        }
        finally
        {
            _compositor.ReleaseMirrorTextureD3D11(srvPtr);
        }
    }

    // -------- Window-content capture (VRChat desktop mode) -----------------
    //
    // PrintWindow + PW_RENDERFULLCONTENT was added in Windows 8.1 specifically
    // so screen-capture tools could grab D3D-rendered windows even when they're
    // partially obscured by other windows. Slower than DXGI Output Duplication
    // (GDI bitmap path) but it gives us the actual window content regardless of
    // what's on top of VRChat on the desktop — which is the whole point.

    private string? _lastDxgiInitErrorReason; // rate-limit the "not found" / failure log

    private bool TryInitWindowCapture()
    {
        _vrchatHwnd = FindVrChatWindow();
        if (_vrchatHwnd == IntPtr.Zero)
        {
            var processCount = SafeProcessCount("VRChat");
            var reason = processCount == 0
                ? "no VRChat process running"
                : $"VRChat process is running (x{processCount}) but MainWindowHandle == 0";
            if (_lastDxgiInitErrorReason != reason)
            {
                _lastDxgiInitErrorReason = reason;
                Logf($"PF signal (Window capture): {reason}.");
            }
            return false;
        }
        _lastDxgiInitErrorReason = null;
        return true;
    }

    private SampleResult TrySampleWindow(out PfSignalSample sample)
    {
        sample = PfSignalSample.None;
        if (_vrchatHwnd == IntPtr.Zero || !IsWindow(_vrchatHwnd)) return SampleResult.NoData;
        if (!GetClientRect(_vrchatHwnd, out var rect)) return SampleResult.NoData;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return SampleResult.NoData;

        IntPtr windowDC = GetWindowDC(_vrchatHwnd);
        if (windowDC == IntPtr.Zero) return SampleResult.NoData;
        IntPtr memDC = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            memDC = CreateCompatibleDC(windowDC);
            if (memDC == IntPtr.Zero) return SampleResult.NoData;
            bitmap = CreateCompatibleBitmap(windowDC, w, h);
            if (bitmap == IntPtr.Zero) return SampleResult.NoData;
            oldBitmap = SelectObject(memDC, bitmap);

            // PW_RENDERFULLCONTENT forces hardware-rendered windows (D3D11 etc.)
            // to actually paint into our DC. Combined with PW_CLIENTONLY we skip
            // the title bar so the sample fractions match the shader's
            // screen-space coords.
            if (!PrintWindow(_vrchatHwnd, memDC, PW_CLIENTONLY | PW_RENDERFULLCONTENT))
                return SampleResult.NoData;

            var atTop = SampleGdi(memDC, w, h, FinderXCenter, FinderTopY);
            var atBot = SampleGdi(memDC, w, h, FinderXCenter, FinderBotY);
            if (atTop is null || atBot is null) return SampleResult.NoData;
            LastFinderTop = atTop.Value;
            LastFinderBottom = atBot.Value;

            Orientation o = DetectOrientation(atTop.Value, atBot.Value);
            if (o == Orientation.Unknown)
            {
                NoteFinderState(false, atTop.Value, atBot.Value);
                sample = PfSignalSample.None;
                return SampleResult.NoLock;
            }
            if (o != _orientation)
            {
                _orientation = o;
                Logf($"PF orientation locked: {(o == Orientation.TopOrigin ? "top-origin (signal at y=0.03)" : "flipped (signal at y=0.97)")}");
            }
            NoteFinderState(true, atTop.Value, atBot.Value);
            float sigY = o == Orientation.TopOrigin ? FinderTopY : FinderBotY;
            var signal = SampleGdi(memDC, w, h, SamplePointX, sigY);
            if (signal is null) return SampleResult.NoData;
            sample = signal.Value;
            return SampleResult.Locked;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDC, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDC != IntPtr.Zero) DeleteDC(memDC);
            ReleaseDC(_vrchatHwnd, windowDC);
        }
    }

    /// <summary>GDI 4x4 sample at fractional (fx, fy). Returns null if no
    /// valid pixels read (whole region was CLR_INVALID). sRGB-decodes each
    /// byte before averaging so the result is in linear space.</summary>
    private static PfSignalSample? SampleGdi(IntPtr memDC, int w, int h, float fx, float fy)
    {
        int sx = (int)(w * fx);
        int sy = (int)(h * fy);
        sx = Math.Clamp(sx, SampleRegion / 2, w - SampleRegion / 2 - 1);
        sy = Math.Clamp(sy, SampleRegion / 2, h - SampleRegion / 2 - 1);

        double rl = 0, gl = 0, bl = 0;
        int n = 0;
        for (int dy = -SampleRegion / 2; dy < SampleRegion / 2; dy++)
        for (int dx = -SampleRegion / 2; dx < SampleRegion / 2; dx++)
        {
            uint c = GetPixel(memDC, sx + dx, sy + dy);
            if (c == 0xFFFFFFFFu) continue;
            rl += SrgbToLinear(((byte)( c        & 0xFF)) / 255.0);
            gl += SrgbToLinear(((byte)((c >>  8) & 0xFF)) / 255.0);
            bl += SrgbToLinear(((byte)((c >> 16) & 0xFF)) / 255.0);
            n++;
        }
        if (n == 0) return null;
        return new PfSignalSample(
            (byte)Math.Clamp(Math.Round(rl / n * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round(gl / n * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round(bl / n * 255.0), 0, 255));
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

                PfSignalSample s = PfSignalSample.None;
                SampleResult res = ActiveBackend switch
                {
                    CaptureBackend.OpenVrMirror  => TrySampleOpenVR(out s),
                    CaptureBackend.WindowCapture => TrySampleWindow(out s),
                    _ => SampleResult.NoData,
                };

                switch (res)
                {
                    case SampleResult.Locked:
                        Handle(s);
                        consecutiveFailures = 0;
                        break;
                    case SampleResult.NoLock:
                        // Backend is alive, the PFSignal quad just isn't in
                        // view at the moment. Don't reset the backend; the
                        // last propagated state holds via ControlService.
                        consecutiveFailures = 0;
                        break;
                    default: // NoData
                        consecutiveFailures++;
                        // ~1s of failures on an active backend → drop it so
                        // the next backend check can re-init from scratch
                        // (handles VRChat closing, SteamVR exit, etc.).
                        if (consecutiveFailures >= 25 && ActiveBackend != CaptureBackend.None)
                        {
                            Logf($"PF: {ActiveBackend} stopped delivering frames. Re-probing …");
                            DropCurrentBackend();
                            nextBackendCheck = DateTime.MinValue;
                            consecutiveFailures = 0;
                        }
                        break;
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
    /// Choose / upgrade the capture backend. OpenVR's mirror is preferred but
    /// ONLY when VRChat is the active SteamVR scene application — otherwise
    /// we'd be sampling SteamVR Home or whichever other VR app holds the
    /// scene, which has nothing to do with the PFSignal pixel in VRChat's
    /// rendering. In that case we want a Window-capture of VRChat's HWND.
    /// </summary>
    private void PickBestBackend()
    {
        // If we're already on OpenVR, verify VRChat is still the scene app.
        // If not, downgrade so the next pick can switch to WindowCapture.
        if (ActiveBackend == CaptureBackend.OpenVrMirror)
        {
            if (IsVrChatTheVrScene()) return;
            Logf("OpenVR scene focus isn't VRChat — downgrading to Window capture.");
            ShutdownOpenVR();
            ActiveBackend = CaptureBackend.None;
        }

        // Consider OpenVR only if SteamVR is up AND VRChat is the scene app.
        if (IsSteamVrRunning() && TryInitOpenVR())
        {
            if (IsVrChatTheVrScene())
            {
                if (ActiveBackend == CaptureBackend.WindowCapture)
                {
                    Logf("VRChat is now the SteamVR scene — switching from Window capture to OpenVR mirror.");
                    _vrchatHwnd = IntPtr.Zero;
                }
                else
                {
                    Logf("OpenVR mirror backend active (VRChat is the VR scene).");
                }
                ActiveBackend = CaptureBackend.OpenVrMirror;
                return;
            }
            else
            {
                ShutdownOpenVR();
            }
        }

        if (ActiveBackend == CaptureBackend.WindowCapture) return;
        if (TryInitWindowCapture())
        {
            ActiveBackend = CaptureBackend.WindowCapture;
            Logf("Window capture backend active (PrintWindow on VRChat HWND).");
        }
    }

    /// <summary>True if the SteamVR scene focus is currently a process named
    /// 'VRChat'. Relies on OpenVR already being initialised.</summary>
    private static bool IsVrChatTheVrScene()
    {
        try
        {
            var apps = OpenVR.Applications;
            if (apps is null) return false;
            uint pid = apps.GetCurrentSceneProcessId();
            if (pid == 0) return false;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, "VRChat", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
            case CaptureBackend.WindowCapture:
                _vrchatHwnd = IntPtr.Zero;
                break;
        }
        ActiveBackend = CaptureBackend.None;
    }

    /// <summary>Forward every locked sample to subscribers — even when the
    /// raw bytes are byte-identical to the previous frame. The downstream
    /// temporal smoothing in ControlService needs to see consecutive frames
    /// to accumulate stability count; deduping at this layer would mean a
    /// perfectly steady scene never advances the count past 1 and the
    /// resolver never reaches the stable threshold.</summary>
    private void Handle(PfSignalSample sample)
    {
        _last = sample;
        SignalChanged?.Invoke(this, sample);
    }

    // -------- diagnostic full-frame capture ---------------------------------

    /// <summary>
    /// Dump the active backend's current frame to a PNG and overlay the three
    /// sample positions we'd read (black-finder candidate, white-finder
    /// candidate, signal pixel). Returns the file path on success, null
    /// otherwise. Used by the GUI's Capture button so the user can confirm
    /// where the reader thinks the PFSignal pixels live — particularly
    /// valuable when debugging "the level changes when I look around"
    /// reports in VR, where any text-prompt UI is unusable.
    /// </summary>
    public string? CaptureToFile(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            int seq = System.Threading.Interlocked.Increment(ref _captureCounter);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var path = Path.Combine(dir, $"reon-pfcapture-{seq:D4}-{stamp}.png");

            switch (ActiveBackend)
            {
                case CaptureBackend.OpenVrMirror: return CaptureOpenVrToFile(path, seq);
                case CaptureBackend.WindowCapture: return CaptureWindowToFile(path, seq);
                default: return null;
            }
        }
        catch (Exception ex)
        {
            Logf($"PF capture-to-file failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private string? CaptureOpenVrToFile(string path, int seq)
    {
        if (_compositor is null || _device is null || _context is null) return null;

        IntPtr srvPtr = IntPtr.Zero;
        var err = _compositor.GetMirrorTextureD3D11(EVREye.Eye_Left, _device.NativePointer, ref srvPtr);
        if (err != EVRCompositorError.None || srvPtr == IntPtr.Zero)
        {
            Logf($"PF capture #{seq}: GetMirrorTextureD3D11 returned {err}, srv=0x{srvPtr.ToInt64():x}");
            return null;
        }

        try
        {
            var iidSrv = typeof(ID3D11ShaderResourceView).GUID;
            int hr = Marshal.QueryInterface(srvPtr, ref iidSrv, out var srvOwned);
            if (hr != 0 || srvOwned == IntPtr.Zero) return null;

            using var srv = new ID3D11ShaderResourceView(srvOwned);
            using var resource = srv.Resource;
            using var src = resource.QueryInterface<ID3D11Texture2D>();
            var desc = src.Description;
            if (desc.Width == 0 || desc.Height == 0) return null;
            int w = (int)desc.Width, h = (int)desc.Height;

            // Dedicated staging texture for capture so the Loop's _staging
            // can keep running without contention beyond the lock window.
            using var captureStaging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width, Height = desc.Height,
                MipLevels = 1, ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });

            Bitmap bmp;
            lock (_ctxLock)
            {
                _context.CopyResource(captureStaging, src);
                var map = _context.Map(captureStaging, 0, MapMode.Read);
                try
                {
                    bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    WriteMappedToBitmap(map, desc.Format, w, h, bmp);
                }
                finally { _context.Unmap(captureStaging, 0); }
            }
            using (bmp)
            {
                OverlaySamplePositions(bmp);
                bmp.Save(path, ImageFormat.Png);
            }
            Logf($"PF capture #{seq}: wrote {w}x{h} {desc.Format} srv=0x{srvPtr.ToInt64():x}");
            return path;
        }
        finally
        {
            _compositor.ReleaseMirrorTextureD3D11(srvPtr);
        }
    }

    private string? CaptureWindowToFile(string path, int seq)
    {
        if (_vrchatHwnd == IntPtr.Zero || !IsWindow(_vrchatHwnd)) return null;
        if (!GetClientRect(_vrchatHwnd, out var rect)) return null;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return null;

        IntPtr windowDC = GetWindowDC(_vrchatHwnd);
        if (windowDC == IntPtr.Zero) return null;
        IntPtr memDC = IntPtr.Zero;
        IntPtr hbitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            memDC = CreateCompatibleDC(windowDC);
            if (memDC == IntPtr.Zero) return null;
            hbitmap = CreateCompatibleBitmap(windowDC, w, h);
            if (hbitmap == IntPtr.Zero) return null;
            oldBitmap = SelectObject(memDC, hbitmap);

            if (!PrintWindow(_vrchatHwnd, memDC, PW_CLIENTONLY | PW_RENDERFULLCONTENT))
                return null;

            using (var bmp = Image.FromHbitmap(hbitmap))
            using (var clone = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(clone)) g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
                OverlaySamplePositions(clone);
                clone.Save(path, ImageFormat.Png);
            }
            Logf($"PF capture #{seq}: wrote {w}x{h} via PrintWindow");
            return path;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDC, oldBitmap);
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (memDC != IntPtr.Zero) DeleteDC(memDC);
            ReleaseDC(_vrchatHwnd, windowDC);
        }
    }

    /// <summary>Convert a mapped staging texture to BGRA32 in a Bitmap.
    /// ReadPixel gives us LINEAR floats — for a PNG that's meant for human
    /// inspection we re-encode through the sRGB curve so the saved image
    /// matches what the user actually sees through the headset (Windows
    /// taskbar blue, etc.), not the perceptually-darker linear bytes the
    /// decoder operates on.</summary>
    private static void WriteMappedToBitmap(MappedSubresource map, Format format, int w, int h, Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* src = (byte*)map.DataPointer;
                byte* dst = (byte*)data.Scan0;
                int dstStride = data.Stride;
                for (int y = 0; y < h; y++)
                {
                    byte* srow = src + y * map.RowPitch;
                    byte* drow = dst + y * dstStride;
                    for (int x = 0; x < w; x++)
                    {
                        ReadPixel(srow, x, format, out double r, out double g, out double b);
                        byte rb = (byte)Math.Clamp(Math.Round(LinearToSrgb(r) * 255.0), 0, 255);
                        byte gb = (byte)Math.Clamp(Math.Round(LinearToSrgb(g) * 255.0), 0, 255);
                        byte bb = (byte)Math.Clamp(Math.Round(LinearToSrgb(b) * 255.0), 0, 255);
                        // Format32bppArgb is BGRA in memory on little-endian.
                        drow[x * 4 + 0] = bb;
                        drow[x * 4 + 1] = gb;
                        drow[x * 4 + 2] = rb;
                        drow[x * 4 + 3] = 255;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>IEC 61966-2-1 forward transfer — linear 0..1 → sRGB 0..1.</summary>
    private static double LinearToSrgb(double linear)
    {
        if (linear <= 0) return 0;
        if (linear >= 1) return 1;
        if (linear <= 0.0031308) return 12.92 * linear;
        return 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
    }

    /// <summary>Draw red crosshairs at the three sample positions (both
    /// finder candidates and the signal pixel). Makes it immediately
    /// obvious from the saved PNG whether the reader is hitting the
    /// PFSignal blocks or random scene content.</summary>
    private static void OverlaySamplePositions(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        using var g = Graphics.FromImage(bmp);
        using var penFinder = new Pen(Color.FromArgb(255, 255, 80, 80), 2);
        using var penSignal = new Pen(Color.FromArgb(255, 80, 255, 80), 2);
        using var font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold);
        using var brushFinder = new SolidBrush(Color.FromArgb(255, 255, 80, 80));
        using var brushSignal = new SolidBrush(Color.FromArgb(255, 80, 255, 80));

        void Mark(float fx, float fy, Pen pen, Brush brush, string label)
        {
            int cx = (int)(w * fx);
            int cy = (int)(h * fy);
            int boxHalf = 12; // generous box so it's visible against scene clutter
            g.DrawRectangle(pen, cx - boxHalf, cy - boxHalf, boxHalf * 2, boxHalf * 2);
            g.DrawLine(pen, cx - 24, cy, cx + 24, cy);
            g.DrawLine(pen, cx, cy - 24, cx, cy + 24);
            g.DrawString(label, font, brush, cx + 16, cy + 8);
        }

        Mark(FinderXCenter, FinderTopY, penFinder, brushFinder, "y=0.03 (finder candidate)");
        Mark(FinderXCenter, FinderBotY, penFinder, brushFinder, "y=0.97 (finder candidate)");
        Mark(SamplePointX, FinderTopY, penSignal, brushSignal, "signal? y=0.03");
        Mark(SamplePointX, FinderBotY, penSignal, brushSignal, "signal? y=0.97");
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

    /// <summary>Sample a 4x4 average at the fractional screen position
    /// (fx, fy) on the mapped staging texture. Clamps to valid bounds so a
    /// near-edge fraction (the white finder lives at y=0.97) never reads
    /// past the texture.</summary>
    private static PfSignalSample SampleAtFraction(MappedSubresource map, Format format, int width, int height, float fx, float fy)
    {
        int sx = Math.Clamp((int)(width  * fx) - SampleRegion / 2, 0, width  - SampleRegion);
        int sy = Math.Clamp((int)(height * fy) - SampleRegion / 2, 0, height - SampleRegion);
        return AveragePixel(map, format, sx, sy);
    }

    /// <summary>Pure black: all channels under the strict threshold.</summary>
    private static bool LooksBlack(PfSignalSample p) =>
        p.R <= FinderBlackMax && p.G <= FinderBlackMax && p.B <= FinderBlackMax;

    /// <summary>Pure white: all channels above the strict threshold.</summary>
    private static bool LooksWhite(PfSignalSample p) =>
        p.R >= FinderWhiteMin && p.G >= FinderWhiteMin && p.B >= FinderWhiteMin;

    /// <summary>Detect which way the mirror texture is oriented based on
    /// the two finder samples. The shader puts BLACK at Unity-top and
    /// WHITE at Unity-bottom; in a top-origin memory layout that means
    /// black at low row indices, white at high row indices. A flipped
    /// runtime swaps these.</summary>
    private static Orientation DetectOrientation(PfSignalSample atTop, PfSignalSample atBot)
    {
        if (LooksBlack(atTop) && LooksWhite(atBot)) return Orientation.TopOrigin;
        if (LooksWhite(atTop) && LooksBlack(atBot)) return Orientation.Flipped;
        return Orientation.Unknown;
    }

    /// <summary>Edge-trigger log on finder visibility transitions so the
    /// user knows why the level might not be updating, but without spamming
    /// the log on every frame. Args are the two candidate-position samples;
    /// when transitioning to a visible state we report which of them was
    /// black and which was white so a glance at the log explains the
    /// orientation auto-detect's choice.</summary>
    private void NoteFinderState(bool valid, PfSignalSample atTop, PfSignalSample atBot)
    {
        if (valid == _lastFinderState) return;
        _lastFinderState = valid;
        if (valid) Logf($"PF: finders detected — y0.03={atTop} y0.97={atBot}");
        else       Logf($"PF: finders lost — y0.03={atTop} y0.97={atBot}. Level held until lock returns.");
    }

    /// <summary>
    /// Average the 4x4 sample region into a single RGB triple normalised to
    /// LINEAR space — so the decoder's thresholds (calibrated against the
    /// PFSignal materials' linear shader values 0/0.251/0.502/0.753) hold
    /// regardless of whether the mirror texture is FP16 HDR, sRGB UNORM, or
    /// straight linear UNORM. Without this, an HDR eye buffer would feed the
    /// reader the low byte of a half-precision float (garbage that jitters
    /// every frame) and an sRGB buffer would systematically over-report level
    /// by one notch.
    /// </summary>
    private static PfSignalSample AveragePixel(MappedSubresource map, Format format, int sx, int sy)
    {
        int n = SampleRegion * SampleRegion;
        double r = 0, g = 0, b = 0;

        unsafe
        {
            byte* basePtr = (byte*)map.DataPointer;
            for (int y = 0; y < SampleRegion; y++)
            {
                byte* row = basePtr + (sy + y) * map.RowPitch;
                for (int x = 0; x < SampleRegion; x++)
                {
                    ReadPixel(row, sx + x, format, out double pr, out double pg, out double pb);
                    r += pr; g += pg; b += pb;
                }
            }
        }

        // Now r/g/b are linear 0..1 sums; average and re-encode to byte for
        // downstream consumers (decoder + GUI hex display).
        byte rb = (byte)Math.Clamp(Math.Round(r / n * 255.0), 0, 255);
        byte gb = (byte)Math.Clamp(Math.Round(g / n * 255.0), 0, 255);
        byte bb = (byte)Math.Clamp(Math.Round(b / n * 255.0), 0, 255);
        return new PfSignalSample(rb, gb, bb);
    }

    /// <summary>Read a single pixel from row at column x, decoding whichever
    /// pixel format the mirror exposes, and return LINEAR floating-point RGB
    /// in [0, 1]. Handles every format the OpenVR compositor mirror has been
    /// observed to expose plus the Typeless variants that aliased SRVs can
    /// surface — without explicit cases for those, an SRGB eye buffer with
    /// a Typeless format ID would fall to the BGRA fallback and emit colours
    /// with R / B swapped relative to the actual storage order.</summary>
    private static unsafe void ReadPixel(byte* row, int x, Format format, out double r, out double g, out double b)
    {
        switch (format)
        {
            // BGRA / BGRX 8-bit, all sRGB / UNORM / Typeless variants treated
            // as sRGB-encoded (Unity in linear color space writes through an
            // sRGB-typed view even when the resource is Typeless).
            case Format.B8G8R8A8_UNorm:
            case Format.B8G8R8X8_UNorm:
            case Format.B8G8R8A8_UNorm_SRgb:
            case Format.B8G8R8X8_UNorm_SRgb:
            case Format.B8G8R8A8_Typeless:
            case Format.B8G8R8X8_Typeless:
            {
                byte* p = row + x * 4;
                r = SrgbToLinear(p[2] / 255.0);
                g = SrgbToLinear(p[1] / 255.0);
                b = SrgbToLinear(p[0] / 255.0);
                return;
            }

            case Format.R8G8B8A8_UNorm:
            case Format.R8G8B8A8_UNorm_SRgb:
            case Format.R8G8B8A8_Typeless:
            {
                byte* p = row + x * 4;
                r = SrgbToLinear(p[0] / 255.0);
                g = SrgbToLinear(p[1] / 255.0);
                b = SrgbToLinear(p[2] / 255.0);
                return;
            }

            // R16G16B16A16_FLOAT — half-precision linear, common for HDR
            // eye buffers in VRChat. Read as 4 halves per pixel; no gamma.
            case Format.R16G16B16A16_Float:
            case Format.R16G16B16A16_Typeless:
            {
                ushort* p = (ushort*)(row + x * 8);
                r = HalfToFloat(p[0]);
                g = HalfToFloat(p[1]);
                b = HalfToFloat(p[2]);
                return;
            }

            // R10G10B10A2 UNORM — sometimes used for HDR-lite mirrors.
            case Format.R10G10B10A2_UNorm:
            case Format.R10G10B10A2_Typeless:
            {
                uint* p = (uint*)(row + x * 4);
                uint v = *p;
                r = (v & 0x3FF) / 1023.0;
                g = ((v >> 10) & 0x3FF) / 1023.0;
                b = ((v >> 20) & 0x3FF) / 1023.0;
                return;
            }

            default:
            {
                // Best-effort BGRA fallback (most modern Windows swap chains
                // are BGRA). The format-log message at the top of capture
                // surfaces which one we hit so we can add an explicit case.
                byte* p = row + x * 4;
                r = p[2] / 255.0; g = p[1] / 255.0; b = p[0] / 255.0;
                return;
            }
        }
    }

    /// <summary>IEC 61966-2-1 inverse transfer function. byte → 0..1 sRGB →
    /// linear 0..1. Hot enough to be inlined by tiered JIT; we call it 16
    /// times per sample worst-case.</summary>
    private static double SrgbToLinear(double srgb)
    {
        if (srgb <= 0.04045) return srgb / 12.92;
        return Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }

    /// <summary>IEEE 754 half-precision (binary16) → float, manual decode so
    /// we don't pull in System.Half on netstandard targets. Handles subnormals
    /// and infinity/NaN gracefully (returns 0/clamps).</summary>
    private static double HalfToFloat(ushort h)
    {
        int sign = (h >> 15) & 0x1;
        int exp  = (h >> 10) & 0x1F;
        int mant = h & 0x3FF;

        double value;
        if (exp == 0)
        {
            // subnormal: ±2^-14 × mant/1024
            value = mant == 0 ? 0.0 : Math.Pow(2, -14) * (mant / 1024.0);
        }
        else if (exp == 31)
        {
            value = mant == 0 ? double.PositiveInfinity : double.NaN;
        }
        else
        {
            // normalised: (1 + mant/1024) × 2^(exp-15)
            value = (1.0 + mant / 1024.0) * Math.Pow(2, exp - 15);
        }

        if (sign != 0) value = -value;

        // For our purposes (color), clamp negative + infinity / NaN into 0..1
        if (double.IsNaN(value) || value < 0) return 0;
        if (value > 1) return 1; // PFSignal pixel is always 0..1 anyway
        return value;
    }

    private void Cleanup()
    {
        try { _staging?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        ShutdownOpenVR();
        _staging = null; _context = null; _device = null;
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    private const uint PW_CLIENTONLY        = 0x00000001;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

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
