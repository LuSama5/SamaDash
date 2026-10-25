using System.IO;

namespace SamaDash.Services;

/// <param name="Launch">Percorso da aprire: un .lnk oppure shell:AppsFolder\AUMID.</param>
/// <param name="Target">Exe di destinazione, se noto.</param>
public record AppEntry(string Name, string Launch, string? Target, string? Aumid = null);

/// <summary>App del menu Start (collegamenti .lnk) e app dello Store.</summary>
public class AppIndex
{
    List<AppEntry> apps = [];
    Dictionary<string, AppEntry> byTarget = new(StringComparer.OrdinalIgnoreCase);

    public Task LoadAsync() => Task.Run(() =>
    {
        var list = new List<AppEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        })
        {
            foreach (var lnk in SafeEnumerate(root, "*.lnk"))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (IsJunk(name) || !names.Add(name)) continue;
                list.Add(new AppEntry(name, lnk, Shell.ResolveLnk(lnk)));
            }
        }

        foreach (var app in LoadPackaged())
            if (!IsJunk(app.Name) && names.Add(app.Name))
                list.Add(app);

        var map = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in list)
            if (app.Target != null && app.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                map.TryAdd(app.Target, app);

        apps = list;
        byTarget = map;
    });

    public IEnumerable<AppEntry> Search(string query)
    {
        return apps
            .Select(a => (app: a, score: Score(a.Name, query)))
            .Where(x => x.score >= 0)
            .OrderBy(x => x.score)
            .ThenBy(x => x.app.Name.Length)
            .Select(x => x.app);
    }

    /// <summary>Trova la voce del menu Start per un exe (anche app dello Store).</summary>
    public AppEntry? ForExe(string exe)
    {
        if (byTarget.TryGetValue(exe, out var app)) return app;

        // C:\Program Files\WindowsApps\<Nome>_<Versione>_<Arch>_<Risorsa>_<Editore>\...
        const string marker = @"\WindowsApps\";
        int i = exe.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var folder = exe[(i + marker.Length)..].Split('\\')[0];
        var parts = folder.Split('_');
        if (parts.Length < 2) return null;
        var family = parts[0] + "_" + parts[^1] + "!";
        return apps.FirstOrDefault(a => a.Aumid?.StartsWith(family, StringComparison.OrdinalIgnoreCase) == true);
    }

    static int Score(string name, string query)
    {
        int i = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (i == 0) return 0;
        if (i > 0) return name[i - 1] == ' ' ? 1 : 2;
        return -1;
    }

    static bool IsJunk(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("disinstall", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>App dello Store dalla cartella virtuale shell:AppsFolder (serve un thread STA).</summary>
    static List<AppEntry> LoadPackaged()
    {
        var list = new List<AppEntry>();
        var thread = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic items = shell.NameSpace("shell:AppsFolder").Items();
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    string name = item.Name;
                    string id = item.Path;
                    if (id.Contains('!'))
                        list.Add(new AppEntry(name, @"shell:AppsFolder\" + id, null, id));
                }
            }
            catch { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return list;
    }
}
