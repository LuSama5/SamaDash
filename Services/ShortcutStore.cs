using System.IO;

namespace SamaDash.Services;

public class Shortcut
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>Shortcut personalizzati in %LOCALAPPDATA%\SamaDash\shortcuts.json.</summary>
public class ShortcutStore
{
    public const string FileName = "shortcuts.json";

    public List<Shortcut> Items { get; private set; } = [];

    public void Load()
    {
        var items = Storage.Load<List<Shortcut>>(FileName);
        if (items == null)
        {
            items = Defaults();
            Storage.Save(FileName, items);
        }
        Items = items;
    }

    public void Add(string path)
    {
        if (Items.Any(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        Items.Add(new Shortcut { Name = NameFor(path), Path = path });
        Storage.Save(FileName, Items);
    }

    public void Remove(string path)
    {
        Items.RemoveAll(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        Storage.Save(FileName, Items);
    }

    public static string NameFor(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http"))
            return uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
        if (Directory.Exists(path))
            return new DirectoryInfo(path).Name is { Length: > 0 } name ? name : path;
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    static List<Shortcut> Defaults()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var candidates = new (string Name, string? Path)[]
        {
            ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Download", System.IO.Path.Combine(home, "Downloads")),
            ("Documenti", documents),
            ("OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer") ?? System.IO.Path.Combine(home, "OneDrive")),
            ("OneDrive Polimi", Environment.GetEnvironmentVariable("OneDriveCommercial")),
            ("GitHub", System.IO.Path.Combine(documents, "GitHub")),
        };
        return candidates
            .Where(c => !string.IsNullOrEmpty(c.Path) && Directory.Exists(c.Path))
            .Select(c => new Shortcut { Name = c.Name, Path = c.Path! })
            .ToList();
    }
}
