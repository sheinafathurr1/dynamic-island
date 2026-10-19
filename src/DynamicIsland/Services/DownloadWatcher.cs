using System.IO;
using System.Windows.Threading;
using DynamicIsland.Interop;

namespace DynamicIsland.Services;

/// <summary>
/// Notices downloads that just finished in the Downloads folder. Browsers write to a temporary
/// name (.crdownload, .part...) and rename it at the end, so the rename to a normal name is the
/// signal; files that appear directly are checked once they stop growing. Event driven.
/// </summary>
internal sealed class DownloadWatcher : IDisposable
{
    private static readonly string[] PartialExtensions =
        [".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".!ut", ".aria2"];

    private readonly Dispatcher _dispatcher;
    private readonly FileSystemWatcher? _watcher;
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.OrdinalIgnoreCase);

    public DownloadWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Folder = Native.KnownFolder(Native.FOLDERID_Downloads);
        if (Folder is null || !Directory.Exists(Folder)) return;

        _watcher = new FileSystemWatcher(Folder) { NotifyFilter = NotifyFilters.FileName };
        _watcher.Renamed += (_, e) =>
        {
            if (!IsPartial(e.FullPath)) _dispatcher.InvokeAsync(() => Settle(e.FullPath, attempts: 20, lastSize: -1));
        };
        _watcher.Created += (_, e) =>
        {
            if (!IsPartial(e.FullPath)) _dispatcher.InvokeAsync(() => Settle(e.FullPath, attempts: 20, lastSize: -1));
        };
    }

    /// <summary>Raised on the UI thread with the finished file.</summary>
    public event Action<string>? Finished;

    public string? Folder { get; }

    public bool Enabled
    {
        get => _watcher?.EnableRaisingEvents ?? false;
        set
        {
            if (_watcher is not null) _watcher.EnableRaisingEvents = value;
        }
    }

    public void Dispose() => _watcher?.Dispose();

    private static bool IsPartial(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal) ||
               name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
               PartialExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Waits until the file exists, is not empty and has stopped growing, then reports it once.</summary>
    private void Settle(string path, int attempts, long lastSize)
    {
        long size = -1;
        bool locked = false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return; // a placeholder that was replaced or deleted again
            if (info.Attributes.HasFlag(FileAttributes.Hidden) || info.Attributes.HasFlag(FileAttributes.Directory)) return;
            size = info.Length;
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
        }
        catch (IOException)
        {
            locked = true;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        // A browser's empty placeholder next to its ".part" file: the rename later is the real event.
        bool placeholder = size == 0 && PartialExtensions.Any(e => File.Exists(path + e));
        if (placeholder) return;

        if (!locked && size > 0 && size == lastSize)
        {
            var now = DateTime.UtcNow;
            if (_recent.TryGetValue(path, out var seen) && now - seen < TimeSpan.FromSeconds(30)) return;
            foreach (var stale in _recent.Where(r => now - r.Value > TimeSpan.FromMinutes(5)).Select(r => r.Key).ToList()) _recent.Remove(stale);
            _recent[path] = now;
            Finished?.Invoke(path);
            return;
        }

        if (attempts <= 0) return;
        var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        retry.Tick += (_, _) =>
        {
            retry.Stop();
            Settle(path, attempts - 1, size);
        };
        retry.Start();
    }
}
