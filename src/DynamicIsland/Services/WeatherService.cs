using System.Net.Http;
using System.Windows.Threading;
using Windows.Devices.Geolocation;

namespace DynamicIsland.Services;

internal enum WeatherState
{
    Off,
    Loading,
    Ready,

    /// <summary>No city set and Windows location is off or unavailable.</summary>
    NoLocation,
    Failed,
}

/// <summary>
/// Weather for the "today" page from Open-Meteo (free, no account or key). The place is a city
/// set in the settings, or else Windows' own location. Refreshes every 30 minutes.
/// </summary>
internal sealed class WeatherService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = RefreshInterval };
    private string _city = "";
    private WeatherPlace? _place;
    private int _version;

    public WeatherService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/1.0 (+https://github.com/sheinafathurr1/dynamic-island)");
        _refreshTimer.Tick += (_, _) => Refresh();
    }

    /// <summary>Raised on the UI thread when the forecast or the state changed.</summary>
    public event Action? Changed;

    public WeatherState State { get; private set; }

    public WeatherForecast? Forecast { get; private set; }

    /// <summary>Name of the city the weather is for; empty when it comes from Windows' location.</summary>
    public string PlaceName => _city.Length > 0 ? _place?.Name ?? _city : "";

    /// <summary>Turns the weather on or off, for a city (empty = Windows' location).</summary>
    public void Configure(bool enabled, string? city)
    {
        city = city?.Trim() ?? "";
        if (city != _city) _place = null;
        _city = city;
        _refreshTimer.Stop();
        if (!enabled)
        {
            _version++;
            State = WeatherState.Off;
            Forecast = null;
            Changed?.Invoke();
            return;
        }
        if (Forecast is null) State = WeatherState.Loading;
        Refresh();
        _refreshTimer.Start();
    }

    public async void Refresh()
    {
        int version = ++_version;
        try
        {
            _place ??= _city.Length > 0 ? await FindCityAsync(_city) : await LocateAsync();
            if (version != _version) return;
            if (_place is null)
            {
                State = WeatherState.NoLocation;
                Changed?.Invoke();
                return;
            }

            string url = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={_place.Latitude:0.####}&longitude={_place.Longitude:0.####}&current=temperature_2m,weather_code,is_day&hourly=temperature_2m,weather_code,is_day&forecast_hours=8&timezone=auto");
            string json = await _http.GetStringAsync(url);
            if (version != _version) return;
            Forecast = WeatherForecast.Parse(json);
            State = WeatherState.Ready;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (version != _version) return;
            if (Forecast is null) State = WeatherState.Failed; // offline for a moment: keep the last forecast
        }
        Changed?.Invoke();
    }

    private async Task<WeatherPlace?> FindCityAsync(string city)
    {
        string json = await _http.GetStringAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=id&format=json");
        return WeatherPlace.ParseSearch(json);
    }

    /// <summary>Windows' location (Settings → Privacy → Location, "let desktop apps access your location").</summary>
    private static async Task<WeatherPlace?> LocateAsync()
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed) return null;
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromHours(3), TimeSpan.FromSeconds(10));
            var point = position.Coordinate.Point.Position;
            return new WeatherPlace("", point.Latitude, point.Longitude);
        }
        catch (Exception)
        {
            return null; // location off, no sensor, timed out
        }
    }
}
