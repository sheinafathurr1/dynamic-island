using System.Runtime.InteropServices;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Mutes the default recording device for every app at once, like a hardware mute switch: Zoom,
/// Meet, Discord... all hear silence (and usually show "muted by your system"). Asked on demand,
/// only while the call pill or the controls page is on screen.
/// </summary>
internal static class MicrophoneMute
{
    private const int ECapture = 1;
    private const int ECommunications = 2;

    /// <summary>Whether the microphone is muted; null when there is no microphone.</summary>
    public static bool? Get() => Use(endpoint => endpoint.GetMute(out bool muted) == 0 ? muted : (bool?)null);

    /// <summary>Flips the mute switch and returns the new state (null when there is no microphone).</summary>
    public static bool? Toggle() => Use(endpoint =>
    {
        if (endpoint.GetMute(out bool muted) != 0) return (bool?)null;
        var context = Guid.Empty;
        return endpoint.SetMute(!muted, ref context) == 0 ? !muted : muted;
    });

    private static bool? Use(Func<IAudioEndpointVolume, bool?> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? instance = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(ECapture, ECommunications, out device) != 0 || device is null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CoreAudio.CLSCTX_ALL, 0, out instance) != 0 || instance is not IAudioEndpointVolume endpoint) return null;
            return action(endpoint);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            foreach (var item in new[] { instance, device, enumerator })
            {
                if (item is not null) Marshal.ReleaseComObject(item);
            }
        }
    }
}
