using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SamaDash.Services;

namespace SamaDash;

// Scheda "Schermi": una scheda per ogni monitor collegato, costruita in base a ciò che supporta.
public partial class MainWindow
{
    bool loadingDisplays;

    /// <summary>Apre la dashboard direttamente sulla scheda Schermi.</summary>
    public void ShowDisplays()
    {
        ShowBar();
        DisplaysTab.IsChecked = true;
    }

    void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return; // scatta già durante InitializeComponent
        if (Query.Text.Length > 0) Query.Text = ""; // TriggerSearch aggiorna i pannelli
        else UpdatePanels(false);
        if (DisplaysTab.IsChecked == true && IsVisible) _ = RefreshDisplaysAsync();
        Keyboard.Focus(Query);
    }

    void UpdatePanels(bool searching)
    {
        bool displays = DisplaysTab.IsChecked == true;
        HomePanel.Visibility = !searching && !displays ? Visibility.Visible : Visibility.Collapsed;
        DisplaysPanel.Visibility = !searching && displays ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task RefreshDisplaysAsync()
    {
        if (loadingDisplays) return;
        loadingDisplays = true;
        try
        {
            if (DisplayCards.Children.Count == 0) DisplaysStatus.Visibility = Visibility.Visible;
            var list = await Task.Run(Displays.Enumerate);
            var projection = await Task.Run(Displays.CurrentProjection);
            Dimmer.Apply(list);

            DisplayCards.Children.Clear();
            DisplayCards.Columns = Math.Clamp(list.Count, 1, 3);
            DisplayCards.Rows = 0;
            foreach (var d in list) DisplayCards.Children.Add(DisplayCard(d));
            DisplaysStatus.Text = "Nessuno schermo rilevato.";
            DisplaysStatus.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildProjection(list.Count, projection);
        }
        finally
        {
            loadingDisplays = false;
        }
    }

    UIElement DisplayCard(DisplayInfo d)
    {
        var body = new StackPanel();
        var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(6, 0, 6, 0), Child = body };

        // intestazione: icona, nome, risoluzione e badge
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x29, 0x5B, 0x8C, 0xFF)),
            Child = Glyph(d.Internal ? "" : "", 17, (Brush)FindResource("Accent")),
        });
        var detail = Text(d.Detail, 12, muted: true);
        var titles = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = d.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        titles.Children.Add(detail);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        if (d.Primary || d.Internal)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center,
                Child = Text(d.Internal ? "Integrato" : "Principale", 11, muted: true),
            };
            Grid.SetColumn(badge, 2);
            header.Children.Add(badge);
        }
        body.Children.Add(header);

        // luminosità
        body.Children.Add(Label(d.Kind == BrightnessKind.Software ? "Luminosità · attenuazione software" : "Luminosità"));
        body.Children.Add(SliderRow("", d.Brightness, d.SetBrightness));
        if (d.Contrast is int contrast)
        {
            body.Children.Add(Label("Contrasto"));
            body.Children.Add(SliderRow("", contrast, d.SetContrast));
        }
        if (d.Kind == BrightnessKind.Software)
        {
            var hint = new DockPanel { Margin = new Thickness(0, 6, 0, 0) }; // DockPanel: il testo va a capo
            hint.Children.Add(Glyph("", 12, (Brush)FindResource("Muted")));
            hint.Children.Add(new TextBlock
            {
                Text = "Lo schermo non accetta comandi DDC/CI: SamaDash scurisce l'immagine.",
                FontSize = 11, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(6, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(hint);
        }

        body.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 14, 0, 12) });

        // HDR
        body.Children.Add(HdrRow(d));

        // frequenza di aggiornamento
        if (d.Rates.Count > 1)
        {
            body.Children.Add(Label("Frequenza di aggiornamento"));
            var rates = new WrapPanel { Margin = new Thickness(0, 0, 0, -6) };
            foreach (var hz in d.Rates)
            {
                var choice = new RadioButton
                {
                    Style = (Style)FindResource("Choice"), GroupName = "hz" + d.GdiName,
                    Content = $"{hz} Hz", IsChecked = hz == d.Frequency,
                };
                choice.Checked += async (_, _) =>
                {
                    keepOpen = true; // il cambio di modalità può togliere il focus alla barra
                    var ok = await Task.Run(() => Displays.SetRefreshRate(d, hz));
                    keepOpen = false;
                    detail.Text = ok ? d.Detail : "Frequenza non applicata";
                };
                rates.Children.Add(choice);
            }
            body.Children.Add(rates);
        }
        return card;
    }

    UIElement HdrRow(DisplayInfo d)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var accent = (Brush)FindResource("Accent");
        var muted = (Brush)FindResource("Muted");
        var badgeText = new TextBlock { Text = "HDR", FontSize = 10, FontWeight = FontWeights.Bold };
        var badge = new Border
        {
            BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center, Child = badgeText,
        };
        var status = Text("", 11, muted: true);
        var labels = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = "HDR", FontSize = 13, FontWeight = FontWeights.Medium });
        labels.Children.Add(status);
        Grid.SetColumn(labels, 1);

        void Paint()
        {
            badge.BorderBrush = d.HdrOn ? accent : new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
            badgeText.Foreground = d.HdrOn ? accent : muted;
            status.Text = !d.HdrSupported ? "Non supportato da questo schermo"
                : d.HdrOn ? "Attivo · colori e luminosità estesi" : "Disattivato";
        }
        Paint();

        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("Switch"), IsChecked = d.HdrOn, IsEnabled = d.HdrSupported,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toggle.Click += async (_, _) =>
        {
            bool on = toggle.IsChecked == true;
            keepOpen = true;
            var ok = await Task.Run(() => Displays.SetHdr(d, on));
            keepOpen = false;
            if (!ok)
            {
                toggle.IsChecked = d.HdrOn;
                status.Text = "Non riuscito: prova dalle impostazioni di Windows";
                return;
            }
            Paint();
        };
        Grid.SetColumn(toggle, 2);

        row.Children.Add(badge);
        row.Children.Add(labels);
        row.Children.Add(toggle);
        return row;
    }

    Grid SliderRow(string glyph, int value, Action<int> apply)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.Children.Add(Glyph(glyph, 14, (Brush)FindResource("Muted")));

        var text = new TextBlock { Text = $"{value}%", FontSize = 12, FontWeight = FontWeights.Medium, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = value, Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            text.Text = $"{v}%";
            apply(v);
        };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(text, 2);
        row.Children.Add(slider);
        row.Children.Add(text);
        return row;
    }

    void BuildProjection(int displayCount, Displays.Projection? current)
    {
        ProjectionChoices.Children.Clear();
        // con un solo schermo ha senso solo se l'altro è collegato ma spento (Solo PC / Solo secondo)
        bool relevant = displayCount > 1 || current is Displays.Projection.Internal or Displays.Projection.External;
        ProjectionPanel.Visibility = relevant ? Visibility.Visible : Visibility.Hidden;
        if (!relevant) return;

        foreach (var (mode, label) in new[]
        {
            (Displays.Projection.Internal, "Solo schermo PC"),
            (Displays.Projection.Clone, "Duplica"),
            (Displays.Projection.Extend, "Estendi"),
            (Displays.Projection.External, "Solo secondo schermo"),
        })
        {
            var choice = new RadioButton
            {
                Style = (Style)FindResource("Choice"), GroupName = "Projection", Content = label, IsChecked = mode == current,
            };
            choice.Checked += async (_, _) =>
            {
                keepOpen = true;
                Displays.SetProjection(mode);
                await Task.Delay(3000); // Windows impiega qualche secondo a riconfigurare gli schermi
                keepOpen = false;
                if (IsVisible) await RefreshDisplaysAsync();
            };
            ProjectionChoices.Children.Add(choice);
        }
    }

    void OnNightLight(object sender, RoutedEventArgs e) => Open(SettingsPage("Luce notturna", "ms-settings:nightlight"));
    void OnDisplaySettings(object sender, RoutedEventArgs e) => Open(SettingsPage("Impostazioni schermo", "ms-settings:display"));

    static Entry SettingsPage(string name, string uri) => new() { Name = name, Path = uri, Kind = EntryKind.Url };

    TextBlock Label(string text) => new()
    {
        Text = text, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("Muted"),
        Margin = new Thickness(0, 14, 0, 4),
    };

    TextBlock Text(string text, double size, bool muted = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        if (muted) t.Foreground = (Brush)FindResource("Muted");
        return t;
    }

    TextBlock Glyph(string glyph, double size, Brush brush) => new()
    {
        Text = glyph, FontFamily = (FontFamily)FindResource("Glyphs"), FontSize = size, Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
}
