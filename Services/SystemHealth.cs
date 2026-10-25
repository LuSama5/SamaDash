using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SamaDash.Services;

public record DiskStatus(string Name, double UsedFraction, long FreeBytes, long TotalBytes);

/// <param name="Disabled">Motivo per cui non si può pulire adesso, o null.</param>
public record CleanupItem(string Key, string Label, long Bytes, string? Disabled = null);

/// <summary>Stato di disco, RAM e CPU, e pulizia dei file inutili.</summary>
public class SystemHealth
{
    long lastIdle, lastKernel, lastUser;

    /// <summary>Uso della CPU dall'ultima chiamata (0..1).</summary>
    public double CpuUsage()
    {
        if (!Native.GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
        long idleDelta = idle - lastIdle;
        long total = kernel - lastKernel + (user - lastUser); // kernel include già l'idle
        (lastIdle, lastKernel, lastUser) = (idle, kernel, user);
        return total <= 0 ? 0 : Math.Clamp(1 - (double)idleDelta / total, 0, 1);
    }

    public static (double UsedFraction, ulong TotalBytes) Memory()
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref status)) return (0, 0);
        return (status.dwMemoryLoad / 100.0, status.ullTotalPhys);
    }

    public static List<DiskStatus> Disks() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DiskStatus(d.Name.TrimEnd('\\'), 1 - (double)d.TotalFreeSpace / d.TotalSize, d.TotalFreeSpace, d.TotalSize))
            .ToList();

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    // ---------- Pulizia ----------

    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly TimeSpan TempMinAge = TimeSpan.FromHours(24);

    static readonly (string Key, string Label, string Process, string UserData)[] Browsers =
    [
        ("chrome", "Cache di Chrome", "chrome", Path.Combine(Local, @"Google\Chrome\User Data")),
        ("edge", "Cache di Edge", "msedge", Path.Combine(Local, @"Microsoft\Edge\User Data")),
    ];

    static readonly string[] CacheFolders = ["Cache", "Code Cache", "GPUCache"];

    /// <summary>Quanto si può liberare, per voce. Lenta: va chiamata in background.</summary>
    public static List<CleanupItem> Scan()
    {
        var items = new List<CleanupItem>
        {
            new("temp", "File temporanei (più vecchi di 24 ore)", OldTempFiles().Sum(SafeLength)),
            new("bin", "Cestino", RecycleBinSize()),
        };
        foreach (var browser in Browsers)
        {
            var dirs = BrowserCacheDirs(browser.UserData).ToList();
            if (dirs.Count == 0) continue;
            long size = dirs.SelectMany(AllFiles).Sum(SafeLength);
            bool running = Process.GetProcessesByName(browser.Process).Length > 0;
            items.Add(new(browser.Key, browser.Label, size, running ? $"chiudi {(browser.Key == "edge" ? "Edge" : "Chrome")} per pulirla" : null));
        }
        return items;
    }

    /// <summary>Pulisce le voci scelte e restituisce i byte liberati (stimati).</summary>
    public static long Clean(IEnumerable<string> keys, IntPtr hwnd)
    {
        long freed = 0;
        foreach (var key in keys)
        {
            switch (key)
            {
                case "temp":
                    freed += DeleteFiles(OldTempFiles());
                    DeleteEmptyDirs(Path.GetTempPath());
                    break;
                case "bin":
                    freed += RecycleBinSize();
                    // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
                    Native.SHEmptyRecycleBin(hwnd, null, 0x7);
                    break;
                default:
                    var browser = Browsers.FirstOrDefault(b => b.Key == key);
                    if (browser.Key == null || Process.GetProcessesByName(browser.Process).Length > 0) break;
                    freed += DeleteFiles(BrowserCacheDirs(browser.UserData).SelectMany(AllFiles));
                    break;
            }
        }
        return freed;
    }

    static IEnumerable<string> OldTempFiles()
    {
        var cutoff = DateTime.Now - TempMinAge;
        return AllFiles(Path.GetTempPath()).Where(f =>
        {
            try { return File.GetLastWriteTime(f) < cutoff; }
            catch { return false; }
        });
    }

    static IEnumerable<string> BrowserCacheDirs(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        // profili: Default, Profile 1, Profile 2…
        foreach (var profile in SafeDirs(userData).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ")))
            foreach (var cache in CacheFolders)
            {
                var dir = Path.Combine(profile, cache);
                if (Directory.Exists(dir)) yield return dir;
            }
    }

    static long RecycleBinSize()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        return Native.SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    /// <summary>Elimina i file e restituisce i byte liberati; salta quelli in uso.</summary>
    static long DeleteFiles(IEnumerable<string> files)
    {
        long freed = 0;
        foreach (var file in files.ToList())
        {
            long size = SafeLength(file);
            try
            {
                File.Delete(file);
                freed += size;
            }
            catch { }
        }
        return freed;
    }

    static void DeleteEmptyDirs(string root)
    {
        foreach (var dir in SafeDirs(root, recursive: true).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }
    }

    static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    static IEnumerable<string> AllFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", Recursive); }
        catch { return []; }
    }

    static IEnumerable<string> SafeDirs(string dir, bool recursive = false)
    {
        try
        {
            return recursive
                ? Directory.EnumerateDirectories(dir, "*", Recursive).ToList()
                : Directory.EnumerateDirectories(dir).ToList();
        }
        catch { return []; }
    }

    static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }
}
