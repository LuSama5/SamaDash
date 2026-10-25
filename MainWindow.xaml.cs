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
        _ = apps.LoadAsync();
        shortcuts.Load();
        BuildSystemBar();
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
        var area = SystemParameters.WorkArea;
        Width = Math.Max(760, area.Width * 0.55);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 10;

        shortcuts.Load();
        RefreshShortcuts();
        _ = RefreshSuggestionsAsync();

        Show();
        Activate();
        Keyboard.Focus(Query);

        var duration = TimeSpan.FromMilliseconds(180);
        Slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-16, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
    }

    public void HideBar()
    {
        if (!IsVisible) return;
        Hide();
        lastHidden = DateTime.Now;
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
        HomePanel.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
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

        Results.ItemsSource = entries;
        if (entries.Count > 0) Results.SelectedIndex = 0;
        Status.Text = error ?? (entries.Count == 0 ? "Nessun risultato" : "");
        Status.Visibility = Status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

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
        var (top, recent) = await Task.Run(() => (Tracker.Top(12), Shell.RecentFiles(3)));

        var suggested = new List<Entry>();
        foreach (var exe in top)
        {
            var entry = ExeToEntry(exe);
            if (entry != null && !suggested.Any(s => s.Name == entry.Name)) suggested.Add(entry);
            if (suggested.Count == 3) break;
        }

        SuggestedApps.ItemsSource = suggested;
        RecentFiles.ItemsSource = recent.Select(p => FileEntry(p, EntrySource.Recent)).ToList();
        SuggestSeparator.Visibility = suggested.Count > 0 && recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestHint.Visibility = suggested.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void RefreshShortcuts()
    {
        var items = shortcuts.Items.Select(s => new Entry
        {
            Name = s.Name,
            Path = s.Path,
            Kind = KindOf(s.Path),
            Source = EntrySource.Shortcut,
            Exe = s.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s.Path : null,
        }).ToList();
        items.Add(new Entry { Name = "Aggiungi", Path = "", Kind = EntryKind.Add, Source = EntrySource.Shortcut });
        ShortcutList.ItemsSource = items;
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

    void Open(Entry entry, bool reveal = false)
    {
        try
        {
            if (reveal)
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
