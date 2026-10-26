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
        // ridimensionandosi (espansione, orientamento, musica) resta ancorata al bordo e al centro salvato
        // a layout finito: se la sposto durante il ridimensionamento WPF ignora la nuova posizione
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded);
        expandTimer.Tick += (_, _) =>
        {
            expandTimer.Stop();
            if (!dragging && IsMouseOver) SetExpanded(true);
        };
        collapseTimer.Tick += (_, _) =>
        {
            collapseTimer.Stop();
            // il ridimensionamento può far scattare un MouseLeave finto: ricontrollo
            if (!dragging && !IsMouseOver && !Native.IsCursorOver(Hwnd)) SetExpanded(false);
        };
        dashboard.AnchorProvider = AnchorInfo;
        ApplyLayout();
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
        bool show = Enabled && !dashboard.IsVisible &&
                    !Native.IsFullscreenAppRunning(new WindowInteropHelper(this).EnsureHandle());
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

    // ---------- Posizione e aggancio ai bordi ----------
    // Tutto in pixel fisici: con schermi a scale diverse le coordinate WPF non bastano.

    IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    BarEdge Edge => Settings.Current.MiniBarEdge;

    /// <summary>Centro salvato della barra, o in alto al centro dello schermo principale.</summary>
    (int X, int Y) Anchor()
    {
        if (Settings.Current.MiniBarX is int x && Settings.Current.MiniBarY is int y) return (x, y);
        var work = Native.PrimaryWorkArea();
        return ((work.Left + work.Right) / 2, work.Top + 1);
    }

    /// <summary>Bordo e rettangolo visibile (senza l'ombra) della barra, per aprire la dashboard da lì; null se la barra è spenta.</summary>
    internal (BarEdge Edge, Native.RECT Card)? AnchorInfo()
    {
        if (!Enabled || Hwnd == IntPtr.Zero) return null;
        var r = Native.WindowRect(Hwnd);
        int shadow = Shadow(r);
        return (Edge, new Native.RECT { Left = r.Left + shadow, Top = r.Top + shadow, Right = r.Right - shadow, Bottom = r.Bottom - shadow });
    }

    int Shadow(Native.RECT r) =>
        (int)Math.Round(ShadowMargin * Native.ScaleAt((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2));

    void Place()
    {
        if (Hwnd == IntPtr.Zero || !IsVisible) return;
        var r = Native.WindowRect(Hwnd);
        var (ax, ay) = Anchor();
        var work = Native.WorkAreaAt(ax, ay);
        double scale = Native.ScaleAt(ax, ay);
        int shadow = (int)Math.Round(ShadowMargin * scale), gap = (int)Math.Round(6 * scale);

        // centrata sul punto salvato, appoggiata al bordo a cui è agganciata
        int x = ax - r.Width / 2, y = ay - r.Height / 2;
        switch (Edge)
        {
            case BarEdge.Top: y = work.Top - shadow + gap; break;
            case BarEdge.Bottom: y = work.Bottom - r.Height + shadow - gap; break;
            case BarEdge.Left: x = work.Left - shadow + gap; break;
            case BarEdge.Right: x = work.Right - r.Width + shadow - gap; break;
        }
        // sempre dentro lo schermo, anche se cambia la configurazione dei monitor
        x = Math.Clamp(x, work.Left - shadow, Math.Max(work.Left - shadow, work.Right - r.Width + shadow));
        y = Math.Clamp(y, work.Top - shadow, Math.Max(work.Top - shadow, work.Bottom - r.Height + shadow));
        if (x != r.Left || y != r.Top) MainWindow.MoveTo(this, x, y);
    }

    /// <summary>Dopo un trascinamento: il bordo più vicino (entro una soglia) diventa quello di aggancio.</summary>
    void SnapToEdge()
    {
        var card = AnchorInfo()!.Value.Card;
        int cx = (card.Left + card.Right) / 2, cy = (card.Top + card.Bottom) / 2;
        var work = Native.WorkAreaAt(cx, cy);
        int threshold = (int)Math.Round(48 * Native.ScaleAt(cx, cy));

        var distances = new (BarEdge Edge, int Distance)[]
        {
            (BarEdge.Top, card.Top - work.Top),
            (BarEdge.Bottom, work.Bottom - card.Bottom),
            (BarEdge.Left, card.Left - work.Left),
            (BarEdge.Right, work.Right - card.Right),
        };
        // a parità (angoli) vince l'aggancio orizzontale, in alto o in basso
        var nearest = distances.OrderBy(d => d.Distance).First();
        var s = Settings.Current;
        s.MiniBarEdge = nearest.Distance <= threshold ? nearest.Edge : BarEdge.Free;
        s.MiniBarX = cx;
        s.MiniBarY = cy;
        s.Save();
        ApplyLayout();
        Place();
    }

    /// <summary>Riporta la barra in alto al centro dello schermo principale.</summary>
    public void ResetPosition()
    {
        var s = Settings.Current;
        s.MiniBarX = s.MiniBarY = null;
        s.MiniBarEdge = BarEdge.Top;
        s.Save();
        ApplyLayout();
        Place();
    }

    // ---------- Orientamento e versione compatta ----------

    bool expanded;
    readonly DispatcherTimer expandTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public bool Compact
    {
        get => Settings.Current.MiniBarCompact;
        set
        {
            Settings.Current.MiniBarCompact = value;
            Settings.Current.Save();
            expanded = false;
            ApplyLayout();
            AnimateChange();
        }
    }

    /// <summary>Pulsante sulla barra: passa da compatta a estesa e viceversa.</summary>
    void OnToggleCompact(object sender, RoutedEventArgs e)
    {
        // il mouse è ancora sopra: senza questo la pillola si riaprirebbe subito
        holdCollapsed = !Compact;
        Compact = !Compact;
    }

    /// <summary>True dopo aver scelto "compatta" dalla barra, finché il mouse non esce.</summary>
    bool holdCollapsed;

    /// <summary>Animazione leggera al cambio di forma: il contenuto entra con dissolvenza e un piccolo zoom.</summary>
    void AnimateChange()
    {
        if (!IsVisible) return;
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(0.92, 1, duration) { EasingFunction = ease };
        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    /// <summary>Verticale sui bordi laterali, orizzontale altrove; pillola se compatta e a riposo.</summary>
    void ApplyLayout()
    {
        bool vertical = Edge is BarEdge.Left or BarEdge.Right;
        bool pill = Compact && !expanded;
        Pill.Visibility = pill ? Visibility.Visible : Visibility.Collapsed;
        Full.Visibility = pill ? Visibility.Collapsed : Visibility.Visible;

        var orientation = vertical ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal;
        Full.Orientation = Pill.Orientation = MusicRow.Orientation = MusicPanel.Orientation = orientation;
        var divider = (Style)FindResource(vertical ? "DividerV" : "Divider");
        foreach (var d in new[] { PinnedDivider, SuggestedDivider, MusicDivider, ToolsDivider }) d.Style = divider;
        var panel = (System.Windows.Controls.ItemsPanelTemplate)FindResource(vertical ? "Column" : "Row");
        Pinned.ItemsPanel = Suggested.ItemsPanel = panel;

        // in verticale restano solo le icone
        SearchText.Visibility = MusicText.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        MusicInfoButton.Padding = vertical ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        PillCover.Margin = vertical ? new Thickness(0, 0, 0, 7) : new Thickness(0, 0, 7, 0);
        Handle.Text = vertical ? "" : "";
        Handle.Padding = vertical ? new Thickness(12, 8, 12, 8) : new Thickness(8, 12, 8, 12);
        // la freccia indica dove si aprirà la dashboard
        CompactGlyph.Text = Compact ? "" : "";
        CompactButton.ToolTip = Compact
            ? "Mini barra sempre estesa"
            : "Mini barra compatta: una pillola che si espande al passaggio del mouse";
        OpenGlyph.Text = Edge switch
        {
            BarEdge.Bottom => "",
            BarEdge.Left => "",
            BarEdge.Right => "",
            _ => "",
        };
    }

    void SetExpanded(bool value)
    {
        if (expanded == value) return;
        expanded = value;
        ApplyLayout();
        AnimateChange();
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
        collapseTimer.Stop();
        if (Compact && !expanded && !holdCollapsed) expandTimer.Start(); // piccolo ritardo: passandoci sopra per caso non si apre
        if (DateTime.Now - lastRefresh > TimeSpan.FromSeconds(30)) _ = RefreshAppsAsync();
    }

    void OnMouseLeave(object sender, MouseEventArgs e)
    {
        Fade(IdleOpacity);
        expandTimer.Stop();
        holdCollapsed = false;
        if (expanded) collapseTimer.Start();
    }

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
            PillCover.Visibility = MusicPanel.Visibility;
            if (now == null) return;
            MusicTitle.Text = now.Title;
            MusicArtist.Text = now.Artist.Length > 0 ? now.Artist : now.App;
            MusicPlayGlyph.Text = now.Playing ? "" : "";
            MusicNext.IsEnabled = now.CanNext;
            MusicNext.Opacity = now.CanNext ? 1 : 0.35;
            MusicCover.Background = PillCover.Background = now.Cover != null
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
        dragging = true;
        DragMove();
        dragging = false;
        SnapToEdge();
    }

    bool dragging;
}
