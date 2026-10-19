using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

/// <summary>
/// Follows the system "now playing" session (Spotify, browsers, Media Player, ...) through
/// GlobalSystemMediaTransportControls. Event driven; all members are used on the UI thread.
/// </summary>
internal sealed class MediaService : IDisposable
{
    public static readonly Color DefaultAccent = Color.FromRgb(0xF2, 0xF2, 0xF7);

    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _propertiesVersion;
    private bool _initialized;

    private TimeSpan _start, _end, _position;
    private DateTimeOffset _positionUpdated;
    private double _rate = 1;

    public MediaService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Title, artist, artwork, playback state or timeline changed.</summary>
    public event Action? Changed;

    /// <summary>A different track started (not raised for the track found at startup).</summary>
    public event Action? TrackChanged;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public BitmapSource? Art { get; private set; }

    /// <summary>The artwork shrunk to a few pixels: stretched out, it becomes a soft ambient glow.</summary>
    public BitmapSource? Backdrop { get; private set; }

    /// <summary>The player's app id, e.g. "Spotify.exe" or "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify".</summary>
    public string SourceAppId { get; private set; } = "";

    /// <summary>Readable player name for buttons and tooltips ("Spotify", "Chrome"...); empty when unknown.</summary>
    public string SourceAppName { get; private set; } = "";
    public Color Accent { get; private set; } = DefaultAccent;
    public bool IsPlaying { get; private set; }
    public bool CanGoNext { get; private set; }
    public bool CanGoPrevious { get; private set; }

    public bool HasSession => _session is not null && Title.Length > 0;
    public TimeSpan Duration => _end - _start;
    public bool HasTimeline => Duration.TotalSeconds >= 1;

    /// <summary>Current position, extrapolated from the last report while playing.</summary>
    public TimeSpan Position
    {
        get
        {
            var position = _position;
            if (IsPlaying && _positionUpdated.Year > 2000)
            {
                var elapsed = DateTimeOffset.Now - _positionUpdated;
                if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(12))
                    position += elapsed * _rate;
            }
            position -= _start;
            if (position < TimeSpan.Zero) return TimeSpan.Zero;
            return position > Duration ? Duration : position;
        }
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return;
        }

        _manager.CurrentSessionChanged += OnSessionsChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        await SelectSessionAsync();
        _initialized = true;
    }

    public void TogglePlayPause()
    {
        if (_session is null) return;

        // Optimistic update so the button reacts instantly; the real state follows via events.
        FreezePosition();
        IsPlaying = !IsPlaying;
        Changed?.Invoke();
        Run(s => s.TryTogglePlayPauseAsync());
    }

    public void Next() => Run(s => s.TrySkipNextAsync());

    public void Previous() => Run(s => s.TrySkipPreviousAsync());

    public void Seek(double fraction)
    {
        if (_session is null || !HasTimeline) return;

        var target = _start + Duration * Math.Clamp(fraction, 0, 1);
        _position = target;
        _positionUpdated = DateTimeOffset.Now;
        Changed?.Invoke();
        Run(s => s.TryChangePlaybackPositionAsync(target.Ticks));
    }

    public void Dispose()
    {
        Detach();
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnSessionsChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }
    }

    private async void Run(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> command)
    {
        if (_session is null) return;
        try
        {
            await command(_session);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    // WinRT raises these on worker threads; hop to the UI thread before touching state.
    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args) =>
        _dispatcher.InvokeAsync(() => _ = SelectSessionAsync());

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() => _ = RefreshPropertiesAsync());

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() =>
        {
            RefreshPlayback();
            // When this player pauses while another one is playing, follow the one that plays.
            if (!IsPlaying && !ReferenceEquals(PickSession(), _session))
                _ = SelectSessionAsync();
            else
                Changed?.Invoke();
        });

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() =>
        {
            RefreshTimeline();
            Changed?.Invoke();
        });

    private async Task SelectSessionAsync()
    {
        var next = PickSession();
        if (!ReferenceEquals(next, _session))
        {
            Detach();
            _session = next;
            Attach();
        }

        RefreshPlayback();
        RefreshTimeline();
        await RefreshPropertiesAsync();
    }

    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null) return null;
        try
        {
            var current = _manager.GetCurrentSession();
            if (current is not null && IsSessionPlaying(current)) return current;

            foreach (var session in _manager.GetSessions())
                if (IsSessionPlaying(session)) return session;

            return current;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession session) =>
        session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    private void Attach()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged += OnPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackChanged;
        _session.TimelinePropertiesChanged += OnTimelineChanged;
    }

    private void Detach()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackChanged;
        _session.TimelinePropertiesChanged -= OnTimelineChanged;
    }

    private async Task RefreshPropertiesAsync()
    {
        int version = ++_propertiesVersion;
        var session = _session;

        GlobalSystemMediaTransportControlsSessionMediaProperties? properties = null;
        if (session is not null)
        {
            try
            {
                properties = await session.TryGetMediaPropertiesAsync();
            }
            catch (Exception)
            {
                // The session can disappear between the event and this call.
            }
        }

        var (art, backdrop, accent) = await LoadArtAsync(properties?.Thumbnail);
        if (version != _propertiesVersion) return; // a newer refresh superseded this one

        string title = properties?.Title?.Trim() ?? "";
        string artist = FirstNonEmpty(properties?.Artist, properties?.AlbumArtist, AppName(session));
        bool trackChanged = title != Title || artist != Artist;

        // Some apps publish the title first and the thumbnail a moment later: keep the current artwork
        // while it still belongs to the same track.
        if (art is null && !trackChanged)
        {
            art = Art;
            backdrop = Backdrop;
            accent = Accent;
        }

        Title = title;
        Artist = artist;
        Album = properties?.AlbumTitle?.Trim() ?? "";
        SourceAppId = session?.SourceAppUserModelId ?? "";
        SourceAppName = AppName(session);
        Art = art;
        Backdrop = backdrop;
        Accent = accent;
        Changed?.Invoke();

        if (trackChanged && _initialized && title.Length > 0)
            TrackChanged?.Invoke();
    }

    private void RefreshPlayback()
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing != IsPlaying) FreezePosition();

            IsPlaying = playing;
            CanGoNext = info?.Controls.IsNextEnabled ?? false;
            CanGoPrevious = info?.Controls.IsPreviousEnabled ?? false;
            _rate = info?.PlaybackRate is double rate && rate > 0 ? rate : 1;
        }
        catch (Exception)
        {
            IsPlaying = false;
        }
    }

    private void RefreshTimeline()
    {
        try
        {
            var timeline = _session?.GetTimelineProperties();
            if (timeline is null)
            {
                _start = _end = _position = TimeSpan.Zero;
                return;
            }

            _start = timeline.StartTime;
            _end = timeline.EndTime;
            _position = timeline.Position;
            _positionUpdated = timeline.LastUpdatedTime;
        }
        catch (Exception)
        {
            _start = _end = _position = TimeSpan.Zero;
        }
    }

    /// <summary>Pins the extrapolated position so it stops (or restarts) moving from the right spot.</summary>
    private void FreezePosition()
    {
        _position = Position + _start;
        _positionUpdated = DateTimeOffset.Now;
    }

    private static async Task<(BitmapSource? Art, BitmapSource? Backdrop, Color Accent)> LoadArtAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return (null, null, DefaultAccent);
        try
        {
            byte[] bytes;
            using (var source = await thumbnail.OpenReadAsync())
            using (var stream = source.AsStreamForRead())
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer);
                bytes = buffer.ToArray();
            }

            // Decoding is the only "heavy" step; keep it off the UI thread so animations stay smooth.
            return await Task.Run(() =>
            {
                var art = Decode(bytes, 160);
                var tiny = Decode(bytes, 12);
                return ((BitmapSource?)art, (BitmapSource?)tiny, ArtPalette.Accent(tiny));
            });
        }
        catch (Exception)
        {
            return (null, null, DefaultAccent);
        }
    }

    private static BitmapImage Decode(byte[] bytes, int width)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }

    /// <summary>"Spotify.exe" → "Spotify", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "ZuneMusic".</summary>
    private static string AppName(GlobalSystemMediaTransportControlsSession? session)
    {
        string id = session?.SourceAppUserModelId ?? "";
        int bang = id.LastIndexOf('!');
        if (bang >= 0) id = id[(bang + 1)..];
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        int dot = id.LastIndexOf('.');
        if (dot >= 0 && dot < id.Length - 1) id = id[(dot + 1)..];

        // Some apps (e.g. Firefox) register a hash instead of a name; that is not worth showing.
        if (id.Length == 0 || (id.Length >= 12 && id.All(Uri.IsHexDigit))) return "";
        return char.ToUpperInvariant(id[0]) + id[1..];
    }
}
