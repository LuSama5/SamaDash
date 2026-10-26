using System.IO;
using System.Text.Json;

namespace SamaDash.Services;

/// <summary>File JSON in %LOCALAPPDATA%\SamaDash.</summary>
public static class Storage
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SamaDash");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string PathOf(string name) => Path.Combine(Dir, name);

    public static T? Load<T>(string name)
    {
        try
        {
            var path = PathOf(name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : default;
        }
        catch
        {
            return default;
        }
    }

    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Dir);
        var path = PathOf(name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }
}

/// <summary>Dove si trova la mini barra. Free = lontana dai bordi.</summary>
public enum BarEdge { Top, Bottom, Left, Right, Free }

public class Settings
{
    public bool FirstRunDone { get; set; }
    public bool MiniBarEnabled { get; set; } = true;
    /// <summary>Scorciatoia per aprire la dashboard (es. "Alt+Space"); vuota = disattivata.</summary>
    public string Hotkey { get; set; } = "Alt+Space";
    /// <summary>Centro della mini barra in pixel fisici; null = in alto al centro dello schermo principale.</summary>
    public int? MiniBarX { get; set; }
    public int? MiniBarY { get; set; }
    /// <summary>Bordo a cui è agganciata la mini barra: decide orientamento e verso di apertura della dashboard.</summary>
    public BarEdge MiniBarEdge { get; set; } = BarEdge.Top;
    /// <summary>Mini barra a pillola: si espande solo al passaggio del mouse.</summary>
    public bool MiniBarCompact { get; set; }
    /// <summary>App fissate sulla taskbar già importate negli shortcut (non vengono riaggiunte se le rimuovi).</summary>
    public List<string> ImportedPins { get; set; } = [];
    /// <summary>Oscuramento software (0-100) per gli schermi senza DDC/CI, per percorso del dispositivo.</summary>
    public Dictionary<string, int> SoftwareBrightness { get; set; } = [];

    public static Settings Current { get; } = Storage.Load<Settings>("settings.json") ?? new Settings();

    public void Save() => Storage.Save("settings.json", this);
}
