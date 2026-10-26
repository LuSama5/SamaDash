using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using SamaDash.Services;

namespace SamaDash;

public partial class MainWindow : Window
{
    public UsageTracker Tracker { get; } = new();
    /// <summary>Completato quando l'elenco delle app del menu Start è pronto.</summary>
    public Task AppsLoaded { get; }

    readonly AppIndex apps = new();
    readonly ShortcutStore shortcuts = new();
    CancellationTokenSource? searchCts;
    string filter = "";
    DateTime lastHidden;
    /// <summary>True mentre è aperto un dialogo: la barra non deve chiudersi.</summary>
    bool keepOpen;
    bool ready;

    public MainWindow()
    {
        InitializeComponent();
        ready = true;
        AppsLoaded = apps.LoadAsync();
        shortcuts.Load();
        WatchTaskbar();
        BuildSystemBar();
        health.CpuUsage(); // prima lettura di riferimento
        healthTimer.Tick += (_, _) => RefreshHealth();
        InitMedia();
        // i risultati cambiano l'altezza: con la mini barra in basso la dashboard deve crescere verso l'alto
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (IsVisible) PlaceDashboard(); }, DispatcherPriority.Loaded);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor = Colors.Transparent;

        // Windows 11: tema scuro, angoli arrotondati, vetro acrilico
        int dark = 1, round = 2, acrylic = 3;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, sizeof(int));
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        hotKey = new GlobalHotKey(hwnd);
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        var combo = HotKeyCombo.Parse(Settings.Current.Hotkey);
        HotkeyConflict = combo != null && !hotKey.Register(combo);
        UpdateHotkeyText();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (GlobalHotKey.IsOurs(msg, wParam))
        {
            Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------- Scorciatoia da tastiera ----------

    GlobalHotKey? hotKey;

    /// <summary>True se all'avvio la scorciatoia salvata era già usata da un'altra app.</summary>
    public bool HotkeyConflict { get; private set; }

    /// <summary>Scorciatoia attiva da mostrare (es. "Alt+Spazio"), o null se disattivata.</summary>
    public string? HotkeyDisplay => hotKey?.Current?.Display;

    public event Action? HotkeyChanged;

    void OnHotkeyClick(object sender, RoutedEventArgs e) => EditHotkey();

    public void EditHotkey()
    {
        if (hotKey == null) return;
        var previous = hotKey.Current;
        hotKey.Unregister(); // così la combinazione attuale arriva al dialogo invece di aprire la dashboard
        bool changed = false;

        var dialog = new HotkeyDialog(previous, combo =>
        {
            if (combo != null && !hotKey.Register(combo)) return false;
            Settings.Current.Hotkey = combo?.ToString() ?? "";
            Settings.Current.Save();
            changed = true;
            return true;
        });
        if (IsVisible) dialog.Owner = this;
        RunDialog(() => dialog.ShowDialog());

        if (!changed && previous != null) hotKey.Register(previous);
        HotkeyConflict = false;
        UpdateHotkeyText();
        HotkeyChanged?.Invoke();
    }

    void UpdateHotkeyText()
    {
        HotkeyText.Text = HotkeyDisplay ?? "";
        HotkeyButton.ToolTip = HotkeyDisplay != null
            ? $"Scorciatoia per aprire la dashboard: {HotkeyDisplay} (clic per cambiarla)"
            : "Imposta una scorciatoia da tastiera per aprire la dashboard";
    }

    // ---------- Mostra / nascondi ----------

    public void Toggle()
    {
        if (IsVisible) HideBar();
        // il clic sulla tray toglie il focus e chiude la barra un attimo prima: non riaprirla
        else if ((DateTime.Now - lastHidden).TotalMilliseconds > 300) ShowBar();
    }

    public void ShowBar()
    {
        var (anchor, work, scale) = Target();
        Width = Math.Max(760, work.Width / scale * 0.55);
        PlaceDashboard(); // prima di Show: così compare già sullo schermo giusto

        shortcuts.Load();
        RefreshShortcuts();
        _ = RefreshSuggestionsAsync();
        RefreshHealth();
        RefreshControls();
        healthTimer.Start();
        StartMedia();
        if (DisplaysTab.IsChecked == true) _ = RefreshDisplaysAsync();

        Show();
        PlaceDashboard();
        Activate();
        Keyboard.Focus(Query);

        // scivola fuori dal lato della mini barra
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var edge = anchor?.Edge ?? BarEdge.Top;
        bool horizontal = edge is BarEdge.Left or BarEdge.Right;
        double from = edge is BarEdge.Bottom or BarEdge.Right ? 16 : -16;
        Slide.BeginAnimation(TranslateTransform.XProperty, horizontal ? new DoubleAnimation(from, 0, duration) { EasingFunction = ease } : null);
        Slide.BeginAnimation(TranslateTransform.YProperty, horizontal ? null : new DoubleAnimation(from, 0, duration) { EasingFunction = ease });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
    }

    public void HideBar()
    {
        if (!IsVisible) return;
        Hide();
        lastHidden = DateTime.Now;
        healthTimer.Stop();
        mediaTimer.Stop();
        HomeTab.IsChecked = true;
        CleanupPanel.Visibility = Visibility.Collapsed;
        Pin.IsChecked = false;
        Query.Text = "";
    }

    void OnDeactivated(object? sender, EventArgs e)
    {
        if (!keepOpen && Pin.IsChecked != true) HideBar();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool searching = ResultsPanel.Visibility == Visibility.Visible;
        switch (e.Key)
        {
            case Key.Escape:
                if (Query.Text.Length > 0) Query.Text = "";
                else HideBar();
                e.Handled = true;
                break;
            case Key.Down when searching:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up when searching:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter when searching:
                if (Results.SelectedItem is Entry entry)
                    Open(entry, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
        }
    }

    void MoveSelection(int delta)
    {
        if (Results.Items.Count == 0) return;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + delta, 0, Results.Items.Count - 1);
        Results.ScrollIntoView(Results.SelectedItem);
    }

    // ---------- Ricerca ----------

    void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (ready) TriggerSearch();
    }

    void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        filter = (string)((RadioButton)sender).Tag;
        if (!ready) return; // scatta già durante InitializeComponent
        TriggerSearch();
        Keyboard.Focus(Query);
    }

    async void TriggerSearch()
    {
        var query = Query.Text.Trim();
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchCts?.Cancel();

        bool searching = query.Length > 0;
        ResultsPanel.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        UpdatePanels(searching);
        if (!searching)
        {
            Results.ItemsSource = null;
            return;
        }

        var cts = searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(150, cts.Token);
            await RunSearchAsync(query, cts.Token);
        }
        catch (OperationCanceledException) { }
    }

    async Task RunSearchAsync(string query, CancellationToken token)
    {
        var entries = new List<Entry>();
        if (filter == "" && await QuickAnswerAsync(query, token) is { } answer)
            entries.Add(answer);

        if (filter is "" or "app")
            entries.AddRange(apps.Search(query).Take(filter == "app" ? 30 : 5).Select(a => AppToEntry(a, EntrySource.Result)));

        string? error = null;
        if (filter != "app")
        {
            var (paths, err) = await EverythingSearch.SearchAsync(query, filter, 30, token);
            error = err;
            entries.AddRange(paths.Select(p => FileEntry(p, EntrySource.Result)));
        }
        token.ThrowIfCancellationRequested();

        if (filter == "")
        {
            if (LooksLikeSite(query))
                entries.Add(WebEntry($"Apri {query}", query.StartsWith("http") ? query : "https://" + query));
            entries.Add(WebEntry($"Cerca «{query}» su Google", "https://www.google.com/search?q=" + Uri.EscapeDataString(query)));
        }

        Results.ItemsSource = entries;
        if (entries.Count > 0) Results.SelectedIndex = 0;
        Status.Text = error ?? (entries.Count == 0 ? "Nessun risultato" : "");
        Status.Visibility = Status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Risultato di un calcolo, di una conversione di unità o di valuta, o null.</summary>
    static async Task<Entry?> QuickAnswerAsync(string query, CancellationToken token)
    {
        if (Calculator.TryEvaluate(query, out double value))
            return CalcEntry(Calculator.Format(value), query);
        if (Calculator.TryConvertUnits(query, out string converted))
            return CalcEntry(converted, query);
        if (await Currency.TryConvertAsync(query, token) is { } money)
            return CalcEntry(money.Value, money.Detail);
        return null;
    }

    static Entry CalcEntry(string result, string detail) => new()
    {
        Name = "= " + result,
        Path = result,
        Detail = $"{detail} · Invio per copiare",
        Kind = EntryKind.Calc,
        Source = EntrySource.Result,
    };

    static Entry WebEntry(string name, string url) => new()
    {
        Name = name,
        Path = url,
        Detail = url,
        Kind = EntryKind.Web,
        Source = EntrySource.Result,
    };

    /// <summary>"github.com", "www.example.org/pagina": parole senza spazi con un dominio.</summary>
    static bool LooksLikeSite(string query) =>
        !query.Contains(' ') && !query.Contains('\\') &&
        System.Text.RegularExpressions.Regex.IsMatch(query, @"^(https?://)?[\w-]+(\.[\w-]+)*\.[a-z]{2,}(/\S*)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
        !IsFileExtension(query); // "relazione.pdf" è un file, non un sito

    static bool IsFileExtension(string query) =>
        Path.GetExtension(query).ToLowerInvariant() is ".pdf" or ".docx" or ".doc" or ".xlsx" or ".pptx" or ".txt"
            or ".jpg" or ".png" or ".zip" or ".exe" or ".mp4" or ".mp3" or ".cs" or ".json" or ".md";

    void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: Entry entry })
            Open(entry);
    }

    void OnResultRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Results, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: Entry entry } item)
        {
            item.IsSelected = true;
            ShowContextMenu(item, entry);
            e.Handled = true;
        }
    }

    // ---------- Suggeriti e shortcut ----------

    async Task RefreshSuggestionsAsync()
    {
        var recentTask = Task.Run(() => Shell.RecentFiles(3));
        var suggested = await SuggestedAppsAsync(3);
        var recent = await recentTask;

        SuggestedApps.ItemsSource = suggested;
        RecentFiles.ItemsSource = recent.Select(p => FileEntry(p, EntrySource.Recent)).ToList();
        SuggestSeparator.Visibility = suggested.Count > 0 && recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestHint.Visibility = suggested.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Le app più probabili adesso, già pronte da mostrare (usata anche dalla mini barra).</summary>
    public async Task<List<Entry>> SuggestedAppsAsync(int count)
    {
        var top = await Task.Run(() => Tracker.Top(count * 4));
        var suggested = new List<Entry>();
        foreach (var exe in top)
        {
            var entry = ExeToEntry(exe);
            if (entry != null && !suggested.Any(s => s.Name == entry.Name)) suggested.Add(entry);
            if (suggested.Count == count) break;
        }
        return suggested;
    }

    /// <summary>Scatta quando cambiano gli shortcut (aggiunti, rimossi o importati dalla taskbar).</summary>
    public event Action? ShortcutsChanged;

    void RefreshShortcuts()
    {
        var items = ShortcutEntries().ToList();
        items.Add(new Entry { Name = "Aggiungi", Path = "", Kind = EntryKind.Add, Source = EntrySource.Shortcut });
        ShortcutList.ItemsSource = items;
        ShortcutsChanged?.Invoke();
    }

    IEnumerable<Entry> ShortcutEntries() => shortcuts.Items.Select(s => new Entry
    {
        Name = s.Name,
        Path = s.Path,
        Kind = ShortcutStore.IsApp(s.Path) ? EntryKind.App : KindOf(s.Path),
        Source = EntrySource.Shortcut,
        Exe = ShortcutStore.TargetOf(s.Path),
    });

    /// <summary>Gli shortcut che sono app (per la mini barra).</summary>
    public List<Entry> AppShortcuts() => ShortcutEntries().Where(e => e.Kind == EntryKind.App).ToList();

    FileSystemWatcher? taskbarWatcher;

    /// <summary>Quando fissi un'app sulla taskbar viene aggiunta anche agli shortcut.</summary>
    void WatchTaskbar()
    {
        if (shortcuts.SyncTaskbar()) RefreshShortcuts();
        if (!Directory.Exists(ShortcutStore.TaskbarDir)) return;

        var pending = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        pending.Tick += (_, _) =>
        {
            pending.Stop();
            if (shortcuts.SyncTaskbar()) RefreshShortcuts();
        };
        taskbarWatcher = new FileSystemWatcher(ShortcutStore.TaskbarDir, "*.lnk") { EnableRaisingEvents = true };
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => { pending.Stop(); pending.Start(); });
        taskbarWatcher.Created += changed;
        taskbarWatcher.Renamed += (s, e) => changed(s, e);
    }

    void AddShortcut(string path)
    {
        shortcuts.Add(path);
        RefreshShortcuts();
    }

    void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Entry entry } tile) return;
        if (entry.IsAdd) ShowAddMenu(tile);
        else Open(entry);
    }

    void OnTileRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Entry { IsAdd: false } entry } tile)
        {
            ShowContextMenu(tile, entry);
            e.Handled = true;
        }
    }

    void ShowContextMenu(FrameworkElement target, Entry entry)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        AddItem(menu, "Apri", () => Open(entry));
        if (File.Exists(entry.Path) || Directory.Exists(entry.Path))
            AddItem(menu, "Mostra in Esplora file", () => Open(entry, reveal: true));
        AddItem(menu, "Copia percorso", () => { try { Clipboard.SetText(entry.Path); } catch { } });
        if (entry.Source == EntrySource.Shortcut)
            AddItem(menu, "Rimuovi dagli shortcut", () => { shortcuts.Remove(entry.Path); RefreshShortcuts(); });
        else
            AddItem(menu, "Aggiungi agli shortcut", () => AddShortcut(entry.Path));
        menu.IsOpen = true;
    }

    void ShowAddMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        AddItem(menu, "File o app…", () =>
        {
            var dialog = new OpenFileDialog { Title = "Scegli un file o un'app", DereferenceLinks = false };
            if (RunDialog(() => dialog.ShowDialog(this))) AddShortcut(dialog.FileName);
        });
        AddItem(menu, "Cartella…", () =>
        {
            var dialog = new OpenFolderDialog { Title = "Scegli una cartella" };
            if (RunDialog(() => dialog.ShowDialog(this))) AddShortcut(dialog.FolderName);
        });
        var url = ClipboardUrl();
        var web = AddItem(menu,
            url != null ? $"Sito dagli appunti ({ShortcutStore.NameFor(url)})" : "Sito web: copia prima un link",
            () => AddShortcut(url!));
        web.IsEnabled = url != null;
        menu.IsOpen = true;
    }

    static MenuItem AddItem(ContextMenu menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    bool RunDialog(Func<bool?> show)
    {
        keepOpen = true;
        try
        {
            return show() == true;
        }
        finally
        {
            keepOpen = false;
            Activate();
        }
    }

    static string? ClipboardUrl()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            return IsWebUrl(text) ? text : null;
        }
        catch
        {
            return null;
        }
    }

    static bool IsWebUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text)
            ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var file in files) shortcuts.Add(file);
        else if (e.Data.GetData(DataFormats.Text) is string text && IsWebUrl(text.Trim()))
            shortcuts.Add(text.Trim());
        RefreshShortcuts();
        Query.Text = "";
        e.Handled = true;
    }

    // ---------- Apertura ----------

    public void Open(Entry entry, bool reveal = false)
    {
        try
        {
            if (entry.Kind == EntryKind.Calc)
            {
                Clipboard.SetText(entry.Path);
            }
            else if (reveal)
            {
                Shell.Reveal(entry.Path);
            }
            else
            {
                Shell.Open(entry.Path);
                if (entry.Exe != null) Tracker.RecordLaunch(entry.Exe);
            }
            if (Pin.IsChecked != true) HideBar();
        }
        catch (Exception ex)
        {
            ShowError($"Impossibile aprire {entry.Name}: {ex.Message}");
        }
    }

    void ShowError(string message)
    {
        keepOpen = true;
        if (IsVisible) MessageBox.Show(this, message, "SamaDash", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(message, "SamaDash", MessageBoxButton.OK, MessageBoxImage.Warning);
        keepOpen = false;
    }

    Entry? ExeToEntry(string exe)
    {
        var app = apps.ForExe(exe);
        if (app != null)
            return new Entry { Name = app.Name, Path = app.Launch, Kind = EntryKind.App, Source = EntrySource.Suggested, Exe = exe };

        // le app dello Store non si avviano dal loro exe
        if (exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return null;

        var description = FileVersionInfo.GetVersionInfo(exe).FileDescription;
        return new Entry
        {
            Name = string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(exe) : description,
            Path = exe,
            Kind = EntryKind.App,
            Source = EntrySource.Suggested,
            Exe = exe,
        };
    }

    static Entry AppToEntry(AppEntry app, EntrySource source) => new()
    {
        Name = app.Name,
        Path = app.Launch,
        Detail = app.Aumid != null ? "App dello Store" : "App",
        Kind = EntryKind.App,
        Source = source,
        Exe = app.Target,
    };

    static Entry FileEntry(string path, EntrySource source)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        return new Entry
        {
            Name = name.Length > 0 ? name : path,
            Path = path,
            Detail = Path.GetDirectoryName(path) ?? path,
            Kind = KindOf(path),
            Source = source,
            Exe = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null,
        };
    }

    static EntryKind KindOf(string path) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? EntryKind.Url
        : Directory.Exists(path) ? EntryKind.Folder
        : EntryKind.File;

    // ---------- Salute PC ----------

    readonly SystemHealth health = new();
    readonly DispatcherTimer healthTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    DispatcherTimer? cleanArmed;

    void RefreshHealth()
    {
        HealthBars.Children.Clear();
        foreach (var disk in SystemHealth.Disks())
            HealthBars.Children.Add(HealthRow($"Disco {disk.Name}", disk.UsedFraction,
                $"{SystemHealth.FormatBytes(disk.FreeBytes)} liberi"));

        var (ram, total) = SystemHealth.Memory();
        HealthBars.Children.Add(HealthRow("RAM", ram, $"{ram:P0} di {SystemHealth.FormatBytes((long)total)}"));
        double cpu = health.CpuUsage();
        HealthBars.Children.Add(HealthRow("CPU", cpu, $"{cpu:P0}"));
    }

    /// <summary>Riga etichetta | barra | valore. La barra diventa rossa sopra il 90%.</summary>
    static Grid HealthRow(string label, double fraction, string value)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });

        var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
        var track = new Border { Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(3) };
        Grid.SetColumnSpan(track, 2);
        var color = fraction > 0.9 ? Color.FromRgb(0xE5, 0x48, 0x4D)
            : fraction > 0.75 ? Color.FromRgb(0xF0, 0xA0, 0x30)
            : Color.FromRgb(0x5B, 0x8C, 0xFF);
        var fill = new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(3) };
        bar.Children.Add(track);
        bar.Children.Add(fill);

        var name = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock
        {
            Text = value, FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(text, 2);
        row.Children.Add(name);
        row.Children.Add(bar);
        row.Children.Add(text);
        return row;
    }

    async void OnCleanupToggle(object sender, RoutedEventArgs e)
    {
        if (CleanupPanel.Visibility == Visibility.Visible)
        {
            CleanupPanel.Visibility = Visibility.Collapsed;
            return;
        }
        CleanupPanel.Visibility = Visibility.Visible;
        CleanupStatus.Text = "";
        await ScanCleanupAsync();
    }

    async Task ScanCleanupAsync()
    {
        CleanupItems.Children.Clear();
        CleanupItems.Children.Add(new TextBlock { Text = "Analisi in corso…", FontSize = 12, Foreground = (Brush)FindResource("Muted") });
        CleanButton.IsEnabled = false;

        var items = await Task.Run(SystemHealth.Scan);
        CleanupItems.Children.Clear();
        foreach (var item in items)
        {
            var label = $"{item.Label} — {SystemHealth.FormatBytes(item.Bytes)}" + (item.Disabled != null ? $" ({item.Disabled})" : "");
            CleanupItems.Children.Add(new CheckBox
            {
                Content = label,
                Tag = item.Key,
                IsChecked = item.Disabled == null && item.Bytes > 0,
                IsEnabled = item.Disabled == null && item.Bytes > 0,
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 2),
            });
        }
        long total = items.Where(i => i.Disabled == null).Sum(i => i.Bytes);
        CleanupStatus.Text = $"Recuperabili: {SystemHealth.FormatBytes(total)}";
        CleanButton.IsEnabled = total > 0;
    }

    /// <summary>Primo clic: chiede conferma. Secondo clic entro 3 secondi: pulisce.</summary>
    async void OnClean(object sender, RoutedEventArgs e)
    {
        if (cleanArmed == null)
        {
            CleanButtonText.Text = "Conferma?";
            CleanButton.Background = new SolidColorBrush(Color.FromArgb(0x66, 0xE5, 0x48, 0x4D));
            cleanArmed = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            cleanArmed.Tick += (_, _) => ResetCleanButton();
            cleanArmed.Start();
            return;
        }
        ResetCleanButton();

        var keys = CleanupItems.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        CleanButton.IsEnabled = false;
        CleanupStatus.Text = "Pulizia in corso…";
        var hwnd = new WindowInteropHelper(this).Handle;
        long freed = await Task.Run(() => SystemHealth.Clean(keys, hwnd));
        RefreshHealth();
        await ScanCleanupAsync();
        CleanupStatus.Text = $"Liberati {SystemHealth.FormatBytes(freed)}";
    }

    void ResetCleanButton()
    {
        cleanArmed?.Stop();
        cleanArmed = null;
        CleanButtonText.Text = "Pulisci selezionati";
        CleanButton.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x5B, 0x8C, 0xFF));
    }

    void OnAdvancedCleanup(object sender, RoutedEventArgs e)
    {
        HideBar();
        Shell.Open("cleanmgr.exe");
    }

    // ---------- Controlli rapidi ----------

    bool updatingControls;

    void RefreshControls()
    {
        updatingControls = true;
        var outputs = AudioDevices.Outputs();
        OutputName.Text = outputs.FirstOrDefault(o => o.IsDefault)?.Name ?? "Nessuna uscita audio";
        double volume = Math.Round(AudioDevices.Volume * 100);
        VolumeSlider.Value = volume;
        VolumeText.Text = $"{volume:0}%";
        MuteGlyph.Text = AudioDevices.Muted ? "" : "";

        bool dark = QuickSettings.DarkMode;
        ThemeText.Text = dark ? "Tema scuro" : "Tema chiaro";
        SetActive(ThemeButton, dark);
        SetActive(DndButton, QuickSettings.DoNotDisturb);
        updatingControls = false;
    }

    static void SetActive(Button button, bool active)
    {
        if (active) button.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x5B, 0x8C, 0xFF));
        else button.ClearValue(BackgroundProperty);
    }

    void OnOutputClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = OutputButton };
        foreach (var output in AudioDevices.Outputs())
        {
            var item = AddItem(menu, output.Name, () =>
            {
                try { AudioDevices.SetDefault(output.Id); }
                catch (Exception ex) { ShowError($"Impossibile cambiare uscita audio: {ex.Message}"); }
                RefreshControls();
            });
            item.IsChecked = output.IsDefault;
        }
        if (menu.Items.Count == 0) AddItem(menu, "Nessuna uscita audio attiva", () => { }).IsEnabled = false;
        menu.IsOpen = true;
    }

    void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        VolumeText.Text = $"{e.NewValue:0}%";
        if (updatingControls) return;
        AudioDevices.Volume = (float)(e.NewValue / 100);
        if (AudioDevices.Muted && e.NewValue > 0)
        {
            AudioDevices.Muted = false;
            MuteGlyph.Text = "";
        }
    }

    void OnMuteClick(object sender, RoutedEventArgs e)
    {
        AudioDevices.Muted = !AudioDevices.Muted;
        RefreshControls();
    }

    void OnThemeClick(object sender, RoutedEventArgs e)
    {
        QuickSettings.DarkMode = !QuickSettings.DarkMode;
        RefreshControls();
    }

    void OnDndClick(object sender, RoutedEventArgs e)
    {
        QuickSettings.DoNotDisturb = !QuickSettings.DoNotDisturb;
        RefreshControls();
    }

    // ---------- Azioni di sistema ----------

    void BuildSystemBar()
    {
        AddAction("", "Blocca", false, () => Native.LockWorkStation());
        AddAction("", "Sospendi", false, () =>
            System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false));
        AddAction("", "Riavvia", true, () => RunHidden("shutdown", "/r /t 0"));
        AddAction("", "Spegni", true, () => RunHidden("shutdown", "/s /t 0"));
        AddAction("", "Svuota cestino", true, () =>
            // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND: la conferma la chiede già la barra
            Native.SHEmptyRecycleBin(new WindowInteropHelper(this).Handle, null, 0x7));
        AddAction("", "Impostazioni", false, () => Shell.Open("ms-settings:"));
        AddAction("", "Gestione attività", false, () => Shell.Open("taskmgr.exe"));
    }

    static void RunHidden(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });

    /// <param name="confirm">Se true serve un secondo clic entro 3 secondi.</param>
    void AddAction(string glyph, string label, bool confirm, Action action)
    {
        var text = new TextBlock { Text = label, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Glyphs"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center };
        var button = new Button
        {
            Style = (Style)FindResource("Flat"),
            Width = 92,
            Padding = new Thickness(4, 10, 4, 8),
            Margin = new Thickness(0, 0, 4, 4),
            Content = new StackPanel { Children = { icon, text } },
        };

        DispatcherTimer? armed = null;
        void Reset()
        {
            armed?.Stop();
            armed = null;
            text.Text = label;
            button.ClearValue(BackgroundProperty);
        }

        button.Click += (_, _) =>
        {
            if (confirm && armed == null)
            {
                text.Text = "Conferma?";
                button.Background = new SolidColorBrush(Color.FromArgb(0x66, 0xE5, 0x48, 0x4D));
                armed = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                armed.Tick += (_, _) => Reset();
                armed.Start();
                return;
            }
            Reset();
            HideBar();
            try { action(); }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        SystemBar.Children.Add(button);
    }
}
