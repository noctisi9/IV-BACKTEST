using Microsoft.Data.Sqlite;
using BoomCrashBacktester.Models;

namespace BoomCrashBacktester.Services;

/// <summary>
/// Local persistent store for downloaded candle history.
/// One SQLite file, one table, indexed by (symbol, granularity, epoch).
/// </summary>
public sealed class CandleStore : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;

    public CandleStore(string? dbPath = null)
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BoomCrashBacktester");
        Directory.CreateDirectory(dataDir);

        _dbPath = dbPath ?? Path.Combine(dataDir, "candles.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS candles (
                symbol TEXT NOT NULL,
                granularity INTEGER NOT NULL,
                epoch INTEGER NOT NULL,
                open REAL NOT NULL,
                high REAL NOT NULL,
                low REAL NOT NULL,
                close REAL NOT NULL,
                PRIMARY KEY (symbol, granularity, epoch)
            );
            CREATE INDEX IF NOT EXISTS idx_candles_lookup
                ON candles (symbol, granularity, epoch);
            """;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts or replaces a batch of candles. Safe to call repeatedly with overlapping data.
    /// </summary>
    public void UpsertCandles(IEnumerable<Candle> candles)
    {
        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO candles (symbol, granularity, epoch, open, high, low, close)
            VALUES ($symbol, $granularity, $epoch, $open, $high, $low, $close)
            ON CONFLICT(symbol, granularity, epoch) DO UPDATE SET
                open = excluded.open,
                high = excluded.high,
                low = excluded.low,
                close = excluded.close;
            """;

        var pSymbol = cmd.CreateParameter(); pSymbol.ParameterName = "$symbol"; cmd.Parameters.Add(pSymbol);
        var pGran = cmd.CreateParameter(); pGran.ParameterName = "$granularity"; cmd.Parameters.Add(pGran);
        var pEpoch = cmd.CreateParameter(); pEpoch.ParameterName = "$epoch"; cmd.Parameters.Add(pEpoch);
        var pOpen = cmd.CreateParameter(); pOpen.ParameterName = "$open"; cmd.Parameters.Add(pOpen);
        var pHigh = cmd.CreateParameter(); pHigh.ParameterName = "$high"; cmd.Parameters.Add(pHigh);
        var pLow = cmd.CreateParameter(); pLow.ParameterName = "$low"; cmd.Parameters.Add(pLow);
        var pClose = cmd.CreateParameter(); pClose.ParameterName = "$close"; cmd.Parameters.Add(pClose);

        foreach (var c in candles)
        {
            pSymbol.Value = c.Symbol;
            pGran.Value = c.GranularitySeconds;
            pEpoch.Value = c.EpochUtc;
            pOpen.Value = c.Open;
            pHigh.Value = c.High;
            pLow.Value = c.Low;
            pClose.Value = c.Close;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Loads all stored candles for a symbol/timeframe, ordered oldest to newest.
    /// </summary>
    public List<Candle> LoadCandles(string symbol, int granularitySeconds)
    {
        var result = new List<Candle>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT epoch, open, high, low, close
            FROM candles
            WHERE symbol = $symbol AND granularity = $granularity
            ORDER BY epoch ASC;
            """;
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$granularity", granularitySeconds);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Candle
            {
                Symbol = symbol,
                GranularitySeconds = granularitySeconds,
                EpochUtc = reader.GetInt64(0),
                Open = reader.GetDouble(1),
                High = reader.GetDouble(2),
                Low = reader.GetDouble(3),
                Close = reader.GetDouble(4),
            });
        }
        return result;
    }

    /// <summary>
    /// Returns the newest stored epoch for a symbol/timeframe, or null if nothing is stored yet.
    /// </summary>
    public long? GetLatestEpoch(string symbol, int granularitySeconds)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT MAX(epoch) FROM candles WHERE symbol = $symbol AND granularity = $granularity;
            """;
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$granularity", granularitySeconds);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : null;
    }

    /// <summary>
    /// Returns the oldest stored epoch for a symbol/timeframe, or null if nothing is stored yet.
    /// Used to know where to keep back-filling from.
    /// </summary>
    public long? GetOldestEpoch(string symbol, int granularitySeconds)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(epoch) FROM candles WHERE symbol = $symbol AND granularity = $granularity;
            """;
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$granularity", granularitySeconds);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : null;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
