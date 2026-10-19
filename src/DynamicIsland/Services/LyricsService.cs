using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

internal sealed record LyricLine(TimeSpan Time, string Text);

internal sealed class Lyrics(IReadOnlyList<LyricLine> lines, bool isSynced, bool isInstrumental)
{
    public static readonly Lyrics Instrumental = new([], isSynced: false, isInstrumental: true);

    public IReadOnlyList<LyricLine> Lines { get; } = lines;

    /// <summary>False when the database only had plain text; the times are then spread over the song.</summary>
    public bool IsSynced { get; } = isSynced;

    public bool IsInstrumental { get; } = isInstrumental;

    /// <summary>Index of the line being sung at <paramref name="position"/>, or -1 before the first line.</summary>
    public int IndexAt(TimeSpan position)
    {
        int low = 0, high = Lines.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (Lines[mid].Time <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return found;
    }
}

/// <summary>
/// Looks up lyrics on LRCLIB (lrclib.net), a free community database with time-synced lyrics.
/// Spotify has no public lyrics API, so the track is matched by title, artist, album and length —
/// which also makes it work for YouTube Music, Apple Music and other players.
/// </summary>
internal sealed partial class LyricsService
{
    private const int CacheSize = 40;

    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // One lookup per track; null = known to have no lyrics.
    private readonly Dictionary<string, Lyrics?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<Lyrics?> GetAsync(string title, string artist, string album, TimeSpan duration, CancellationToken cancel)
    {
        string key = $"{artist}\u0001{title}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var lyrics = await FindAsync(title, artist, album, duration, cancel);
        if (_cache.Count >= CacheSize) _cache.Clear();
        _cache[key] = lyrics;
        return lyrics;
    }

    private static async Task<Lyrics?> FindAsync(string title, string artist, string album, TimeSpan duration, CancellationToken cancel)
    {
        // 1. Exact signature match (fast, most accurate) when everything is known.
        if (album.Length > 0 && duration.TotalSeconds >= 1)
        {
            string query = Query(("track_name", title), ("artist_name", artist), ("album_name", album),
                ("duration", Math.Round(duration.TotalSeconds).ToString(CultureInfo.InvariantCulture)));
            if (await GetTrackAsync("api/get?" + query, cancel) is { } exact && ToLyrics(exact, duration) is { } found)
                return found;
        }

        // 2. Search, first as-is, then without " - Remastered 2011", "(feat. ...)" and the like.
        string mainArtist = artist.Split([", ", " & ", " feat. ", "; "], StringSplitOptions.None)[0];
        foreach (string name in new[] { title, CleanTitle(title) }.Distinct())
        {
            var results = await SearchAsync(Query(("track_name", name), ("artist_name", mainArtist)), cancel);
            if (Pick(results, duration) is { } match && ToLyrics(match, duration) is { } found)
                return found;
        }
        return null;
    }

    private static LrcLibTrack? Pick(IReadOnlyList<LrcLibTrack> results, TimeSpan duration)
    {
        // Prefer synced lyrics, then a length that matches the song being played.
        return results
            .Where(r => r.Instrumental || !string.IsNullOrWhiteSpace(r.SyncedLyrics) || !string.IsNullOrWhiteSpace(r.PlainLyrics))
            .OrderBy(r => string.IsNullOrWhiteSpace(r.SyncedLyrics) ? 1 : 0)
            .ThenBy(r => duration.TotalSeconds >= 1 && r.Duration is double d ? Math.Abs(d - duration.TotalSeconds) : 0)
            .FirstOrDefault();
    }

    private static Lyrics? ToLyrics(LrcLibTrack track, TimeSpan duration)
    {
        if (track.Instrumental) return Lyrics.Instrumental;

        if (!string.IsNullOrWhiteSpace(track.SyncedLyrics))
        {
            var lines = ParseLrc(track.SyncedLyrics);
            if (lines.Count > 0) return new Lyrics(lines, isSynced: true, isInstrumental: false);
        }

        if (!string.IsNullOrWhiteSpace(track.PlainLyrics))
        {
            // No timestamps: spread the lines evenly over the song so they still roll along.
            var text = track.PlainLyrics.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            double length = duration.TotalSeconds >= 1 ? duration.TotalSeconds : (track.Duration ?? 0);
            if (text.Count == 0 || length <= 0) return null;
            var lines = text.Select((line, i) => new LyricLine(TimeSpan.FromSeconds(length * i / text.Count), line)).ToList();
            return new Lyrics(lines, isSynced: false, isInstrumental: false);
        }
        return null;
    }

    /// <summary>Parses "[mm:ss.xx] text" lines (several stamps per line allowed); empty lines become "♪".</summary>
    public static List<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (string raw in lrc.Split('\n'))
        {
            var stamps = TimeStamp().Matches(raw);
            if (stamps.Count == 0) continue; // [ar:], [ti:] and other metadata tags have no time

            string text = TimeStamp().Replace(raw, "").Trim();
            foreach (Match stamp in stamps)
            {
                int minutes = int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
                double seconds = double.Parse(stamp.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromSeconds(minutes * 60 + seconds), text.Length > 0 ? text : "♪"));
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    private static string CleanTitle(string title)
    {
        string clean = TitleNoise().Replace(title, "");
        int dash = clean.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) clean = clean[..dash];
        return clean.Trim();
    }

    private static async Task<LrcLibTrack?> GetTrackAsync(string path, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(path, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<LrcLibTrack>(await response.Content.ReadAsStreamAsync(cancel), Json, cancel);
    }

    private static async Task<IReadOnlyList<LrcLibTrack>> SearchAsync(string query, CancellationToken cancel)
    {
        using var response = await Http.GetAsync("api/search?" + query, cancel);
        response.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<List<LrcLibTrack>>(await response.Content.ReadAsStreamAsync(cancel), Json, cancel) ?? [];
    }

    private static string Query(params (string Name, string Value)[] parts) =>
        string.Join("&", parts.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(10) };
        // LRCLIB asks clients to identify themselves.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/1.0 (+https://github.com/sheinafathurr1/dynamic-island)");
        return client;
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex TimeStamp();

    [GeneratedRegex(@"\s*[\(\[](feat\.|ft\.|with |from |remaster|live|radio edit|explicit)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TitleNoise();

    private sealed class LrcLibTrack
    {
        public double? Duration { get; set; }
        public bool Instrumental { get; set; }
        public string? PlainLyrics { get; set; }
        public string? SyncedLyrics { get; set; }
    }
}
