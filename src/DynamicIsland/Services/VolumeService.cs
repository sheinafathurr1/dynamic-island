using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Reports master-volume / mute changes of the default playback device (volume keys, the tray
/// flyout, apps...). Purely callback driven through Core Audio, so it costs nothing while idle.
/// </summary>
internal sealed class VolumeService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly VolumeCallback _volumeCallback;
    private readonly DeviceCallback _deviceCallback;
    private IAudioEndpointVolume? _endpoint;
    private float _level = -1;
    private bool _muted;

    public VolumeService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _volumeCallback = new VolumeCallback(this);
        _deviceCallback = new DeviceCallback(this);
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
        BindDefaultDevice();
    }

    /// <summary>Raised on the UI thread with the new level (0..1) and mute state.</summary>
    public event Action<float, bool>? VolumeChanged;

    public void Dispose()
    {
        try
        {
            ReleaseEndpoint();
            _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
            Marshal.ReleaseComObject(_enumerator);
        }
        catch (Exception)
        {
            // Shutting down anyway.
        }
    }

    private void BindDefaultDevice()
    {
        ReleaseEndpoint();
        if (_enumerator.GetDefaultAudioEndpoint(CoreAudio.ERender, CoreAudio.EMultimedia, out var device) != 0 || device is null)
            return; // no speakers / headphones right now

        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CoreAudio.CLSCTX_ALL, 0, out var instance) != 0 || instance is not IAudioEndpointVolume endpoint)
                return;

            endpoint.GetMasterVolumeLevelScalar(out _level);
            endpoint.GetMute(out _muted);
            endpoint.RegisterControlChangeNotify(_volumeCallback);
            _endpoint = endpoint;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void ReleaseEndpoint()
    {
        if (_endpoint is null) return;
        _endpoint.UnregisterControlChangeNotify(_volumeCallback);
        Marshal.ReleaseComObject(_endpoint);
        _endpoint = null;
    }

    // Called on an audio thread.
    private void OnNotify(float level, bool muted) => _dispatcher.InvokeAsync(() =>
    {
        // Balance or per-channel tweaks also notify; only react to what the user can see.
        if (Math.Abs(level - _level) < 0.001f && muted == _muted) return;
        _level = level;
        _muted = muted;
        VolumeChanged?.Invoke(level, muted);
    });

    // Called on an audio thread. Re-registering inside the callback is not allowed, so defer it.
    private void OnDefaultDeviceChanged() => _dispatcher.InvokeAsync(() =>
    {
        try
        {
            BindDefaultDevice();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    });

    [ComVisible(true)]
    private sealed class VolumeCallback(VolumeService owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(nint notificationData)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA { GUID guidEventContext; BOOL bMuted; float fMasterVolume; ... }
            bool muted = Marshal.ReadInt32(notificationData, 16) != 0;
            float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(notificationData, 20));
            owner.OnNotify(level, muted);
            return 0;
        }
    }

    [ComVisible(true)]
    private sealed class DeviceCallback(VolumeService owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            if (flow == CoreAudio.ERender && role == CoreAudio.EMultimedia)
                owner.OnDefaultDeviceChanged();
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
