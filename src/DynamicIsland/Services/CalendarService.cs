using System.IO;
using System.Net.Http;
using System.Windows.Threading;

namespace DynamicIsland.Services;

internal enum CalendarState
{
    /// <summary>No calendar link set.</summary>
    Off,
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// Keeps an iCal feed (a link, or a local .ics file) fresh: loads it when set, then every 15 minutes.
/// The feed itself is tiny and parsing takes milliseconds, so this costs next to nothing.
/// </summary>
internal sealed class CalendarService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private const int MaxFeedBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = RefreshInterval };
    private string _source = "";
    private string? _feed;
    private int _version;
    private DateTime _expandedFor;
    private List<CalendarEvent> _events = [];

    public CalendarService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/1.0 (+https://github.com/sheinafathurr1/dynamic-island)");
        _refreshTimer.Tick += (_, _) => Refresh();
    }

    /// <summary>Raised on the UI thread when the events or the state changed.</summary>
    public event Action? Changed;

    public CalendarState State { get; private set; }

    /// <summary>Sets the feed (https://, webcal:// or a file path; empty = off) and loads it.</summary>
    public void SetSource(string? source)
    {
        _source = source?.Trim() ?? "";
        _feed = null;
        _events = [];
        _expandedFor = default;
        _refreshTimer.Stop();
        if (_source.Length == 0)
        {
            State = CalendarState.Off;
            Changed?.Invoke();
            return;
        }

        State = CalendarState.Loading;
        Changed?.Invoke();
        Refresh();
        _refreshTimer.Start();
    }

    /// <summary>Occurrences from a week before <paramref name="today"/> to two weeks after, in local time.</summary>
    public IReadOnlyList<CalendarEvent> EventsAround(DateTime today)
    {
        if (_feed is null) return [];
        if (_expandedFor != today)
        {
            _expandedFor = today;
            try
            {
                _events = IcsCalendar.Expand(_feed, today.AddDays(-7), today.AddDays(15));
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                _events = [];
            }
        }
        return _events;
    }

    public async void Refresh()
    {
        if (_source.Length == 0) return;
        int version = ++_version;
        string source = _source;
        try
        {
            string feed = await LoadAsync(source);
            if (version != _version) return; // the link changed meanwhile
            _feed = feed;
            _expandedFor = default;
            State = CalendarState.Ready;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException or InvalidDataException or UriFormatException)
        {
            if (version != _version) return;
            // Offline for a moment: keep showing what we had. Only a feed that never loaded is "failed".
            if (_feed is null) State = CalendarState.Failed;
        }
        Changed?.Invoke();
    }

    /// <summary>True for links and paths <see cref="SetSource"/> can load.</summary>
    public static bool IsValidSource(string source)
    {
        source = source.Trim();
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "webcal" or "webcals")
            return true;
        return Path.IsPathFullyQualified(source) && File.Exists(source);
    }

    private async Task<string> LoadAsync(string source)
    {
        string text;
        if (Path.IsPathFullyQualified(source) && !source.Contains("://", StringComparison.Ordinal))
        {
            text = await File.ReadAllTextAsync(source);
        }
        else
        {
            // webcal:// is just https:// with a "subscribe" hint for calendar apps.
            var uri = new UriBuilder(source);
            if (uri.Scheme is "webcal" or "webcals") uri.Scheme = "https";
            if (uri.Scheme == "https") uri.Port = -1;

            using var response = await _http.GetAsync(uri.Uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxFeedBytes) throw new InvalidDataException("Calendar feed is too large.");
            text = await response.Content.ReadAsStringAsync();
        }

        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Not an iCalendar feed.");
        return text;
    }
}
