using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SamaDash.Services;

namespace SamaDash;

// Sezione "In riproduzione" della Home.
public partial class MainWindow
{
    readonly DispatcherTimer mediaTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    Brush? defaultCover;
    bool refreshingMedia;

    void InitMedia()
    {
        defaultCover = MusicCover.Background;
        Media.ResolveAppId = apps.NameForAumid;
        Media.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible) _ = RefreshMediaAsync();
        });
        mediaTimer.Tick += (_, _) => _ = RefreshMediaAsync();
    }

    void StartMedia()
    {
        _ = RefreshMediaAsync();
        mediaTimer.Start();
    }

    async Task RefreshMediaAsync()
    {
        if (refreshingMedia) return;
        refreshingMedia = true;
        try
        {
            var now = await Media.CurrentAsync();
            MusicPanel.Visibility = now == null ? Visibility.Collapsed : Visibility.Visible;
            if (now == null) return;

            MusicTitle.Text = now.Title;
            MusicArtist.Text = now.Artist;
            MusicArtist.Visibility = now.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            MusicApp.Text = now.App;
            MusicPlayGlyph.Text = now.Playing ? "" : "";
            MusicPrev.IsEnabled = now.CanPrevious;
            MusicNext.IsEnabled = now.CanNext;
            MusicPrev.Opacity = now.CanPrevious ? 1 : 0.35;
            MusicNext.Opacity = now.CanNext ? 1 : 0.35;

            MusicCover.Background = now.Cover != null
                ? new ImageBrush(now.Cover) { Stretch = Stretch.UniformToFill }
                : defaultCover;
            MusicCoverGlyph.Visibility = now.Cover != null ? Visibility.Collapsed : Visibility.Visible;

            // alcune app (es. i browser su certi siti) non comunicano la durata
            bool timed = now.Duration > TimeSpan.FromSeconds(1);
            MusicProgressRow.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
            if (timed)
            {
                double done = Math.Clamp(now.Position / now.Duration, 0, 1);
                MusicDone.Width = new GridLength(done, GridUnitType.Star);
                MusicLeft.Width = new GridLength(1 - done, GridUnitType.Star);
                MusicPosition.Text = Clock(now.Position);
                MusicDuration.Text = Clock(now.Duration);
            }

            var sources = await Media.SourcesAsync();
            MusicSourceButton.Visibility = sources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            MusicSourceText.Text = $"{sources.Count} fonti";
        }
        finally
        {
            refreshingMedia = false;
        }
    }

    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    async void OnMusicPlayPause(object sender, RoutedEventArgs e) { await Media.PlayPauseAsync(); await RefreshMediaAsync(); }
    async void OnMusicNext(object sender, RoutedEventArgs e) { await Media.NextAsync(); await RefreshMediaAsync(); }
    async void OnMusicPrev(object sender, RoutedEventArgs e) { await Media.PreviousAsync(); await RefreshMediaAsync(); }

    async void OnMusicSource(object sender, RoutedEventArgs e)
    {
        var current = (await Media.CurrentAsync())?.SourceId;
        var menu = new ContextMenu { PlacementTarget = MusicSourceButton };
        foreach (var (id, app) in await Media.SourcesAsync())
        {
            var item = new MenuItem { Header = app, IsCheckable = true, IsChecked = id == current };
            item.Click += (_, _) => Media.Select(id);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
