using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SamaDash.Services;

/// <summary>Icone di Explorer per file, cartelle, collegamenti e app dello Store.</summary>
public static class IconCache
{
    const uint SHGFI_ICON = 0x100;
    const uint SHGFI_LARGEICON = 0x0;
    const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint SHGFI_PIDL = 0x8;
    const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> UniqueIconExts = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".lnk", ".ico", ".appref-ms", "" };

    public static ImageSource? Get(string path)
    {
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return Generic("x.url", ".url");
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return FromParsingName(path);

        bool isDir = Directory.Exists(path);
        string ext = isDir ? "" : Path.GetExtension(path);
        if (!isDir && !UniqueIconExts.Contains(ext))
            return Generic("x" + ext, ext);

        if (Cache.TryGetValue(path, out var cached)) return cached;
        var info = new Native.SHFILEINFO();
        Native.SHGetFileInfo(path, isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
            ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_LARGEICON);
        return Cache[path] = ToImage(info.hIcon);
    }

    static ImageSource? Generic(string fakeName, string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var info = new Native.SHFILEINFO();
        Native.SHGetFileInfo(fakeName, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info),
            SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        return Cache[key] = ToImage(info.hIcon);
    }

    static ImageSource? FromParsingName(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        ImageSource? image = null;
        if (Native.SHParseDisplayName(name, IntPtr.Zero, out var pidl, 0, out _) == 0)
        {
            var info = new Native.SHFILEINFO();
            Native.SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_PIDL);
            image = ToImage(info.hIcon);
            Marshal.FreeCoTaskMem(pidl);
        }
        return Cache[name] = image;
    }

    static ImageSource? ToImage(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            Native.DestroyIcon(hIcon);
        }
    }
}
