using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using DynamicIsland.Interop;
using Microsoft.Win32;

namespace DynamicIsland.Services;

/// <summary>A notification another app just showed (WhatsApp, Discord, Teams, mail...).</summary>
internal sealed record AppNotification(string AppId, string Title, string Body, string? ImagePath, bool RoundImage);

/// <summary>
/// Notices new toast notifications of other apps. Windows keeps every toast in a small SQLite
/// database in the user's profile; this reads new rows from it (read-only) when the file changes,
/// plus a cheap check every few seconds in case a change notification is missed. Windows' own
/// rules are respected: Do not disturb / Focus, the global switch, and per-app "off" or
/// "notification centre only". Nothing leaves the PC.
/// </summary>
internal sealed class NotificationWatcher : IDisposable
{
    // New rows get a higher rowid ("Order"); "Id" is only a per-app number. The arrival time catches
    // the rare row that reuses the rowid of a newest notification that was just dismissed.
    private const string NewToastsSql =
        """
        SELECT n.rowid, n.Payload, h.PrimaryId, n.ArrivalTime, n.Tag, n."Group"
        FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId
        WHERE n.Type = 'toast' AND (n.rowid > ?1 OR n.ArrivalTime > ?2)
        ORDER BY n.rowid LIMIT 20
        """;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");

    private static readonly string DatabasePath = Path.Combine(Folder, "wpndatabase.db");
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _recent = [];
    private readonly Timer _timer;
    private FileSystemWatcher? _watcher;
    private long _lastRow = -1;
    private long _lastArrival;
    private bool _useSnapshot;
    private int _failures;
    private int _pending;

    public NotificationWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new Timer(_ => Check());
    }

    /// <summary>Raised on the UI thread for each new notification that Windows would show as a banner.</summary>
    public event Action<AppNotification>? Received;

    public static bool IsSupported => File.Exists(DatabasePath);

    public bool IsRunning => _watcher is not null;

    public void Start()
    {
        if (_watcher is not null || !IsSupported) return;
        _lastRow = -1; // re-baseline: only what arrives from now on
        _watcher = new FileSystemWatcher(Folder, "wpndatabase.db*")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) =>
        {
            // Bursts of writes: check once shortly after the first, not after the last.
            if (Interlocked.Exchange(ref _pending, 1) == 0) _timer.Change(Debounce, PollInterval);
        };
        _watcher.EnableRaisingEvents = true;
        _timer.Change(TimeSpan.Zero, PollInterval);
    }

    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }

    // Thread pool; never runs twice at once.
    private void Check()
    {
        Volatile.Write(ref _pending, 0);
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_lastRow < 0)
            {
                Read(db => db.Query("SELECT IFNULL(MAX(rowid), 0), IFNULL(MAX(ArrivalTime), 0) FROM Notification", [], row =>
                {
                    _lastRow = row.Int64(0);
                    _lastArrival = row.Int64(1);
                }));
                return;
            }

            var rows = new List<(long Row, byte[] Payload, string AppId, long Arrival, string Tag, string Group)>();
            Read(db => db.Query(NewToastsSql, [_lastRow, _lastArrival], row =>
                rows.Add((row.Int64(0), row.Blob(1), row.Text(2), row.Int64(3), row.Text(4), row.Text(5)))));

            foreach (var row in rows)
            {
                _lastRow = Math.Max(_lastRow, row.Row);
                _lastArrival = Math.Max(_lastArrival, row.Arrival);
                if (ToNotification(row.Payload, row.AppId, row.Arrival, row.Tag, row.Group) is { } notification)
                    _dispatcher.InvokeAsync(() => Received?.Invoke(notification));
            }
            _failures = 0;
        }
        catch (Exception ex)
        {
            // Log the first failure only; a locked or briefly missing file sorts itself out.
            if (++_failures == 1) Log.Error(ex);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private AppNotification? ToNotification(byte[] payload, string appId, long arrival, string tag, string group)
    {
        var arrived = DateTime.FromFileTimeUtc(Math.Max(0, arrival));
        if (DateTime.UtcNow - arrived > MaxAge) return null; // old rows resurfacing (e.g. after a sync)

        if (ToastPayload.Parse(Decode(payload)) is not { } content) return null;
        if (!WouldShowBanner(appId)) return null;

        // Progress toasts (downloads, copies) update over and over: show the first one only.
        // Chat apps sometimes re-post the same message: show it once.
        string key = content.HasProgress ? $"{appId}\u0001{tag}\u0001{group}" : $"{appId}\u0001{content.Title}\u0001{content.Body}";
        var now = DateTime.UtcNow;
        foreach (var stale in _recent.Where(r => now - r.Value > TimeSpan.FromMinutes(10)).Select(r => r.Key).ToList())
            _recent.Remove(stale);
        if (_recent.TryGetValue(key, out var seen) && now - seen < (content.HasProgress ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30)))
            return null;
        _recent[key] = now;

        return new AppNotification(appId, content.Title, content.Body, ResolveImage(content.ImageSource, appId), content.RoundImage);
    }

    private void Read(Action<SqliteReader> action)
    {
        if (!_useSnapshot)
        {
            try
            {
                using var live = SqliteReader.Open(DatabasePath);
                action(live);
                return;
            }
            catch (SqliteException)
            {
                _useSnapshot = true; // kept locked by the notification service on this PC: read a copy instead
            }
        }

        string folder = Path.Combine(Path.GetTempPath(), "DynamicIsland-notifications");
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, "wpndatabase.db");
        foreach (string suffix in new[] { "", "-wal" })
        {
            string source = DatabasePath + suffix, target = copy + suffix;
            if (!File.Exists(source))
            {
                File.Delete(target);
                continue;
            }
            using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var to = new FileStream(target, FileMode.Create, FileAccess.Write);
            from.CopyTo(to);
        }
        File.Delete(copy + "-shm"); // rebuilt from the copied log

        using var snapshot = SqliteReader.Open(copy);
        action(snapshot);
    }

    private static string Decode(byte[] payload)
    {
        // UTF-8 in practice; tolerate UTF-16 and trailing NULs.
        bool utf16 = payload.Length > 1 && (payload[1] == 0 || (payload[0] == 0xFF && payload[1] == 0xFE));
        return (utf16 ? Encoding.Unicode : Encoding.UTF8).GetString(payload).Trim('\0', '﻿', ' ', '\r', '\n');
    }

    /// <summary>What Windows itself would do: no banners during Do not disturb or for apps set to "off" or "notification centre only".</summary>
    private static bool WouldShowBanner(string appId)
    {
        try
        {
            if (QuietHoursActive()) return false;

            using (var push = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications"))
            {
                if (push?.GetValue("ToastEnabled") is 0) return false;
            }

            using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings");
            if (settings?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") is 0) return false;
            using var app = settings?.OpenSubKey(appId);
            if (app?.GetValue("Enabled") is 0 || app?.GetValue("ShowBanner") is 0) return false;
        }
        catch (Exception)
        {
            // Unreadable settings: show it, like Windows' default.
        }
        return true;
    }

    // Do not disturb / Focus assist: the active "quiet hours" profile, published through WNF (0 = off).
    private const ulong WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED = 0x0D83063EA3BF1C75;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(ref ulong stateName, nint typeId, nint explicitScope, out uint changeStamp, out uint buffer, ref uint bufferSize);

    private static bool QuietHoursActive()
    {
        try
        {
            ulong state = WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED;
            uint size = sizeof(uint);
            return NtQueryWnfStateData(ref state, 0, 0, out _, out uint profile, ref size) == 0 && size >= sizeof(uint) && profile != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Local picture for the toast (contact photo, sender avatar). Packaged apps point into their
    /// own data folder with ms-appdata:///; web images are skipped (nothing is downloaded).
    /// </summary>
    private static string? ResolveImage(string? source, string appId)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            string? path = null;
            if (source.StartsWith("ms-appdata:///", StringComparison.OrdinalIgnoreCase) && appId.Contains('!'))
            {
                string family = appId[..appId.IndexOf('!')];
                string rest = Uri.UnescapeDataString(source["ms-appdata:///".Length..]).Replace('/', '\\');
                int slash = rest.IndexOf('\\');
                if (slash > 0)
                {
                    string state = rest[..slash].ToLowerInvariant() switch
                    {
                        "local" => "LocalState",
                        "roaming" => "RoamingState",
                        "temp" => "TempState",
                        _ => "",
                    };
                    if (state.Length > 0)
                    {
                        path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Packages", family, state, rest[(slash + 1)..]);
                    }
                }
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                path = uri.LocalPath;
            }
            return path is not null && File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
