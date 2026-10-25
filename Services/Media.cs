using System.IO;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace SamaDash.Services;

/// <summary>Brano in riproduzione in una delle app che usano i controlli multimediali di Windows.</summary>
public sealed record NowPlaying(
    string SourceId, string App, string Title, string Artist, BitmapImage? Cover,
    bool Playing, bool CanPrevious, bool CanNext, TimeSpan Position, TimeSpan Duration);

/// <summary>
/// Legge e controlla la musica tramite i controlli multimediali di sistema (gli stessi del pannello volume di Windows):
/// funziona con Spotify, browser, lettori e qualsiasi app che li supporti.
/// </summary>
public static class Media
{
    static GlobalSystemMediaTransportControlsSessionManager? manager;
    static string? selectedId;
    static (string Key, BitmapImage? Image) coverCache = ("", null);

    /// <summary>Nome di un'app dello Store dal suo AppUserModelID (lo imposta la dashboard).</summary>
    public static Func<string, string?>? ResolveAppId { get; set; }

    /// <summary>Scatta quando cambia brano, stato o elenco delle fonti (da un thread qualsiasi).</summary>
    public static event Action? Changed;

    static async Task<GlobalSystemMediaTransportControlsSessionManager?> ManagerAsync()
    {
        if (manager != null) return manager;
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch
        {
            return null;
        }
        manager.SessionsChanged += (_, _) => { Hook(); Changed?.Invoke(); };
        manager.CurrentSessionChanged += (_, _) => Changed?.Invoke();
        Hook();
        return manager;
    }

    static readonly HashSet<string> hooked = [];

    static void Hook()
    {
        foreach (var s in manager!.GetSessions())
        {
            if (!hooked.Add(s.SourceAppUserModelId)) continue;
            s.MediaPropertiesChanged += (_, _) => Changed?.Invoke();
            s.PlaybackInfoChanged += (_, _) => Changed?.Invoke();
        }
    }

    /// <summary>Nomi delle app con una sessione multimediale aperta.</summary>
    public static async Task<List<(string Id, string App)>> SourcesAsync()
    {
        var m = await ManagerAsync();
        return m == null ? [] : m.GetSessions().Select(s => (s.SourceAppUserModelId, AppName(s.SourceAppUserModelId))).ToList();
    }

    public static void Select(string id)
    {
        selectedId = id;
        Changed?.Invoke();
    }

    /// <summary>La fonte scelta dall'utente, altrimenti quella che Windows considera attuale.</summary>
    static async Task<Session?> SessionAsync()
    {
        var m = await ManagerAsync();
        if (m == null) return null;
        var sessions = m.GetSessions();
        return sessions.FirstOrDefault(s => s.SourceAppUserModelId == selectedId)
            ?? m.GetCurrentSession()
            ?? sessions.FirstOrDefault();
    }

    /// <summary>Stato attuale, o null se nessuna app sta riproducendo qualcosa.</summary>
    public static async Task<NowPlaying?> CurrentAsync()
    {
        try
        {
            var s = await SessionAsync();
            if (s == null) return null;
            var props = await s.TryGetMediaPropertiesAsync();
            if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;
            var info = s.GetPlaybackInfo();
            var timeline = s.GetTimelineProperties();

            var playing = info.PlaybackStatus == Status.Playing;
            var position = timeline.Position;
            // la posizione viene aggiornata solo a ogni evento: la stimo dal tempo passato
            if (playing && timeline.LastUpdatedTime.Year > 2000)
                position += DateTimeOffset.Now - timeline.LastUpdatedTime;
            var duration = timeline.EndTime - timeline.StartTime;
            if (position > duration && duration > TimeSpan.Zero) position = duration;

            var artist = props.Artist;
            if (!string.IsNullOrWhiteSpace(props.AlbumTitle) && props.AlbumTitle != props.Title)
                artist = string.IsNullOrWhiteSpace(artist) ? props.AlbumTitle : $"{artist} · {props.AlbumTitle}";

            return new NowPlaying(
                s.SourceAppUserModelId, AppName(s.SourceAppUserModelId), props.Title, artist ?? "",
                await CoverAsync(s.SourceAppUserModelId + props.Title, props.Thumbnail),
                playing, info.Controls.IsPreviousEnabled, info.Controls.IsNextEnabled,
                position < TimeSpan.Zero ? TimeSpan.Zero : position, duration);
        }
        catch
        {
            return null;
        }
    }

    public static async Task PlayPauseAsync() { if (await SessionAsync() is { } s) await s.TryTogglePlayPauseAsync(); }
    public static async Task NextAsync() { if (await SessionAsync() is { } s) await s.TrySkipNextAsync(); }
    public static async Task PreviousAsync() { if (await SessionAsync() is { } s) await s.TrySkipPreviousAsync(); }

    static async Task<BitmapImage?> CoverAsync(string key, Windows.Storage.Streams.IRandomAccessStreamReference? thumb)
    {
        if (coverCache.Key == key) return coverCache.Image;
        BitmapImage? image = null;
        if (thumb != null)
        {
            try
            {
                using var stream = await thumb.OpenReadAsync();
                using var input = stream.AsStreamForRead();
                var copy = new MemoryStream();
                await input.CopyToAsync(copy);
                copy.Position = 0;
                image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 128;
                image.StreamSource = copy;
                image.EndInit();
                image.Freeze();
            }
            catch
            {
                image = null;
            }
        }
        coverCache = (key, image);
        return image;
    }

    /// <summary>Nome leggibile dell'app: "Spotify.exe" → "Spotify", ID dello Store → nome dell'app installata.</summary>
    static string AppName(string id)
    {
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        if (id.Contains('!'))
        {
            var app = ResolveAppId?.Invoke(id);
            if (app != null) return app;
            id = id[..id.IndexOf('!')];
            if (id.Contains('_')) id = id[..id.IndexOf('_')];
            if (id.Contains('.')) id = id[(id.LastIndexOf('.') + 1)..];
        }
        return id.Length > 0 ? char.ToUpper(id[0]) + id[1..] : id;
    }
}
