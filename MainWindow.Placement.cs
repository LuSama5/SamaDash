using System.Windows.Interop;
using SamaDash.Services;

namespace SamaDash;

// Dove si apre la dashboard: dalla mini barra, verso il centro dello schermo.
public partial class MainWindow
{
    /// <summary>Bordo e rettangolo della mini barra (pixel fisici); null se la barra è spenta.</summary>
    internal Func<(BarEdge Edge, Native.RECT Card)?>? AnchorProvider { get; set; }

    /// <summary>Mini barra di riferimento, area di lavoro del suo schermo (o di quello col mouse) e relativa scala.</summary>
    ((BarEdge Edge, Native.RECT Card)? Anchor, Native.RECT Work, double Scale) Target()
    {
        var anchor = AnchorProvider?.Invoke();
        var (x, y) = anchor is { Card: var c } ? ((c.Left + c.Right) / 2, (c.Top + c.Bottom) / 2) : Native.CursorPosition();
        return (anchor, Native.WorkAreaAt(x, y), Native.ScaleAt(x, y));
    }

    /// <summary>
    /// Mini barra in alto o libera: si apre verso il basso partendo da lei. In basso: verso l'alto.
    /// Sui lati: verso il centro. Senza mini barra: in alto sullo schermo dove si trova il mouse.
    /// </summary>
    void PlaceDashboard()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var r = Native.WindowRect(hwnd);
        var (anchor, work, scale) = Target();
        int w = r.Width, h = r.Height, margin = (int)Math.Round(8 * scale);

        int x, y;
        if (anchor is { Edge: var edge, Card: var c })
        {
            int cx = (c.Left + c.Right) / 2, cy = (c.Top + c.Bottom) / 2;
            (x, y) = edge switch
            {
                BarEdge.Bottom => (cx - w / 2, c.Bottom - h),
                BarEdge.Left => (c.Left, cy - h / 2),
                BarEdge.Right => (c.Right - w, cy - h / 2),
                _ => (cx - w / 2, c.Top),
            };
        }
        else
        {
            (x, y) = ((work.Left + work.Right - w) / 2, work.Top + (int)Math.Round(10 * scale));
        }

        x = Math.Clamp(x, work.Left + margin, Math.Max(work.Left + margin, work.Right - w - margin));
        y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - h - margin));
        if (x != r.Left || y != r.Top) MoveTo(this, x, y);
    }

    /// <summary>
    /// Sposta una finestra WPF a una posizione in pixel fisici. Passa da Left/Top (unità WPF = pixel / scala
    /// dello schermo attuale): se la spostassi con SetWindowPos, WPF la riporterebbe dov'era al primo ridimensionamento.
    /// Se cambia schermo e scala, il successivo SizeChanged la riallinea.
    /// </summary>
    internal static void MoveTo(System.Windows.Window window, int x, int y)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        window.Left = x / dpi.DpiScaleX;
        window.Top = y / dpi.DpiScaleY;
    }
}
