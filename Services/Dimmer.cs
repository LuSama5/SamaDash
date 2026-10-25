using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SamaDash.Services;

/// <summary>
/// Luminosità "software" per gli schermi che non accettano comandi DDC/CI:
/// un velo nero semitrasparente sopra lo schermo, che lascia passare i clic.
/// </summary>
public static class Dimmer
{
    const double MaxDarkness = 0.85; // a 0% lo schermo resta comunque leggibile

    static readonly Dictionary<string, Window> overlays = [];
    static readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    static bool started;

    /// <summary>Riapplica i livelli salvati e li tiene allineati quando cambiano gli schermi.</summary>
    public static void Start()
    {
        if (started) return;
        started = true;
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            Settings.Current.Save();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => _ = RestoreAsync();
        _ = RestoreAsync();
    }

    static async Task RestoreAsync()
    {
        var displays = await Task.Run(Displays.Enumerate);
        Apply(displays);
    }

    /// <summary>Allinea i veli agli schermi attuali (chiamare sul thread della UI).</summary>
    public static void Apply(IEnumerable<DisplayInfo> displays)
    {
        var soft = displays.Where(d => d.Kind == BrightnessKind.Software).ToList();
        foreach (var key in overlays.Keys.Except(soft.Select(d => d.DevicePath)).ToList())
            Close(key);
        foreach (var d in soft)
            Show(d, d.Brightness);
    }

    public static void Set(DisplayInfo display, int brightness)
    {
        Settings.Current.SoftwareBrightness[display.DevicePath] = brightness;
        saveTimer.Stop();
        saveTimer.Start();
        Show(display, brightness);
    }

    static void Show(DisplayInfo display, int brightness)
    {
        if (brightness >= 100)
        {
            Close(display.DevicePath);
            return;
        }
        if (!overlays.TryGetValue(display.DevicePath, out var overlay))
        {
            overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Black,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                IsHitTestVisible = false,
                Focusable = false,
                Title = "SamaDash dimmer",
            };
            var hwnd = new WindowInteropHelper(overlay).EnsureHandle();
            var style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)(style | Native.WS_EX_TRANSPARENT |
                Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
            overlays[display.DevicePath] = overlay;
            overlay.Show();
        }
        overlay.Opacity = (100 - brightness) / 100.0 * MaxDarkness;
        Place(overlay, display.Bounds);
    }

    static void Place(Window overlay, Native.RECT r)
    {
        var hwnd = new WindowInteropHelper(overlay).Handle;
        // due volte: passando su uno schermo con altra scala WPF ridimensiona la finestra dopo il primo spostamento
        for (int i = 0; i < 2; i++)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    static void Close(string key)
    {
        if (overlays.Remove(key, out var overlay)) overlay.Close();
    }
}
