using System.Drawing;
using System.Drawing.Drawing2D;

namespace SamaDash;

/// <summary>Icona della tray disegnata al volo: quadrato blu-viola con una barra in alto.</summary>
internal static class TrayIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var background = RoundedRect(new RectangleF(1, 1, 30, 30), 8);
            using var gradient = new LinearGradientBrush(new Rectangle(0, 0, 32, 32),
                Color.FromArgb(91, 140, 255), Color.FromArgb(155, 93, 229), 45f);
            g.FillPath(gradient, background);

            using var white = new SolidBrush(Color.White);
            using var soft = new SolidBrush(Color.FromArgb(170, 255, 255, 255));
            using var bar = RoundedRect(new RectangleF(7, 7, 18, 5), 2.5f);
            g.FillPath(white, bar);
            using var left = RoundedRect(new RectangleF(7, 15, 8, 10), 2);
            using var right = RoundedRect(new RectangleF(17, 15, 8, 10), 2);
            g.FillPath(soft, left);
            g.FillPath(soft, right);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
