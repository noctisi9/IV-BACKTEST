using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BoomCrashBacktester.Models;

namespace BoomCrashBacktester.Services;

/// <summary>
/// Talks to Deriv's public WebSocket API to backfill historical candles and
/// receive live candle updates for Boom 1000 / Crash 1000.
///
/// App ID is a shared public one by default (1089). If it becomes unreliable
/// under bulk-download load, swap AppId for a personal one registered at
/// https://developers.deriv.com/ — nothing else in this class needs to change.
/// </summary>
public sealed class DerivDataService : IAsyncDisposable
{
    public int AppId { get; set; } = 1089;

    private const int MaxCandlesPerRequest = 5000; // Deriv's per-request cap
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _receiveLoopCts;

    public event Action<Candle>? LiveCandleUpdated;
    public event Action<string>? ConnectionError;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _socket = new ClientWebSocket();
        var uri = new Uri($"wss://ws.derivws.com/websockets/v3?app_id={AppId}");
        await _socket.ConnectAsync(uri, ct);

        _receiveLoopCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_receiveLoopCts.Token));
    }

    /// <summary>
    /// Downloads candles for an explicit [fromUtc, toUtc) window only — never more than
    /// the user asked for. Reports progress as (fetched, expectedTotal) so a UI can show
    /// a real percentage. Stops cleanly (no exception, no hang) if Deriv has less data
    /// than requested for that window; the caller finds out exactly how much it got via
    /// the returned DownloadResult.
    /// </summary>
    public async Task<DownloadResult> DownloadRangeAsync(
        string symbol,
        int granularitySeconds,
        DateTime fromUtc,
        DateTime toUtc,
        CandleStore store,
        IProgress<(int fetched, int expectedTotal)>? progress = null,
        CancellationToken ct = default)
    {
        long fromEpoch = new DateTimeOffset(fromUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        long toEpoch = new DateTimeOffset(toUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        if (toEpoch <= fromEpoch)
            return new DownloadResult { Error = "End time must be after start time." };

        int expectedTotal = (int)Math.Max(1, (toEpoch - fromEpoch) / granularitySeconds);
        long chunkSpanSeconds = (long)MaxCandlesPerRequest * granularitySeconds;

        long cursor = fromEpoch;
        int fetched = 0;
        int consecutiveEmptyChunks = 0;
        long? actualEarliest = null;
        long? actualLatest = null;

        try
        {
            while (cursor < toEpoch)
            {
                if (ct.IsCancellationRequested)
                    return new DownloadResult
                    {
                        CandlesFetched = fetched, CandlesExpected = expectedTotal,
                        ActualEarliestEpoch = actualEarliest, ActualLatestEpoch = actualLatest,
                        WasCancelled = true
                    };

                long chunkEnd = Math.Min(cursor + chunkSpanSeconds, toEpoch);

                var request = new
                {
                    ticks_history = symbol,
                    adjust_start_time = 1,
                    granularity = granularitySeconds,
                    style = "candles",
                    start = cursor,
                    end = chunkEnd,
                };

                var response = await SendRequestAsync(request, ct);

                if (response.RootElement.TryGetProperty("error", out var err))
                {
                    return new DownloadResult
                    {
                        CandlesFetched = fetched, CandlesExpected = expectedTotal,
                        ActualEarliestEpoch = actualEarliest, ActualLatestEpoch = actualLatest,
                        Error = err.TryGetProperty("message", out var m) ? m.GetString() : "Deriv API error"
                    };
                }

                var candles = ParseCandlesResponse(response, symbol, granularitySeconds);

                if (candles.Count == 0)
                {
                    // No data in this window (e.g. before the symbol existed, or a genuine
                    // gap). Bail out after a few empty chunks in a row instead of spinning.
                    consecutiveEmptyChunks++;
                    cursor = chunkEnd;
                    if (consecutiveEmptyChunks >= 3)
                        break;
                    continue;
                }

                consecutiveEmptyChunks = 0;
                store.UpsertCandles(candles);
                fetched += candles.Count;
                actualEarliest ??= candles.Min(c => c.EpochUtc);
                actualLatest = candles.Max(c => c.EpochUtc);
                progress?.Report((fetched, expectedTotal));

                cursor = chunkEnd;
            }
        }
        catch (OperationCanceledException)
        {
            return new DownloadResult
            {
                CandlesFetched = fetched, CandlesExpected = expectedTotal,
                ActualEarliestEpoch = actualEarliest, ActualLatestEpoch = actualLatest,
                WasCancelled = true
            };
        }
        catch (Exception ex)
        {
            return new DownloadResult
            {
                CandlesFetched = fetched, CandlesExpected = expectedTotal,
                ActualEarliestEpoch = actualEarliest, ActualLatestEpoch = actualLatest,
                Error = ex.Message
            };
        }

        return new DownloadResult
        {
            CandlesFetched = fetched,
            CandlesExpected = expectedTotal,
            ActualEarliestEpoch = actualEarliest,
            ActualLatestEpoch = actualLatest,
        };
    }

    /// <summary>
    /// Downloads everything Deriv has, walking backward from "latest" until it stops
    /// returning candles. No fixed size assumed up front — this just takes however
    /// much history actually exists.
    /// </summary>
    public async Task DownloadFullHistoryAsync(
        string symbol,
        int granularitySeconds,
        CandleStore store,
        Action<int>? onBatch = null,
        CancellationToken ct = default)
    {
        long? cursorEnd = store.GetOldestEpoch(symbol, granularitySeconds);
        // If nothing stored yet, start from "latest" and walk backward.
        string endParam = cursorEnd.HasValue ? (cursorEnd.Value - 1).ToString() : "latest";
        int totalFetched = 0;

        while (!ct.IsCancellationRequested)
        {
            var request = new
            {
                ticks_history = symbol,
                adjust_start_time = 1,
                count = MaxCandlesPerRequest,
                end = endParam,
                start = 1,
                style = "candles",
                granularity = granularitySeconds,
            };

            var response = await SendRequestAsync(request, ct);
            var candles = ParseCandlesResponse(response, symbol, granularitySeconds);

            if (candles.Count == 0)
                break; // no more history available

            store.UpsertCandles(candles);
            totalFetched += candles.Count;
            onBatch?.Invoke(totalFetched);

            var oldest = candles.Min(c => c.EpochUtc);
            endParam = (oldest - 1).ToString();

            if (candles.Count < MaxCandlesPerRequest)
                break; // reached the beginning of available history
        }

        // Also pull anything newer than what's stored, to catch up to "latest".
        await CatchUpToLatestAsync(symbol, granularitySeconds, store, ct);
    }

    private async Task CatchUpToLatestAsync(
        string symbol, int granularitySeconds, CandleStore store, CancellationToken ct)
    {
        var request = new
        {
            ticks_history = symbol,
            adjust_start_time = 1,
            count = MaxCandlesPerRequest,
            end = "latest",
            start = 1,
            style = "candles",
            granularity = granularitySeconds,
        };

        var response = await SendRequestAsync(request, ct);
        var candles = ParseCandlesResponse(response, symbol, granularitySeconds);
        if (candles.Count > 0)
            store.UpsertCandles(candles);
    }

    /// <summary>
    /// Subscribes to live 1-candle-at-a-time updates for a symbol/timeframe.
    /// Fires LiveCandleUpdated as new candles form.
    /// </summary>
    public async Task SubscribeLiveAsync(string symbol, int granularitySeconds, CancellationToken ct = default)
    {
        var request = new
        {
            ticks_history = symbol,
            adjust_start_time = 1,
            count = 1,
            end = "latest",
            start = 1,
            style = "candles",
            granularity = granularitySeconds,
            subscribe = 1,
        };

        await SendRawAsync(request, ct);
        // Live updates arrive via the receive loop and are dispatched in ReceiveLoopAsync.
    }

    private async Task<JsonDocument> SendRequestAsync(object request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonDocument>();
        void Handler(JsonDocument doc) => tcs.TrySetResult(doc);

        _pendingOneShot = Handler;
        await SendRawAsync(request, ct);

        using (ct.Register(() => tcs.TrySetCanceled()))
        {
            return await tcs.Task;
        }
    }

    private Action<JsonDocument>? _pendingOneShot;

    private async Task SendRawAsync(object request, CancellationToken ct)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
            throw new InvalidOperationException("Not connected to Deriv API.");

        var json = JsonSerializer.Serialize(request);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var messageBuilder = new StringBuilder();

        try
        {
            while (_socket is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
            {
                messageBuilder.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, ct);
                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                var doc = JsonDocument.Parse(messageBuilder.ToString());
                HandleMessage(doc);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(ex.Message);
        }
    }

    private void HandleMessage(JsonDocument doc)
    {
        var root = doc.RootElement;

        // Live streamed OHLC update
        if (root.TryGetProperty("ohlc", out var ohlc))
        {
            var symbol = ohlc.GetProperty("symbol").GetString() ?? "";
            var granularity = ohlc.TryGetProperty("granularity", out var g) ? g.GetInt32() : Timeframes.Default;
            var candle = new Candle
            {
                Symbol = symbol,
                GranularitySeconds = granularity,
                EpochUtc = ohlc.GetProperty("epoch").GetInt64(),
                Open = double.Parse(ohlc.GetProperty("open").GetString() ?? "0"),
                High = double.Parse(ohlc.GetProperty("high").GetString() ?? "0"),
                Low = double.Parse(ohlc.GetProperty("low").GetString() ?? "0"),
                Close = double.Parse(ohlc.GetProperty("close").GetString() ?? "0"),
            };
            LiveCandleUpdated?.Invoke(candle);
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            ConnectionError?.Invoke(error.GetProperty("message").GetString() ?? "Unknown Deriv API error");
        }

        // One-shot request/response (history fetch)
        var handler = _pendingOneShot;
        _pendingOneShot = null;
        handler?.Invoke(doc);
    }

    private static List<Candle> ParseCandlesResponse(JsonDocument doc, string symbol, int granularitySeconds)
    {
        var list = new List<Candle>();
        if (!doc.RootElement.TryGetProperty("candles", out var candlesArray))
            return list;

        foreach (var item in candlesArray.EnumerateArray())
        {
            list.Add(new Candle
            {
                Symbol = symbol,
                GranularitySeconds = granularitySeconds,
                EpochUtc = item.GetProperty("epoch").GetInt64(),
                Open = ParseNumeric(item.GetProperty("open")),
                High = ParseNumeric(item.GetProperty("high")),
                Low = ParseNumeric(item.GetProperty("low")),
                Close = ParseNumeric(item.GetProperty("close")),
            });
        }
        return list;
    }

    private static double ParseNumeric(JsonElement el) =>
        el.ValueKind == JsonValueKind.String ? double.Parse(el.GetString()!) : el.GetDouble();

    public async ValueTask DisposeAsync()
    {
        _receiveLoopCts?.Cancel();
        if (_socket is { State: WebSocketState.Open })
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
        }
        _socket?.Dispose();
    }
}
