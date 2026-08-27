namespace BoomCrashBacktester.Models;

/// <summary>
/// A single OHLC candle for a given symbol/timeframe/epoch.
/// </summary>
public sealed class Candle
{
    public string Symbol { get; set; } = string.Empty;
    public int GranularitySeconds { get; set; }
    public long EpochUtc { get; set; } // candle open time, unix seconds
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }

    public bool IsBullish => Close >= Open;

    public DateTime OpenTimeUtc => DateTimeOffset.FromUnixTimeSeconds(EpochUtc).UtcDateTime;
}

/// <summary>
/// Supported synthetic index symbols.
/// </summary>
public static class Symbols
{
    public const string Boom1000 = "BOOM1000";
    public const string Crash1000 = "CRASH1000";

    public static readonly string[] All = { Boom1000, Crash1000 };

    public static string DisplayName(string symbol) => symbol switch
    {
        Boom1000 => "Boom 1000",
        Crash1000 => "Crash 1000",
        _ => symbol
    };
}

/// <summary>
/// Supported timeframes, mapped to Deriv's granularity in seconds.
/// </summary>
public static class Timeframes
{
    public static readonly (string Label, int Seconds)[] All =
    {
        ("1m", 60),
        ("5m", 300),
        ("15m", 900),
        ("30m", 1800),
        ("1h", 3600),
        ("4h", 14400),
        ("1d", 86400),
    };

    public const int Default = 60; // 1m
}

/// <summary>Outcome of a ranged download — tells the caller exactly what it actually got.</summary>
public sealed class DownloadResult
{
    public int CandlesFetched { get; init; }
    public int CandlesExpected { get; init; }
    public long? ActualEarliestEpoch { get; init; }
    public long? ActualLatestEpoch { get; init; }
    public bool WasCancelled { get; init; }
    public string? Error { get; init; }

    public bool GotLessThanRequested => CandlesFetched < CandlesExpected && Error is null;
}
