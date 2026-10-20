using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Rotina.App;

/// <summary>Draws the tray icon: a clock on a circle in the current activity's color.</summary>
static class TrayIcon
{
    public static Icon Create(string hex)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill;
            try { fill = ColorTranslator.FromHtml(hex); } catch (Exception) { fill = Color.Teal; }
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 16, 16, 16, 8);
            g.DrawLine(pen, 16, 16, 22, 19);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(nint handle);
}
