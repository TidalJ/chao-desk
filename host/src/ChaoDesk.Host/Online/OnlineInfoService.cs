using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ChaoDesk.Host.Localization;
using Serilog;

namespace ChaoDesk.Host.Online;

/// <summary>One row of the Bank of China foreign-exchange quotation table (RMB per 100 units).</summary>
public sealed record ExchangeRate(string Currency, double? Buy, double? CashBuy, double? Sell, double? CashSell, double? Middle, string Published);

/// <summary>One day in the official Chinese public-holiday schedule: a day off or a make-up working day.</summary>
public sealed record HolidayDay(string Date, string Name, bool IsOffDay);

/// <summary>Weather for the user's city (Open-Meteo; WMO weather codes, °C, km/h).</summary>
public sealed record WeatherPlace(string Name, string Region, double Lat, double Lon);
public sealed record WeatherNow(double Temp, double Feels, int Humidity, double Wind, int Code, bool IsDay);
public sealed record WeatherDay(string Date, int Code, double Max, double Min, int? RainChance, string Sunrise, string Sunset);
public sealed record WeatherReport(WeatherPlace Place, WeatherNow Now, IReadOnlyList<WeatherDay> Days, int? Aqi, DateTime FetchedAt);

/// <summary>
/// The only network access in Chao Desk, and only when the user enables "Online info": the host (never the
/// wallpaper page) fetches the Bank of China quotation page every 30 minutes, the official holiday
/// schedule (NateScarlet/holiday-cn via jsDelivr) about once a week, caching the latter on disk, and
/// the weather for the chosen city (Open-Meteo, no account needed) every 30 minutes.
/// Results are handed to the pages as messages. UI thread only.
/// </summary>
public sealed partial class OnlineInfoService : IDisposable
{
    public const string BocRatesUrl = "https://www.boc.cn/sourcedb/whpj/";
    private const string HolidayUrl = "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{0}.json";
    private static readonly TimeSpan RatesEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HolidaysMaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan WeatherEvery = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer;
    private readonly string _cacheDir;
    private bool _enabled;
    private DateTime _lastRates = DateTime.MinValue;
    private DateTime _lastHolidays = DateTime.MinValue;
    private DateTime _lastWeather = DateTime.MinValue;
    private string _city = "";
    private WeatherPlace? _place;

    public IReadOnlyList<ExchangeRate> Rates { get; private set; } = [];
    public DateTime? RatesFetchedAt { get; private set; }
    public IReadOnlyList<HolidayDay> Holidays { get; private set; } = [];
    public string? LastError { get; private set; }
    public WeatherReport? Weather { get; private set; }
    /// <summary>Stays English: the widgets page reads it (it looks for "not found").</summary>
    public string? WeatherError { get; private set; }

    /// <summary>City for the weather page (any language Open-Meteo's geocoder knows, e.g. 北京 or Sydney).</summary>
    public string WeatherCity
    {
        get => _city;
        set
        {
            value = (value ?? "").Trim();
            if (value == _city) return;
            _city = value;
            _place = null;
            Weather = null;
            WeatherError = null;
            _lastWeather = DateTime.MinValue;
            if (_enabled) _ = RefreshAsync(force: false);
        }
    }

    /// <summary>Raised on the UI thread after new data arrived.</summary>
    public event Action? Updated;

    public OnlineInfoService(string cacheDir)
    {
        _cacheDir = cacheDir;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ChaoDesk/0.2");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => await RefreshAsync(force: false);
        Holidays = LoadCachedHolidays();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value) { _timer.Start(); _ = RefreshAsync(force: true); }
            else _timer.Stop();
        }
    }

    public async Task RefreshAsync(bool force)
    {
        if (!_enabled) return;
        bool changed = false;
        if (force || DateTime.UtcNow - _lastRates >= RatesEvery)
        {
            _lastRates = DateTime.UtcNow;
            try
            {
                string html = await _http.GetStringAsync(BocRatesUrl);
                var rates = ParseBocRates(html);
                if (rates.Count > 0) { Rates = rates; RatesFetchedAt = DateTime.Now; changed = true; LastError = null; }
                else LastError = Loc.T("Bank of China page had no rates (layout changed?)");
                Log.Information("Exchange rates: {Count} currencies", rates.Count);
            }
            catch (Exception ex) { LastError = Loc.F("Exchange rates: {0}", ex.Message); Log.Warning("Exchange rates fetch failed: {Message}", ex.Message); }
        }
        if (force || DateTime.UtcNow - _lastHolidays >= TimeSpan.FromHours(6))
        {
            _lastHolidays = DateTime.UtcNow;
            int year = DateTime.Now.Year;
            var years = DateTime.Now.Month >= 11 ? new[] { year, year + 1 } : new[] { year };
            foreach (int y in years) changed |= await RefreshHolidaysAsync(y);
            if (changed) Holidays = LoadCachedHolidays();
        }
        if (_city.Length > 0 && (force || DateTime.UtcNow - _lastWeather >= WeatherEvery))
        {
            _lastWeather = DateTime.UtcNow;
            changed |= await RefreshWeatherAsync();
        }
        if (changed) Updated?.Invoke();
    }

    private async Task<bool> RefreshWeatherAsync()
    {
        string city = _city;
        try
        {
            if (_place == null)
            {
                _place = ParsePlace(await _http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=zh&format=json"));
                if (_place == null) { WeatherError = $"City not found: {city}"; Weather = null; return true; }
            }
            var p = _place;
            string ll = FormattableString.Invariant($"latitude={p.Lat:0.####}&longitude={p.Lon:0.####}");
            string forecast = await _http.GetStringAsync($"https://api.open-meteo.com/v1/forecast?{ll}&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max&timezone=auto&forecast_days=5");
            int? aqi = null;
            try { aqi = ParseAqi(await _http.GetStringAsync($"https://air-quality-api.open-meteo.com/v1/air-quality?{ll}&current=us_aqi")); }
            catch (Exception ex) { Log.Debug("Air quality fetch failed: {Message}", ex.Message); }
            if (city != _city) return false; // the city changed while we were fetching
            var report = ParseForecast(forecast, p, aqi);
            if (report == null) { WeatherError = "Weather service returned no data"; return false; }
            Weather = report;
            WeatherError = null;
            Log.Information("Weather for {City}: {Temp} °C, code {Code}", p.Name, report.Now.Temp, report.Now.Code);
            return true;
        }
        catch (Exception ex) { WeatherError = $"Weather: {ex.Message}"; Log.Warning("Weather fetch failed: {Message}", ex.Message); return false; }
    }

    private async Task<bool> RefreshHolidaysAsync(int year)
    {
        string file = Path.Combine(_cacheDir, $"holidays-{year}.json");
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < HolidaysMaxAge) return false;
        try
        {
            string json = await _http.GetStringAsync(string.Format(HolidayUrl, year));
            if (ParseHolidays(json).Count == 0) return false; // published schedule not out yet
            Directory.CreateDirectory(_cacheDir);
            await File.WriteAllTextAsync(file, json);
            Log.Information("Holiday schedule {Year} updated", year);
            return true;
        }
        catch (Exception ex) { Log.Warning("Holiday schedule {Year} fetch failed: {Message}", year, ex.Message); return false; }
    }

    private List<HolidayDay> LoadCachedHolidays()
    {
        var all = new List<HolidayDay>();
        if (!Directory.Exists(_cacheDir)) return all;
        foreach (string f in Directory.GetFiles(_cacheDir, "holidays-*.json"))
        {
            try { all.AddRange(ParseHolidays(File.ReadAllText(f))); }
            catch (Exception ex) { Log.Debug(ex, "Bad holiday cache {File}", f); }
        }
        return all.OrderBy(d => d.Date, StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex("""<tr data-currency='(?<name>[^']+)'>\s*<td>[^<]*</td>\s*<td>(?<b>[^<]*)</td>\s*<td>(?<cb>[^<]*)</td>\s*<td>(?<s>[^<]*)</td>\s*<td>(?<cs>[^<]*)</td>\s*<td>(?<m>[^<]*)</td>\s*<td[^>]*>(?<t>[^<]*)</td>""", RegexOptions.Singleline)]
    private static partial Regex BocRow();

    /// <summary>Parse the quotation table of https://www.boc.cn/sourcedb/whpj/ (values are RMB per 100 units).</summary>
    public static List<ExchangeRate> ParseBocRates(string html)
    {
        static double? Num(string s) => double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
        var list = new List<ExchangeRate>();
        foreach (Match m in BocRow().Matches(html))
        {
            list.Add(new ExchangeRate(m.Groups["name"].Value.Trim(), Num(m.Groups["b"].Value), Num(m.Groups["cb"].Value),
                Num(m.Groups["s"].Value), Num(m.Groups["cs"].Value), Num(m.Groups["m"].Value), m.Groups["t"].Value.Trim()));
        }
        return list;
    }

    /// <summary>Parse a holiday-cn year file: {"year":2026,"days":[{"name":"元旦","date":"2026-01-01","isOffDay":true},…]}.</summary>
    public static List<HolidayDay> ParseHolidays(string json)
    {
        var list = new List<HolidayDay>();
        if (JsonNode.Parse(json)?["days"] is not JsonArray days) return list;
        foreach (var d in days)
        {
            string? date = d?["date"]?.GetValue<string>(), name = d?["name"]?.GetValue<string>();
            if (date is { Length: 10 } && !string.IsNullOrWhiteSpace(name) && d?["isOffDay"] is JsonValue off && off.TryGetValue(out bool isOff))
                list.Add(new HolidayDay(date, name, isOff));
        }
        return list;
    }

    /// <summary>First result of an Open-Meteo geocoding search, or null.</summary>
    public static WeatherPlace? ParsePlace(string json)
    {
        if (JsonNode.Parse(json)?["results"] is not JsonArray { Count: > 0 } results || results[0] is not JsonObject r) return null;
        string name = r["name"]?.GetValue<string>() ?? "";
        string region = r["admin1"]?.GetValue<string>() ?? r["country"]?.GetValue<string>() ?? "";
        if (r["latitude"] is not JsonValue la || r["longitude"] is not JsonValue lo || !la.TryGetValue(out double lat) || !lo.TryGetValue(out double lon)) return null;
        return new WeatherPlace(name, region, lat, lon);
    }

    /// <summary>Parse an Open-Meteo forecast (current + daily blocks).</summary>
    public static WeatherReport? ParseForecast(string json, WeatherPlace place, int? aqi)
    {
        var root = JsonNode.Parse(json);
        if (root?["current"] is not JsonObject c || root["daily"] is not JsonObject d) return null;
        static double Num(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double x) ? x : double.NaN;
        var now = new WeatherNow(Num(c["temperature_2m"]), Num(c["apparent_temperature"]), (int)Math.Round(Num(c["relative_humidity_2m"])),
            Num(c["wind_speed_10m"]), (int)Num(c["weather_code"]), Num(c["is_day"]) >= 1);
        if (double.IsNaN(now.Temp)) return null;
        var days = new List<WeatherDay>();
        if (d["time"] is JsonArray times)
            for (int i = 0; i < times.Count; i++)
            {
                double rain = Num((d["precipitation_probability_max"] as JsonArray)?[i]);
                days.Add(new WeatherDay(times[i]?.GetValue<string>() ?? "", (int)Num((d["weather_code"] as JsonArray)?[i]),
                    Num((d["temperature_2m_max"] as JsonArray)?[i]), Num((d["temperature_2m_min"] as JsonArray)?[i]),
                    double.IsNaN(rain) ? null : (int)rain,
                    (d["sunrise"] as JsonArray)?[i]?.GetValue<string>() ?? "", (d["sunset"] as JsonArray)?[i]?.GetValue<string>() ?? ""));
            }
        return new WeatherReport(place, now, days, aqi, DateTime.Now);
    }

    public static int? ParseAqi(string json) =>
        JsonNode.Parse(json)?["current"]?["us_aqi"] is JsonValue v && v.TryGetValue(out double x) ? (int)Math.Round(x) : null;

    /// <summary>Messages for the wallpaper pages.</summary>
    public string RatesMessage() => JsonSerializer.Serialize(new
    {
        type = "rates",
        source = BocRatesUrl,
        fetchedAt = RatesFetchedAt?.ToString("yyyy-MM-dd HH:mm"),
        rates = Rates.Select(r => new { currency = r.Currency, buy = r.Buy, cashBuy = r.CashBuy, sell = r.Sell, cashSell = r.CashSell, middle = r.Middle, published = r.Published }),
    });

    public string HolidaysMessage() => JsonSerializer.Serialize(new
    {
        type = "holidays",
        days = Holidays.Select(d => new { date = d.Date, name = d.Name, off = d.IsOffDay }),
    });

    public string WeatherMessage()
    {
        // JSON has no NaN: missing numbers become null.
        static double? N(double v) => double.IsNaN(v) ? null : Math.Round(v, 1);
        var w = Weather;
        return JsonSerializer.Serialize(new
        {
            type = "weather",
            city = _city,
            error = WeatherError,
            place = w == null ? null : new { name = w.Place.Name, region = w.Place.Region },
            now = w == null ? null : new { temp = N(w.Now.Temp), feels = N(w.Now.Feels), humidity = w.Now.Humidity, wind = N(w.Now.Wind), code = w.Now.Code, isDay = w.Now.IsDay },
            days = w?.Days.Select(x => new { date = x.Date, code = x.Code, max = N(x.Max), min = N(x.Min), rain = x.RainChance, sunrise = x.Sunrise, sunset = x.Sunset }),
            aqi = w?.Aqi,
            fetchedAt = w?.FetchedAt.ToString("HH:mm"),
        });
    }

    public void Dispose()
    {
        _timer.Stop();
        _http.Dispose();
    }
}
