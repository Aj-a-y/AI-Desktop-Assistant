using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopAssistant;

internal sealed class WeatherService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
        DefaultRequestHeaders = { { "User-Agent", "DesktopAssistant/1.0" } }
    };

    private static readonly Regex WeatherLocationRegex = new(
        @"\b(?:weather|forecast)\b.*?\b(?:in|for|at)\s+(?<place>.+?)(?=\s+(?:today|tomorrow|right now|currently|please|now)\b|[?.!]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryGetLocation(string query, out string location)
    {
        var match = WeatherLocationRegex.Match(query.Trim());
        location = match.Success ? match.Groups["place"].Value.Trim().TrimEnd(',', '.') : string.Empty;
        return location.Length > 1;
    }

    public async Task<string> GetCurrentReportAsync(string location, bool tomorrow = false, CancellationToken cancellationToken = default)
    {
        var geocodeUrl = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(location)
            + "&count=1&language=en&format=json";
        using var geocodeJson = await GetJsonAsync(geocodeUrl, "City lookup", cancellationToken);
        if (!geocodeJson.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return $"I couldn't find a location called {location}. Please check the place name and try again.";

        var place = results[0];
        var latitude = place.GetProperty("latitude").GetDouble();
        var longitude = place.GetProperty("longitude").GetDouble();
        var placeName = place.GetProperty("name").GetString() ?? location;
        var region = place.TryGetProperty("admin1", out var admin) ? admin.GetString() : null;
        var country = place.TryGetProperty("country", out var countryValue) ? countryValue.GetString() : null;
        var label = string.Join(", ", new[] { placeName, region, country }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        var forecastUrl = "https://api.open-meteo.com/v1/forecast?latitude=" + latitude.ToString("0.####", CultureInfo.InvariantCulture)
            + "&longitude=" + longitude.ToString("0.####", CultureInfo.InvariantCulture)
            + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m"
            + "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max,weather_code"
            + "&forecast_days=2&timezone=auto&wind_speed_unit=kmh";
        using var forecastJson = await GetJsonAsync(forecastUrl, "Forecast request", cancellationToken);

        var root = forecastJson.RootElement;
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        var time = current.GetProperty("time").GetString() ?? "";
        var temperature = current.GetProperty("temperature_2m").GetDouble();
        var feelsLike = current.GetProperty("apparent_temperature").GetDouble();
        var humidity = current.GetProperty("relative_humidity_2m").GetInt32();
        var wind = current.GetProperty("wind_speed_10m").GetDouble();
        var currentDescription = DescribeCode(current.GetProperty("weather_code").GetInt32());
        var dayIndex = tomorrow ? 1 : 0;
        var high = daily.GetProperty("temperature_2m_max")[dayIndex].GetDouble();
        var low = daily.GetProperty("temperature_2m_min")[dayIndex].GetDouble();
        var precipitationChance = daily.GetProperty("precipitation_probability_max")[dayIndex].GetInt32();
        var outlookDescription = DescribeCode(daily.GetProperty("weather_code")[dayIndex].GetInt32());

        if (tomorrow)
        {
            var date = daily.GetProperty("time")[dayIndex].GetString();
            return $"Tomorrow ({date}) in {label}, expect {outlookDescription}, with a high of {high:0.#}°C, a low of {low:0.#}°C, and up to {precipitationChance}% precipitation probability. " +
                   "Forecast source: [Open-Meteo](https://open-meteo.com/).";
        }

        return $"Currently in {label}, it is {temperature:0.#}°C with {currentDescription}; it feels like {feelsLike:0.#}°C, with {humidity}% humidity and winds at {wind:0.#} km/h. " +
               $"Today's outlook is {outlookDescription}, with a high of {high:0.#}°C, a low of {low:0.#}°C, and up to {precipitationChance}% precipitation probability. " +
               $"Updated {time} local time. Source: [Open-Meteo](https://open-meteo.com/).";
    }

    private static string DescribeCode(int code) => code switch
    {
        0 => "clear skies",
        1 => "mostly clear skies",
        2 => "partly cloudy skies",
        3 => "overcast skies",
        45 or 48 => "fog",
        51 or 53 or 55 => "drizzle",
        56 or 57 => "freezing drizzle",
        61 or 63 or 65 => "rain",
        66 or 67 => "freezing rain",
        71 or 73 or 75 => "snowfall",
        77 => "snow grains",
        80 or 81 or 82 => "rain showers",
        85 or 86 => "snow showers",
        95 => "a thunderstorm",
        96 or 99 => "a thunderstorm with hail",
        _ => "conditions unavailable"
    };

    private static async Task<JsonDocument> GetJsonAsync(string url, string requestName, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonDocument.Parse(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Open-Meteo {requestName} failed ({exception.GetType().Name}): {exception.Message}",
                exception);
        }
    }
}
