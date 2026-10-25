using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SamaDash.Services;

public static class Shell
{
    public static void Open(string path)
    {
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            Process.Start("explorer.exe", path);
        else
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void Reveal(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    [ThreadStatic] static dynamic? wsh;

    /// <summary>Destinazione di un collegamento .lnk, o null.</summary>
    public static string? ResolveLnk(string lnk)
    {
        try
        {
            wsh ??= Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            var shortcut = wsh!.CreateShortcut(lnk);
            string target = shortcut.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Ultimi file aperti in Windows (cartella Recent).</summary>
    public static List<string> RecentFiles(int count)
    {
        var result = new List<string>();
        try
        {
            var dir = new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.Recent));
            foreach (var lnk in dir.EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(40))
            {
                var target = ResolveLnk(lnk.FullName);
                if (target == null || !File.Exists(target) || result.Contains(target, StringComparer.OrdinalIgnoreCase))
                    continue;
                result.Add(target);
                if (result.Count == count) break;
            }
        }
        catch { }
        return result;
    }
}

public static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "SamaDash";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(Name, false);
    }
}
