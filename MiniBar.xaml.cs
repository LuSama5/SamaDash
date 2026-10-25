using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SamaDash.Services;

namespace SamaDash;

/// <summary>
/// Piccola barra sempre visibile al bordo alto: semitrasparente a riposo,
/// piena al passaggio del mouse. Apre la dashboard e le app suggerite.
/// </summary>
public partial class MiniBar : Window
{
    const double IdleOpacity = 0.4;

    readonly MainWindow dashboard;
    readonly DispatcherTimer visibilityTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    DateTime lastRefresh = DateTime.MinValue;

    public MiniBar(MainWindow dashboard)
    {
        InitializeComponent();
        this.dashboard = dashboard;

        dashboard.IsVisibleChanged += (_, _) => UpdateVisibility();
        visibilityTimer.Tick += (_, _) =>
        {
            UpdateVisibility();
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
        Left = Math.Clamp(left, area.Left, area.Right - ActualWidth);
        Top = area.Top;
    }

    async Task RefreshAppsAsync()
    {
        lastRefresh = DateTime.Now;
        Apps.ItemsSource = await dashboard.SuggestedAppsAsync(3);
    }

    void OnMouseEnter(object sender, MouseEventArgs e)
    {
        Fade(1);
        if (DateTime.Now - lastRefresh > TimeSpan.FromSeconds(30)) _ = RefreshAppsAsync();
    }

    void OnMouseLeave(object sender, MouseEventArgs e) => Fade(IdleOpacity);

    void Fade(double to) =>
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(150)));

    void OnOpenDashboard(object sender, RoutedEventArgs e) => dashboard.ShowBar();

    void OnAppClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Entry entry }) dashboard.Open(entry);
    }

    void OnDragHandle(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        Top = SystemParameters.WorkArea.Top;
        Settings.Current.MiniBarLeft = Left;
        Settings.Current.Save();
        Place();
    }
}
