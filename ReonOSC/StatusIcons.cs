using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ReonOSC.Ble;

namespace ReonOSC;

public enum IconState { Off, Cool, Heat, Smart }

/// <summary>
/// Pre-renders the Reon icon in four state variants (off / cool / heat / smart)
/// as small bitmap-backed Icons usable for NotifyIcon and Form.Icon.
///
/// The geometry mirrors assets/reon.svg (viewBox 0 0 256 256). Plate fill and
/// indicator dot colours change with state; everything else is constant.
/// </summary>
public sealed class StatusIcons : IDisposable
{
    private const int RenderSize = 32;

    private readonly Dictionary<IconState, IconEntry> _icons = new();

    public StatusIcons()
    {
        foreach (IconState s in Enum.GetValues<IconState>())
            _icons[s] = Build(s);
    }

    public Icon For(IconState state) => _icons[state].Icon;

    public Icon For(ReonProtocol.Mode mode) => For(mode switch
    {
        ReonProtocol.Mode.Cool => IconState.Cool,
        ReonProtocol.Mode.Heat => IconState.Heat,
        ReonProtocol.Mode.Smart => IconState.Smart,
        _ => IconState.Off,
    });

    private static IconEntry Build(IconState state)
    {
        var bmp = new Bitmap(RenderSize, RenderSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            DrawIcon(g, state);
        }
        var hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);
        bmp.Dispose();
        return new IconEntry(icon, hIcon);
    }

    private static void DrawIcon(Graphics g, IconState state)
    {
        // The SVG works in a 0..256 coordinate system. We render at RenderSize px.
        const float source = 256f;
        float scale = RenderSize / source;
        g.ScaleTransform(scale, scale);

        var (plateFill, indicatorColor) = ColoursFor(state);
        var bodyFill = Color.FromArgb(244, 244, 244);
        var stroke   = Color.FromArgb(34, 34, 34);

        // device body
        using (var path = RoundedRect(74, 28, 108, 200, 42))
        using (var bodyBrush = new SolidBrush(bodyFill))
        using (var bodyPen   = new Pen(stroke, 10) { LineJoin = LineJoin.Round })
        {
            g.FillPath(bodyBrush, path);
            g.DrawPath(bodyPen, path);
        }

        // top vents (two horizontal lines)
        using (var vent1 = new Pen(stroke, 10) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(vent1, 98, 68, 158, 68);
        using (var vent2 = new Pen(Color.FromArgb(192, stroke), 8) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(vent2, 102, 92, 154, 92);

        // cooling plate
        using (var path = RoundedRect(92, 112, 72, 54, 18))
        using (var plateBrush = new SolidBrush(plateFill))
        using (var platePen   = new Pen(stroke, 8) { LineJoin = LineJoin.Round })
        {
            g.FillPath(plateBrush, path);
            g.DrawPath(platePen, path);
        }

        // cold mark in plate — 4 strokes (vertical, horizontal, two diagonals)
        using (var mark = new Pen(Color.FromArgb(192, stroke), 5)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        })
        {
            g.DrawLine(mark, 128, 122, 128, 156);   // |
            g.DrawLine(mark, 113, 139, 143, 139);   // —
            g.DrawLine(mark, 117, 128, 139, 150);   // \
            g.DrawLine(mark, 139, 128, 117, 150);   // /
        }

        // bottom indicator
        using (var indicator = new SolidBrush(indicatorColor))
            g.FillEllipse(indicator, 120, 186, 16, 16);
    }

    private static (Color plate, Color indicator) ColoursFor(IconState state) => state switch
    {
        IconState.Cool  => (Color.FromArgb(217, 237, 247), Color.FromArgb(30, 136, 196)),    // blue
        IconState.Heat  => (Color.FromArgb(247, 217, 217), Color.FromArgb(196,  50,  30)),   // red
        IconState.Smart => (Color.FromArgb(247, 240, 217), Color.FromArgb(196, 156,  30)),   // amber
        _               => (Color.FromArgb(232, 232, 232), Color.FromArgb( 34,  34,  34)),   // off / neutral
    };

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(x,             y,             r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y,             r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2,   0, 90);
        path.AddArc(x,             y + h - r * 2, r * 2, r * 2,  90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        foreach (var entry in _icons.Values)
        {
            entry.Icon.Dispose();
            DestroyIcon(entry.Handle);
        }
        _icons.Clear();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly record struct IconEntry(Icon Icon, IntPtr Handle);
}
