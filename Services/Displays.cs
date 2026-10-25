using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace SamaDash.Services;

public enum BrightnessKind { Ddc, Wmi, Software }

/// <summary>Uno schermo collegato, con le capacità rilevate al momento della lettura.</summary>
public sealed class DisplayInfo
{
    public string GdiName { get; init; } = "";      // \\.\DISPLAY1
    public string DevicePath { get; init; } = "";   // stabile tra riavvii: chiave per le impostazioni
    public string Name { get; init; } = "";
    public bool Primary { get; init; }
    public bool Internal { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Frequency { get; set; }
    public List<int> Rates { get; init; } = [];
    public bool HdrSupported { get; init; }
    public bool HdrOn { get; set; }
    public BrightnessKind Kind { get; init; }
    public int Brightness { get; set; } = 100;
    /// <summary>Contrasto DDC/CI (0-100) o null se non disponibile.</summary>
    public int? Contrast { get; set; }
    internal Native.RECT Bounds { get; init; }

    internal Displays.LUID Adapter;
    internal uint TargetId;
    internal IntPtr Physical; // handle DDC/CI

    readonly object gate = new();
    int pendingBrightness = -1, pendingContrast = -1;
    bool writing;

    /// <summary>Imposta la luminosità. I comandi DDC sono lenti: li mando in background e tengo solo l'ultimo valore.</summary>
    public void SetBrightness(int value)
    {
        Brightness = value;
        if (Kind == BrightnessKind.Software)
        {
            Dimmer.Set(this, value);
            return;
        }
        lock (gate) pendingBrightness = value;
        Pump();
    }

    public void SetContrast(int value)
    {
        Contrast = value;
        lock (gate) pendingContrast = value;
        Pump();
    }

    void Pump()
    {
        lock (gate)
        {
            if (writing) return;
            writing = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                int b, c;
                lock (gate)
                {
                    (b, c) = (pendingBrightness, pendingContrast);
                    pendingBrightness = pendingContrast = -1;
                    if (b < 0 && c < 0)
                    {
                        writing = false;
                        return;
                    }
                }
                try
                {
                    if (b >= 0 && Kind == BrightnessKind.Ddc) Displays.SetVcp(Physical, Displays.VcpBrightness, b);
                    if (b >= 0 && Kind == BrightnessKind.Wmi) Displays.SetWmiBrightness(b);
                    if (c >= 0) Displays.SetVcp(Physical, Displays.VcpContrast, c);
                }
                catch { }
            }
        });
    }

    public string Detail => $"{Width} × {Height} · {Frequency} Hz";
}

/// <summary>Rileva gli schermi e ne controlla luminosità, contrasto, HDR, frequenza e modalità di proiezione.</summary>
public static class Displays
{
    internal const byte VcpBrightness = 0x10, VcpContrast = 0x12;

    static readonly List<IntPtr> openHandles = [];

    /// <summary>Legge gli schermi attivi. Lento (DDC/CI): chiamarlo fuori dal thread della UI.</summary>
    public static List<DisplayInfo> Enumerate()
    {
        // una lettura alla volta: chiude gli handle DDC della lettura precedente
        lock (enumGate) return EnumerateCore();
    }

    static readonly object enumGate = new();

    static List<DisplayInfo> EnumerateCore()
    {
        lock (openHandles)
        {
            foreach (var h in openHandles) DestroyPhysicalMonitor(h);
            openHandles.Clear();
        }

        var targets = ActiveTargets();
        var result = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMon, ref mi)) return true;
            targets.TryGetValue(mi.szDevice, out var target);

            // DDC/CI: il primo monitor fisico collegato a questo HMONITOR
            // gli handle dei monitor fisici partono da 0: serve un flag a parte
            IntPtr physical = IntPtr.Zero;
            bool hasPhysical = false;
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) && count > 0)
            {
                var arr = new PHYSICAL_MONITOR[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                {
                    physical = arr[0].hPhysicalMonitor;
                    hasPhysical = true;
                    lock (openHandles) openHandles.AddRange(arr.Select(a => a.hPhysicalMonitor));
                }
            }

            var kind = BrightnessKind.Software;
            int brightness = 100;
            int? contrast = null;
            if (target.Internal && GetWmiBrightness() is int wmi)
            {
                kind = BrightnessKind.Wmi;
                brightness = wmi;
            }
            else if (hasPhysical && GetVcp(physical, VcpBrightness) is int ddc)
            {
                kind = BrightnessKind.Ddc;
                brightness = ddc;
                contrast = GetVcp(physical, VcpContrast);
            }
            else
            {
                brightness = Settings.Current.SoftwareBrightness.GetValueOrDefault(target.DevicePath ?? mi.szDevice, 100);
            }

            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            EnumDisplaySettings(mi.szDevice, ENUM_CURRENT_SETTINGS, ref mode);

            result.Add(new DisplayInfo
            {
                GdiName = mi.szDevice,
                DevicePath = target.DevicePath ?? mi.szDevice,
                Name = string.IsNullOrWhiteSpace(target.Name)
                    ? (target.Internal ? "Schermo integrato" : $"Schermo {result.Count + 1}")
                    : target.Name,
                Primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                Internal = target.Internal,
                Width = mode.dmPelsWidth,
                Height = mode.dmPelsHeight,
                Frequency = mode.dmDisplayFrequency,
                Rates = RefreshRates(mi.szDevice, mode),
                HdrSupported = target.HdrSupported,
                HdrOn = target.HdrOn,
                Kind = kind,
                Brightness = brightness,
                Contrast = contrast,
                Bounds = mi.rcMonitor,
                Adapter = target.Adapter,
                TargetId = target.Id,
                Physical = physical,
            });
            return true;
        }, IntPtr.Zero);

        return result.OrderByDescending(d => d.Primary).ThenBy(d => d.Bounds.Left).ToList();
    }

    // ---- Frequenza di aggiornamento

    static List<int> RefreshRates(string device, DEVMODE current)
    {
        var rates = new SortedSet<int>();
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettings(device, i, ref mode); i++)
            if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight &&
                mode.dmBitsPerPel == current.dmBitsPerPel && mode.dmDisplayFrequency > 1)
                rates.Add(mode.dmDisplayFrequency);
        return [.. rates];
    }

    public static bool SetRefreshRate(DisplayInfo display, int hz)
    {
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(display.GdiName, ENUM_CURRENT_SETTINGS, ref mode)) return false;
        mode.dmDisplayFrequency = hz;
        mode.dmFields = DM_DISPLAYFREQUENCY;
        var ok = ChangeDisplaySettingsEx(display.GdiName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero) == 0;
        if (ok) display.Frequency = hz;
        return ok;
    }

    // ---- HDR

    public static bool SetHdr(DisplayInfo display, bool on)
    {
        var state = new SET_ADVANCED_COLOR_STATE
        {
            header = new DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE,
                size = (uint)Marshal.SizeOf<SET_ADVANCED_COLOR_STATE>(),
                adapterId = display.Adapter,
                id = display.TargetId,
            },
            value = on ? 1u : 0u,
        };
        var ok = DisplayConfigSetDeviceInfo(ref state) == 0;
        if (ok) display.HdrOn = on;
        return ok;
    }

    // ---- Modalità di proiezione (Win+P)

    public enum Projection { Internal = 1, Clone = 2, Extend = 4, External = 8 }

    /// <summary>Modalità attuale, o null se non è possibile leggerla.</summary>
    public static Projection? CurrentProjection()
    {
        if (GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out uint np, out uint nm) != 0) return null;
        var paths = new PATH_INFO[np];
        var modes = new MODE_INFO[nm];
        if (QueryDisplayConfig(QDC_DATABASE_CURRENT, ref np, paths, ref nm, modes, out uint topology) != 0) return null;
        return Enum.IsDefined(typeof(Projection), (int)topology) ? (Projection)topology : null;
    }

    public static void SetProjection(Projection mode)
    {
        var arg = mode switch
        {
            Projection.Internal => "/internal",
            Projection.Clone => "/clone",
            Projection.External => "/external",
            _ => "/extend",
        };
        Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe"), arg)
            { UseShellExecute = false, CreateNoWindow = true });
    }

    // ---- DDC/CI

    internal static int? GetVcp(IntPtr physical, byte code)
    {
        if (!GetVCPFeatureAndVCPFeatureReply(physical, code, IntPtr.Zero, out uint cur, out uint max) || max == 0) return null;
        return (int)Math.Round(cur * 100.0 / max);
    }

    internal static void SetVcp(IntPtr physical, byte code, int percent)
    {
        if (!GetVCPFeatureAndVCPFeatureReply(physical, code, IntPtr.Zero, out _, out uint max) || max == 0) return;
        SetVCPFeature(physical, code, (uint)Math.Round(percent * max / 100.0));
    }

    // ---- Schermi integrati (portatili): WMI

    internal static int? GetWmiBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var obj in searcher.Get())
                return Convert.ToInt32(obj["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    internal static void SetWmiBrightness(int percent)
    {
        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
        foreach (ManagementObject obj in searcher.Get())
            obj.InvokeMethod("WmiSetBrightness", [1u, (byte)percent]);
    }

    // ---- DisplayConfig: nome, percorso, HDR e tipo di collegamento di ogni schermo attivo

    record struct Target(LUID Adapter, uint Id, string? Name, string? DevicePath, bool Internal, bool HdrSupported, bool HdrOn);

    static Dictionary<string, Target> ActiveTargets()
    {
        var map = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return map;
        var paths = new PATH_INFO[np];
        var modes = new MODE_INFO[nm];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return map;

        foreach (var path in paths.Take((int)np))
        {
            var source = new SOURCE_DEVICE_NAME();
            source.header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, Marshal.SizeOf(source), path.sourceInfo.adapterId, path.sourceInfo.id);
            if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;

            var name = new TARGET_DEVICE_NAME();
            name.header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, Marshal.SizeOf(name), path.targetInfo.adapterId, path.targetInfo.id);
            var hasName = DisplayConfigGetDeviceInfo(ref name) == 0;

            var color = new GET_ADVANCED_COLOR_INFO();
            color.header = Header(DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, Marshal.SizeOf(color), path.targetInfo.adapterId, path.targetInfo.id);
            var hasColor = DisplayConfigGetDeviceInfo(ref color) == 0;

            var tech = path.targetInfo.outputTechnology;
            map.TryAdd(source.viewGdiDeviceName, new Target(
                path.targetInfo.adapterId, path.targetInfo.id,
                hasName ? name.monitorFriendlyDeviceName : null,
                hasName && name.monitorDevicePath.Length > 0 ? name.monitorDevicePath : null,
                tech is OUTPUT_INTERNAL or OUTPUT_LVDS or OUTPUT_DISPLAYPORT_EMBEDDED or OUTPUT_UDI_EMBEDDED,
                hasColor && (color.value & 1) != 0,
                hasColor && (color.value & 2) != 0));
        }
        return map;
    }

    static DEVICE_INFO_HEADER Header(int type, int size, LUID adapter, uint id) =>
        new() { type = type, size = (uint)size, adapterId = adapter, id = id };

    // ---- P/Invoke

    const int ENUM_CURRENT_SETTINGS = -1, DM_DISPLAYFREQUENCY = 0x400000, CDS_UPDATEREGISTRY = 1, MONITORINFOF_PRIMARY = 1;
    const uint QDC_ONLY_ACTIVE_PATHS = 2, QDC_DATABASE_CURRENT = 4;
    const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2,
        DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9, DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;
    const uint OUTPUT_LVDS = 6, OUTPUT_DISPLAYPORT_EMBEDDED = 11, OUTPUT_UDI_EMBEDDED = 13, OUTPUT_INTERNAL = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public Native.RECT rcMonitor, rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }
    [StructLayout(LayoutKind.Sequential)] struct RATIONAL { public uint numerator, denominator; }

    [StructLayout(LayoutKind.Sequential)]
    struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)] struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    struct MODE_INFO
    {
        public uint infoType, id;
        public LUID adapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] data;
    }

    [StructLayout(LayoutKind.Sequential)] struct DEVICE_INFO_HEADER { public int type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GET_ADVANCED_COLOR_INFO { public DEVICE_INFO_HEADER header; public uint value, colorEncoding, bitsPerColorChannel; }

    [StructLayout(LayoutKind.Sequential)]
    struct SET_ADVANCED_COLOR_STATE { public DEVICE_INFO_HEADER header; public uint value; }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr param);

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes,
        [Out] MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes,
        [Out] MODE_INFO[] modes, out uint topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref GET_ADVANCED_COLOR_INFO info);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref SET_ADVANCED_COLOR_STATE info);

    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);
    [DllImport("dxva2.dll")]
    static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);
}
