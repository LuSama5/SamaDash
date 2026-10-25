using System.Runtime.InteropServices;
using System.Text;

namespace SamaDash.Services;

internal static class Native
{
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int processId);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TRANSPARENT = 0x20;

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("shell32.dll")]
    static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string className, string? windowName);

    /// <summary>Nascondi automaticamente la barra delle applicazioni di Windows.</summary>
    public static bool TaskbarAutoHide
    {
        get
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            const uint ABM_GETSTATE = 4;
            return ((ulong)SHAppBarMessage(ABM_GETSTATE, ref data) & 1) != 0;
        }
        set
        {
            const uint ABM_SETSTATE = 10;
            const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;
            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                hWnd = FindWindow("Shell_TrayWnd", null),
                lParam = value ? ABS_AUTOHIDE : ABS_ALWAYSONTOP,
            };
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }
    }

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    /// <summary>Avvisa tutte le finestre che un'impostazione è cambiata (es. "ImmersiveColorSet" per il tema).</summary>
    public static void BroadcastSettingChange(string area)
    {
        const uint WM_SETTINGCHANGE = 0x1A, SMTO_ABORTIFHUNG = 0x2;
        SendMessageTimeout((IntPtr)0xFFFF, WM_SETTINGCHANGE, IntPtr.Zero, area, SMTO_ABORTIFHUNG, 200, out _);
    }

    /// <summary>True con giochi, video o presentazioni a schermo intero.</summary>
    public static bool IsFullscreenAppRunning() =>
        SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4;

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("kernel32.dll")]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll")]
    public static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>Percorso dell'exe della finestra in primo piano, o null.</summary>
    public static string? ForegroundExe()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
