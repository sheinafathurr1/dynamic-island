using System.Globalization;
using System.Text.Json;

namespace DynamicIsland.Services;

/// <summary>How a weather icon is drawn.</summary>
internal enum WeatherKind
{
    Clear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Drizzle,
    Rain,
    Snow,
    Storm,
}

internal sealed record WeatherHour(DateTime Time, double Temperature, int Code, bool IsDay);

/// <summary>Current weather plus the next hours, as returned by Open-Meteo (times are local to the place).</summary>
internal sealed record WeatherForecast(double Temperature, int Code, bool IsDay, IReadOnlyList<WeatherHour> Hours)
{
    /// <summary>Reads Open-Meteo's /v1/forecast answer (current + hourly temperature_2m, weather_code, is_day).</summary>
    public static WeatherForecast Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var current = root.GetProperty("current");
        var currentTime = ParseTime(current.GetProperty("time").GetString());

        var hourly = root.GetProperty("hourly");
        var times = hourly.GetProperty("time").EnumerateArray().Select(t => ParseTime(t.GetString())).ToList();
        var temperatures = hourly.GetProperty("temperature_2m").EnumerateArray().Select(Number).ToList();
        var codes = hourly.GetProperty("weather_code").EnumerateArray().Select(c => (int)Number(c)).ToList();
        var days = hourly.TryGetProperty("is_day", out var isDay) ? isDay.EnumerateArray().Select(d => Number(d) > 0).ToList() : [];

        var hours = new List<WeatherHour>();
        for (int i = 0; i < times.Count && i < temperatures.Count && i < codes.Count; i++)
        {
            if (times[i] <= currentTime) continue; // only what is still to come
            hours.Add(new WeatherHour(times[i], temperatures[i], codes[i], i >= days.Count || days[i]));
        }

        return new WeatherForecast(
            Number(current.GetProperty("temperature_2m")),
            (int)Number(current.GetProperty("weather_code")),
            !current.TryGetProperty("is_day", out var day) || Number(day) > 0,
            hours);
    }

    /// <summary>WMO weather code → icon.</summary>
    public static WeatherKind KindOf(int code) => code switch
    {
        0 => WeatherKind.Clear,
        1 or 2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Cloudy,
        45 or 48 => WeatherKind.Fog,
        >= 51 and <= 57 => WeatherKind.Drizzle,
        (>= 61 and <= 67) or (>= 80 and <= 82) => WeatherKind.Rain,
        (>= 71 and <= 77) or 85 or 86 => WeatherKind.Snow,
        >= 95 => WeatherKind.Storm,
        _ => WeatherKind.Cloudy,
    };

    /// <summary>WMO weather code → a short Indonesian description.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Cerah",
        1 => "Cerah berawan",
        2 => "Berawan sebagian",
        3 => "Mendung",
        45 or 48 => "Berkabut",
        51 or 53 or 55 => "Gerimis",
        56 or 57 => "Gerimis beku",
        61 => "Hujan ringan",
        63 => "Hujan",
        65 => "Hujan lebat",
        66 or 67 => "Hujan beku",
        71 or 73 or 75 or 77 or 85 or 86 => "Salju",
        80 => "Hujan lokal",
        81 => "Hujan sedang",
        82 => "Hujan sangat lebat",
        95 => "Badai petir",
        96 or 99 => "Badai petir & hujan es",
        _ => "Berawan",
    };

    private static double Number(JsonElement element) => element.ValueKind == JsonValueKind.Number ? element.GetDouble() : 0;

    private static DateTime ParseTime(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : DateTime.MinValue;
}

/// <summary>A place found by name ("Bandung" → coordinates), from Open-Meteo's geocoding API.</summary>
internal sealed record WeatherPlace(string Name, double Latitude, double Longitude)
{
    public static WeatherPlace? ParseSearch(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var first = results[0];
        string name = first.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        return new WeatherPlace(name, first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
    }
}
