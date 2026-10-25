using System.Diagnostics;
using System.IO;
using System.Text;

namespace SamaDash.Services;

/// <summary>Ricerca file tramite es.exe (Everything CLI).</summary>
public static class EverythingSearch
{
    // Cartelle che riempiono i risultati di file inutili
    static readonly string[] Excludes =
    [
        $@"!{Environment.GetFolderPath(Environment.SpecialFolder.Windows)}\",
        $@"!{Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)}\",
        @"!\AppData\", @"!\$Recycle.Bin\", @"!\node_modules\", @"!\.git\", @"!\.cache\",
    ];

    static readonly string? EsPath = FindEs();
    static readonly string? EverythingExe = FindEverything();

    public static async Task<(List<string> Paths, string? Error)> SearchAsync(
        string query, string filter, int max, CancellationToken token)
    {
        if (EsPath == null) return ([], "es.exe non trovato: installa voidtools.Everything.Cli");

        var result = await RunAsync(query, filter, max, token);
        if (result.ExitCode == 8 && EverythingExe != null)
        {
            // Everything non è aperto: lo avvio in background e riprovo
            Process.Start(EverythingExe, "-startup");
            await Task.Delay(1500, token);
            result = await RunAsync(query, filter, max, token);
        }

        if (result.ExitCode == 8) return ([], "Everything non è in esecuzione");
        if (result.ExitCode != 0) return ([], $"Errore di ricerca ({result.ExitCode})");

        var paths = result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            // a parità di data, prima i nomi che iniziano con la ricerca
            .OrderBy(p => Path.GetFileName(p).StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        return (paths, null);
    }

    static async Task<(int ExitCode, string Output)> RunAsync(string query, string filter, int max, CancellationToken token)
    {
        var psi = new ProcessStartInfo(EsPath!)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-utf8-bom");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(max.ToString());
        psi.ArgumentList.Add("-sort");
        psi.ArgumentList.Add("date-modified-descending");
        foreach (var exclude in Excludes) psi.ArgumentList.Add(exclude);
        if (filter != "") psi.ArgumentList.Add(filter);
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(word);

        using var process = Process.Start(psi)!;
        using var _ = token.Register(() => { try { process.Kill(); } catch { } });
        var output = await process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return (process.ExitCode, output.TrimStart('\uFEFF'));
    }

    static string? FindEs()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "es.exe"))
            .Append(Path.Combine(local, @"Microsoft\WinGet\Links\es.exe"))
            .Concat(EverythingDirs().Select(dir => Path.Combine(dir, "es.exe")));
        foreach (var path in candidates)
            if (File.Exists(path)) return path;

        var packages = Path.Combine(local, @"Microsoft\WinGet\Packages");
        try
        {
            return Directory.EnumerateDirectories(packages, "voidtools.Everything.Cli*")
                .Select(dir => Path.Combine(dir, "es.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Cartelle in cui di solito si trova Everything (installer, Scoop).</summary>
    static IEnumerable<string> EverythingDirs()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"scoop\apps\everything\current");
    }

    static string? FindEverything()
    {
        // l'installer registra la cartella tra i programmi installati
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Everything");
            if (key?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, "Everything.exe")))
                return Path.Combine(dir, "Everything.exe");
        }
        var dirs = EverythingDirs();
        if (EsPath != null) dirs = dirs.Prepend(Path.GetDirectoryName(EsPath)!);
        return dirs.Select(dir => Path.Combine(dir, "Everything.exe")).FirstOrDefault(File.Exists);
    }
}
