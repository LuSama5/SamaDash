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

    /// <summary>Cartella dei collegamenti delle app fissate sulla barra delle applicazioni.</summary>
    public static readonly string TaskbarDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    // già presenti tra le azioni di sistema
    static readonly HashSet<string> SkippedPins = new(StringComparer.OrdinalIgnoreCase)
        { "Gestione attività", "Task Manager", "Impostazioni", "Settings" };

    /// <summary>
    /// Aggiunge le app appena fissate sulla taskbar, senza duplicati.
    /// Ogni collegamento viene importato una volta sola: se lo togli dagli shortcut non ritorna.
    /// </summary>
    public bool SyncTaskbar()
    {
        if (!Directory.Exists(TaskbarDir)) return false;
        var imported = Settings.Current.ImportedPins;
        bool added = false, seen = false;

        foreach (var lnk in Directory.EnumerateFiles(TaskbarDir, "*.lnk"))
        {
            if (imported.Contains(lnk, StringComparer.OrdinalIgnoreCase)) continue;
            imported.Add(lnk);
            seen = true;

            var name = NameFor(lnk);
            var target = TargetOf(lnk);
            if (SkippedPins.Contains(name) || target?.EndsWith("taskmgr.exe", StringComparison.OrdinalIgnoreCase) == true)
                continue;
            if (Items.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                               (target != null && target.Equals(TargetOf(s.Path), StringComparison.OrdinalIgnoreCase))))
                continue;

            Items.Add(new Shortcut { Name = name, Path = lnk });
            added = true;
        }

        if (seen) Settings.Current.Save();
        if (added) Storage.Save(FileName, Items);
        return added;
    }

    /// <summary>Exe a cui punta il percorso (risolve i .lnk), o null.</summary>
    public static string? TargetOf(string path) =>
        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Shell.ResolveLnk(path)
        : path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path
        : null;

    /// <summary>True per app (exe, collegamenti, app dello Store); false per file, cartelle e siti.</summary>
    public static bool IsApp(string path)
    {
        if (path.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".appref-ms", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        // .lnk senza destinazione su disco = app dello Store fissata
        var target = Shell.ResolveLnk(path);
        return target == null || target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
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
