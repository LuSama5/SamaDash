using Microsoft.Win32;

namespace SamaDash.Services;

/// <summary>Tema chiaro/scuro e Non disturbare di Windows.</summary>
public static class QuickSettings
{
    const string ThemeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string NotificationsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    const string ToastsValue = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    public static bool DarkMode
    {
        get => ReadDword(ThemeKey, "AppsUseLightTheme", 1) == 0;
        set
        {
            using (var key = Registry.CurrentUser.CreateSubKey(ThemeKey))
            {
                key.SetValue("AppsUseLightTheme", value ? 0 : 1, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", value ? 0 : 1, RegistryValueKind.DWord);
            }
            Native.BroadcastSettingChange("ImmersiveColorSet");
        }
    }

    /// <summary>Spegne i banner delle notifiche (l'interruttore generale "Notifiche" di Windows).</summary>
    public static bool DoNotDisturb
    {
        get => ReadDword(NotificationsKey, ToastsValue, 1) == 0;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(NotificationsKey);
            key.SetValue(ToastsValue, value ? 0 : 1, RegistryValueKind.DWord);
            Native.BroadcastSettingChange("Policy");
        }
    }

    static int ReadDword(string keyPath, string name, int fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(name) is int value ? value : fallback;
    }
}
