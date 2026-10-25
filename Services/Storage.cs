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

public class Settings
{
    public bool FirstRunDone { get; set; }
    public bool MiniBarEnabled { get; set; } = true;
    /// <summary>Posizione orizzontale della mini barra; null = centrata.</summary>
    public double? MiniBarLeft { get; set; }

    public static Settings Current { get; } = Storage.Load<Settings>("settings.json") ?? new Settings();

    public void Save() => Storage.Save("settings.json", this);
}
