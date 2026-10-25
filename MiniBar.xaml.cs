using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SamaDash.Services;

namespace SamaDash;

/// <summary>
/// Piccola barra sempre visibile, spostabile ovunque: semitrasparente a riposo,
/// piena al passaggio del mouse. Contiene le app fissate, le suggerite e apre la dashboard.
/// </summary>
public partial class MiniBar : Window
{
    const double IdleOpacity = 0.4;
    /// <summary>Margine trasparente attorno alla barra (per l'ombra).</summary>
    const double ShadowMargin = 16;

    readonly MainWindow dashboard;
    readonly DispatcherTimer visibilityTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    DateTime lastRefresh = DateTime.MinValue;

    public MiniBar(MainWindow dashboard)
    {
        InitializeComponent();
        this.dashboard = dashboard;

        dashboard.IsVisibleChanged += (_, _) => UpdateVisibility();
        dashboard.ShortcutsChanged += () => _ = RefreshAppsAsync();
        defaultCover = MusicCover.Background;
        Media.Changed += () => Dispatcher.BeginInvoke(() => _ = RefreshMediaAsync());
        visibilityTimer.Tick += (_, _) =>
        {
            UpdateVisibility();
            _ = RefreshMediaAsync();
            if (DateTime.Now - lastRefresh > TimeSpan.FromMinutes(5)) _ = RefreshAppsAsync();
        };
        SizeChanged += (_, _) => Place();
    }

    public bool Enabled
    {
        get => Settings.Current.MiniBarEnabled;
        set
        {
            Settings.Current.MiniBarEnabled = value;
            Settings.Current.Save();
            UpdateVisibility();
        }
    }

    public async void Start()
    {
        visibilityTimer.Start();
        UpdateTaskbarButton();
        UpdateVisibility();
        await dashboard.AppsLoaded;
        await RefreshAppsAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // non ruba il focus e non compare in Alt+Tab
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)(style | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
    }

    /// <summary>Visibile solo se attiva, con la dashboard chiusa e senza app a schermo intero.</summary>
    void UpdateVisibility()
    {
        bool show = Enabled && !dashboard.IsVisible && !Native.IsFullscreenAppRunning();
        if (show && !IsVisible)
        {
            Show();
            Place();
            UpdateTaskbarButton();
        }
        else if (!show && IsVisible)
        {
            Hide();
        }
    }

    void Place()
    {
        var area = SystemParameters.WorkArea;
        double left = Settings.Current.MiniBarLeft ?? area.Left + (area.Width - ActualWidth) / 2;
        double top = Settings.Current.MiniBarTop ?? area.Top - ShadowMargin + 8;

        // resta dentro gli schermi anche se cambia la configurazione dei monitor
        double minX = SystemParameters.VirtualScreenLeft - ShadowMargin;
        double minY = SystemParameters.VirtualScreenTop - ShadowMargin;
        double maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth + ShadowMargin;
        double maxY = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight + ShadowMargin;
        Left = Math.Clamp(left, minX, Math.Max(minX, maxX));
        Top = Math.Clamp(top, minY, Math.Max(minY, maxY));
    }

    /// <summary>App fissate (shortcut) + suggerite che non sono già tra le fissate.</summary>
    async Task RefreshAppsAsync()
    {
        lastRefresh = DateTime.Now;
        var pinned = await Task.Run(dashboard.AppShortcuts);
        var candidates = await dashboard.SuggestedAppsAsync(10);

        var suggested = candidates
            .Where(s => !pinned.Any(p =>
                p.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase) ||
                (p.Exe != null && p.Exe.Equals(s.Exe, StringComparison.OrdinalIgnoreCase))))
            .Take(3)
            .ToList();

        Pinned.ItemsSource = pinned;
        Suggested.ItemsSource = suggested;
        PinnedDivider.Visibility = pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestedDivider.Visibility = suggested.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnMouseEnter(object sender, MouseEventArgs e)
    {
        Fade(1);
        UpdateTaskbarButton();
        if (DateTime.Now - lastRefresh > TimeSpan.FromSeconds(30)) _ = RefreshAppsAsync();
    }

    void OnMouseLeave(object sender, MouseEventArgs e) => Fade(IdleOpacity);

    void Fade(double to) =>
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(150)));

    void OnOpenDashboard(object sender, RoutedEventArgs e) => dashboard.ShowBar();

    void OnOpenDisplays(object sender, RoutedEventArgs e) => dashboard.ShowDisplays();

    // ---------- In riproduzione ----------

    bool refreshingMedia;

    async Task RefreshMediaAsync()
    {
        if (refreshingMedia || !IsVisible) return;
        refreshingMedia = true;
        try
        {
            var now = await Media.CurrentAsync();
            MusicPanel.Visibility = now == null ? Visibility.Collapsed : Visibility.Visible;
            if (now == null) return;
            MusicTitle.Text = now.Title;
            MusicArtist.Text = now.Artist.Length > 0 ? now.Artist : now.App;
            MusicPlayGlyph.Text = now.Playing ? "" : "";
            MusicNext.IsEnabled = now.CanNext;
            MusicNext.Opacity = now.CanNext ? 1 : 0.35;
            MusicCover.Background = now.Cover != null
                ? new ImageBrush(now.Cover) { Stretch = Stretch.UniformToFill }
                : defaultCover;
        }
        finally
        {
            refreshingMedia = false;
        }
    }

    Brush? defaultCover;

    async void OnMusicPlayPause(object sender, RoutedEventArgs e) { await Media.PlayPauseAsync(); await RefreshMediaAsync(); }
    async void OnMusicNext(object sender, RoutedEventArgs e) { await Media.NextAsync(); await RefreshMediaAsync(); }

    void OnAppClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Entry entry }) dashboard.Open(entry);
    }

    void OnToggleTaskbar(object sender, RoutedEventArgs e)
    {
        Native.TaskbarAutoHide = !Native.TaskbarAutoHide;
        UpdateTaskbarButton();
    }

    public void UpdateTaskbarButton()
    {
        bool autoHide = Native.TaskbarAutoHide;
        TaskbarGlyph.Foreground = autoHide ? (Brush)FindResource("Accent") : Brushes.White;
        TaskbarButton.ToolTip = autoHide
            ? "Barra delle applicazioni a scomparsa: ATTIVA (clic per disattivare)"
            : "Barra delle applicazioni a scomparsa: disattiva (clic per attivare)";
    }

    void OnDragHandle(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        Settings.Current.MiniBarLeft = Left;
        Settings.Current.MiniBarTop = Top;
        Settings.Current.Save();
        Place();
    }

    /// <summary>Riporta la barra in alto al centro.</summary>
    public void ResetPosition()
    {
        Settings.Current.MiniBarLeft = null;
        Settings.Current.MiniBarTop = null;
        Settings.Current.Save();
        Place();
    }
}
