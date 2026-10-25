using System.Runtime.InteropServices;
using System.Windows.Input;

namespace SamaDash.Services;

/// <summary>Combinazione di tasti, salvata come testo (es. "Ctrl+Alt+Space").</summary>
public record HotKeyCombo(ModifierKeys Modifiers, Key Key)
{
    public static HotKeyCombo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = ModifierKeys.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts[..^1])
        {
            mods |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                "win" => ModifierKeys.Windows,
                _ => ModifierKeys.None,
            };
        }
        return Enum.TryParse<Key>(parts[^1], true, out var key) ? new HotKeyCombo(mods, key) : null;
    }

    /// <summary>Forma salvata nelle impostazioni.</summary>
    public override string ToString() => string.Join("+", Parts(ctrl: "Ctrl", shift: "Shift", keyName: Key.ToString()));

    /// <summary>Forma da mostrare, in italiano (es. "Alt+Spazio").</summary>
    public string Display => string.Join("+", Parts(ctrl: "Ctrl", shift: "Maiusc", keyName: KeyName(Key)));

    IEnumerable<string> Parts(string ctrl, string shift, string keyName)
    {
        if (Modifiers.HasFlag(ModifierKeys.Control)) yield return ctrl;
        if (Modifiers.HasFlag(ModifierKeys.Alt)) yield return "Alt";
        if (Modifiers.HasFlag(ModifierKeys.Shift)) yield return shift;
        if (Modifiers.HasFlag(ModifierKeys.Windows)) yield return "Win";
        yield return keyName;
    }

    static string KeyName(Key key) => key switch
    {
        Key.Space => "Spazio",
        Key.Return => "Invio",
        Key.Back => "Backspace",
        Key.Oem3 => "ò",
        Key.OemPeriod => ".",
        Key.OemComma => ",",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString(),
    };

    /// <summary>Le combinazioni senza modificatori sono ammesse solo per i tasti funzione (F1–F24).</summary>
    public bool IsValid => Modifiers != ModifierKeys.None || Key is >= Key.F1 and <= Key.F24;
}

/// <summary>Scorciatoia globale registrata con RegisterHotKey su una finestra.</summary>
public sealed class GlobalHotKey(IntPtr hwnd)
{
    public const int WM_HOTKEY = 0x0312;
    const int Id = 0x5D01;
    const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    public HotKeyCombo? Current { get; private set; }

    /// <summary>False se la combinazione è già usata da un'altra app.</summary>
    public bool Register(HotKeyCombo combo)
    {
        Unregister();
        // ModifierKeys di WPF ha gli stessi valori di MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(combo.Key);
        if (!RegisterHotKey(hwnd, Id, (uint)combo.Modifiers | MOD_NOREPEAT, vk)) return false;
        Current = combo;
        return true;
    }

    public void Unregister()
    {
        if (Current == null) return;
        UnregisterHotKey(hwnd, Id);
        Current = null;
    }

    public static bool IsOurs(int msg, IntPtr wParam) => msg == WM_HOTKEY && wParam.ToInt32() == Id;
}
