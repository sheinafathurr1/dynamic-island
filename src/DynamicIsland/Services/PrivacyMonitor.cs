using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using DynamicIsland.Interop;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>
/// Knows which app is using the microphone or camera, like the orange / green dots on iOS.
/// Windows records every capture session under the CapabilityAccessManager consent store
/// (LastUsedTimeStop is 0 while it is running); we get a registry change notification instead of
/// polling.
/// </summary>
internal sealed class PrivacyMonitor : IDisposable
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly RegistryWatch? _microphone;
    private readonly RegistryWatch? _camera;
    private readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);

    public PrivacyMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Rescan();
        };

        _microphone = RegistryWatch.TryCreate(ConsentStore + "microphone", OnRegistryChanged);
        _camera = RegistryWatch.TryCreate(ConsentStore + "webcam", OnRegistryChanged);
        Microphone = FindActiveApp(_microphone);
        CameraApp = FindActiveApp(_camera)?.Name;
    }

    /// <summary>Raised on the UI thread when an app starts or stops using the mic or camera.</summary>
    public event Action? Changed;

    /// <summary>Friendly name of the app using the microphone, or null when it is not in use.</summary>
    public string? MicrophoneApp => Microphone?.Name;

    /// <summary>The app using the microphone (name, how to find it, since when), or null.</summary>
    public CaptureApp? Microphone { get; private set; }

    /// <summary>Friendly name of the app using the camera, or null when it is not in use.</summary>
    public string? CameraApp { get; private set; }

    public void Dispose()
    {
        _debounce.Stop();
        _microphone?.Dispose();
        _camera?.Dispose();
    }

    // Thread pool. Apps often write several values in a row, so coalesce them.
    private void OnRegistryChanged() => _dispatcher.InvokeAsync(() =>
    {
        _debounce.Stop();
        _debounce.Start();
    });

    private void Rescan()
    {
        // Re-arm first so a change that lands while scanning is not missed.
        _microphone?.Arm();
        _camera?.Arm();

        var microphone = FindActiveApp(_microphone);
        var camera = FindActiveApp(_camera)?.Name;
        if (microphone?.Key == Microphone?.Key && camera == CameraApp) return;

        Microphone = microphone;
        CameraApp = camera;
        Changed?.Invoke();
    }

    private CaptureApp? FindActiveApp(RegistryWatch? watch)
    {
        if (watch is null) return null;
        try
        {
            foreach (var name in watch.Key.GetSubKeyNames())
            {
                using var app = watch.Key.OpenSubKey(name);
                if (app is null) continue;

                if (name == "NonPackaged")
                {
                    // Classic desktop apps, keyed by their path with '\' replaced by '#'.
                    foreach (var exe in app.GetSubKeyNames())
                    {
                        using var entry = app.OpenSubKey(exe);
                        if (IsInUse(entry)) return new CaptureApp(DesktopAppName(exe), exe, StartedAt(entry!));
                    }
                }
                else if (IsInUse(app))
                {
                    return new CaptureApp(PackagedAppName(name), name, StartedAt(app));
                }
            }
        }
        catch (Exception)
        {
            // Keys can vanish while we enumerate; the next notification rescans anyway.
        }
        return null;
    }

    private static DateTime StartedAt(RegistryKey key) =>
        key.GetValue("LastUsedTimeStart") is long start && start > 0 ? DateTime.FromFileTimeUtc(start) : DateTime.UtcNow;

    private static bool IsInUse(RegistryKey? key) =>
        key?.GetValue("LastUsedTimeStop") is long stop && stop == 0 &&
        key.GetValue("LastUsedTimeStart") is long start && start > 0;

    private string DesktopAppName(string keyName)
    {
        if (_nameCache.TryGetValue(keyName, out var cached)) return cached;

        string path = keyName.Replace('#', '\\');
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            // "Zoom.exe" → "Zoom Workplace", "chrome.exe" → "Google Chrome".
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
                name = description.Trim();
        }
        catch (Exception)
        {
            // Fall back to the file name.
        }

        _nameCache[keyName] = name;
        return name;
    }

    /// <summary>"Microsoft.WindowsCamera_8wekyb3d8bbwe" → "WindowsCamera".</summary>
    private static string PackagedAppName(string packageFamilyName)
    {
        string name = packageFamilyName;
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    private sealed class RegistryWatch : IDisposable
    {
        private readonly AutoResetEvent _signal = new(false);
        private readonly RegisteredWaitHandle _wait;

        private RegistryWatch(RegistryKey key, Action changed)
        {
            Key = key;
            Arm();
            _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => changed(), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        public RegistryKey Key { get; }

        public static RegistryWatch? TryCreate(string path, Action changed)
        {
            var key = Registry.CurrentUser.OpenSubKey(path);
            return key is null ? null : new RegistryWatch(key, changed);
        }

        /// <summary>Registry notifications fire once; this asks for the next one.</summary>
        public void Arm() => Native.RegNotifyChangeKeyValue(
            Key.Handle,
            watchSubtree: true,
            Native.REG_NOTIFY_CHANGE_NAME | Native.REG_NOTIFY_CHANGE_LAST_SET | Native.REG_NOTIFY_THREAD_AGNOSTIC,
            _signal.SafeWaitHandle,
            asynchronous: true);

        public void Dispose()
        {
            _wait.Unregister(null);
            Key.Dispose();
            _signal.Dispose();
        }
    }
}

/// <summary>
/// An app recording from the microphone or camera. <paramref name="Key"/> is its exe path with '#'
/// for '\' (desktop apps) or its package family name (Store apps).
/// </summary>
internal sealed record CaptureApp(string Name, string Key, DateTime StartedUtc)
{
    public bool IsDesktop => Key.Contains('#');

    public string? ExePath => IsDesktop ? Key.Replace('#', '\\') : null;
}
