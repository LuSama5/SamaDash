using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace SamaDash.Services;

public record AudioOutput(string Id, string Name, bool IsDefault);

/// <summary>Uscite audio: elenco, cambio dell'uscita predefinita, volume e muto.</summary>
public static class AudioDevices
{
    public static List<AudioOutput> Outputs()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var current = enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID
                : "";
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new AudioOutput(d.ID, d.FriendlyName, d.ID == current))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Imposta l'uscita predefinita per tutti i ruoli (console, multimedia, comunicazioni).</summary>
    public static void SetDefault(string id)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            for (int role = 0; role < 3; role++) config.SetDefaultEndpoint(id, role);
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }

    public static float Volume
    {
        get => WithDefault(d => d.AudioEndpointVolume.MasterVolumeLevelScalar, 0f);
        set => WithDefault(d => d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f), 0f);
    }

    public static bool Muted
    {
        get => WithDefault(d => d.AudioEndpointVolume.Mute, false);
        set => WithDefault(d => d.AudioEndpointVolume.Mute = value, false);
    }

    static T WithDefault<T>(Func<MMDevice, T> action, T fallback)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return fallback;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return action(device);
        }
        catch
        {
            return fallback;
        }
    }

    // Interfaccia non documentata di Windows usata da tutti i programmi di cambio audio.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        // l'ordine conta: i metodi non usati tengono il posto nella vtable
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        void SetEndpointVisibility();
    }
}
