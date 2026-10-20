using ChaoDesk.Host.Online;
using ChaoDesk.Host.Settings;

namespace ChaoDesk.Host.Tests;

public class OnlineInfoTests
{
    // Trimmed from the real https://www.boc.cn/sourcedb/whpj/ page (2026-09-29).
    private const string BocSample = """
        <tr data-currency='美元'>
            <td>美元</td>
            <td>711.35</td>
            <td>711.35</td>
            <td>714.33</td>
            <td>714.33</td>
            <td>712.62</td>
            <td class="pjrq">2026/09/29 19:06:02</td>
            <td>19:06:02</td>
        </tr>
        <tr data-currency='澳大利亚元'>

        	<td>澳大利亚元</td>
            <td>467.85</td>
            <td>467.85</td>
            <td>471.57</td>
            <td>471.57</td>
            <td>471.97</td>
            <td class="pjrq">2026/09/29 19:06:02</td>
            <td>19:06:02</td>
        </tr>
        <tr data-currency='文莱元'>
            <td>文莱元</td>
            <td></td>
            <td>532.1</td>
            <td></td>
            <td>535.9</td>
            <td>534.2</td>
            <td class="pjrq">2026/09/29 19:06:02</td>
            <td>19:06:02</td>
        </tr>
        """;

    [Fact]
    public void ParsesBankOfChinaRates()
    {
        var rates = OnlineInfoService.ParseBocRates(BocSample);
        Assert.Equal(3, rates.Count);
        var aud = rates.Single(r => r.Currency == "澳大利亚元");
        Assert.Equal(467.85, aud.Buy);
        Assert.Equal(471.57, aud.Sell);
        Assert.Equal(471.97, aud.Middle);
        Assert.Equal("2026/09/29 19:06:02", aud.Published);
        var bnd = rates.Single(r => r.Currency == "文莱元");
        Assert.Null(bnd.Buy); // empty cells stay empty
        Assert.Equal(535.9, bnd.CashSell);
    }

    [Fact]
    public void ParsesHolidaySchedule()
    {
        var days = OnlineInfoService.ParseHolidays("""
            {"year":2026,"papers":[],"days":[
              {"name":"国庆节","date":"2026-09-20","isOffDay":false},
              {"name":"国庆节","date":"2026-10-01","isOffDay":true},
              {"name":"broken","date":"2026-1","isOffDay":true}
            ]}
            """);
        Assert.Equal(2, days.Count);
        Assert.False(days[0].IsOffDay);
        Assert.True(days[1].IsOffDay);
        Assert.Empty(OnlineInfoService.ParseHolidays("""{"year":2027,"days":[]}"""));
    }

    [Fact]
    public void ParsesOpenMeteoPlaceAndForecast()
    {
        var place = OnlineInfoService.ParsePlace("""
            {"results":[{"id":1816670,"name":"北京","latitude":39.9075,"longitude":116.39723,"country":"中国","admin1":"北京市"}]}
            """);
        Assert.NotNull(place);
        Assert.Equal("北京", place.Name);
        Assert.Equal("北京市", place.Region);
        Assert.Null(OnlineInfoService.ParsePlace("""{"generationtime_ms":0.1}"""));

        var report = OnlineInfoService.ParseForecast("""
            {"current":{"time":"2026-09-29T20:00","temperature_2m":22.8,"relative_humidity_2m":63,"apparent_temperature":23.5,"weather_code":0,"wind_speed_10m":7.3,"is_day":0},
             "daily":{"time":["2026-09-29","2026-09-30"],"weather_code":[53,2],"temperature_2m_max":[26.3,19.9],"temperature_2m_min":[15.1,12.0],
                      "sunrise":["2026-09-29T06:09","2026-09-30T06:10"],"sunset":["2026-09-29T18:03","2026-09-30T18:01"],"precipitation_probability_max":[40,null]}}
            """, place, 62);
        Assert.NotNull(report);
        Assert.Equal(22.8, report.Now.Temp);
        Assert.False(report.Now.IsDay);
        Assert.Equal(2, report.Days.Count);
        Assert.Equal(53, report.Days[0].Code);
        Assert.Equal(40, report.Days[0].RainChance);
        Assert.Null(report.Days[1].RainChance);
        Assert.Equal("2026-09-29T18:03", report.Days[0].Sunset);
        Assert.Equal(62, report.Aqi);
        Assert.Equal(17, OnlineInfoService.ParseAqi("""{"current":{"time":"2026-09-29T12:00","us_aqi":17.4}}"""));
    }

    [Fact]
    public void TasksAreCleanedOnLoad()
    {
        var s = new AppSettings { Tasks = [new TaskItem { Text = "  " }, new TaskItem { Id = "", Text = new string('x', 200) }] }.Normalize();
        Assert.Single(s.Tasks);
        Assert.Equal(120, s.Tasks[0].Text.Length);
        Assert.False(string.IsNullOrEmpty(s.Tasks[0].Id));
    }
}
