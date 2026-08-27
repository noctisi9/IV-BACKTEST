using System;
using System.Collections.Generic;
using System.Windows.Threading;
using BoomCrashBacktester.Models;

namespace BoomCrashBacktester.Services;

public enum ReplayState { Stopped, Playing, Paused }

/// <summary>
/// Drives TradingView-style bar replay over a fixed list of candles:
/// scrub to any point, play forward automatically, pause, and step
/// one candle at a time in either direction.
/// </summary>
public sealed class ReplayEngine
{
    private List<Candle> _candles = new();
    private readonly DispatcherTimer _timer;

    /// <summary>Index of the last visible candle (inclusive). -1 means nothing loaded.</summary>
    public int CursorIndex { get; private set; } = -1;

    public ReplayState State { get; private set; } = ReplayState.Stopped;

    /// <summary>Candles per second while auto-playing. Adjustable for speed control.</summary>
    public double PlaybackSpeed { get; set; } = 2.0;

    /// <summary>Raised whenever the visible candle set changes (scrub, step, or play tick).</summary>
    public event Action<IReadOnlyList<Candle>>? VisibleCandlesChanged;

    public ReplayEngine()
    {
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) => StepForward();
        UpdateTimerInterval();
    }

    public void LoadCandles(List<Candle> candles)
    {
        _candles = candles;
        CursorIndex = _candles.Count > 0 ? _candles.Count - 1 : -1;
        State = ReplayState.Stopped;
        _timer.Stop();
        RaiseVisibleChanged();
    }

    public int TotalCandles => _candles.Count;

    /// <summary>Jump the cursor to a specific index (0-based) and show history up to it.</summary>
    public void ScrubTo(int index)
    {
        if (_candles.Count == 0) return;
        CursorIndex = Math.Clamp(index, 0, _candles.Count - 1);
        RaiseVisibleChanged();
    }

    /// <summary>Jump the cursor to a specific point in time (nearest candle at or before it).</summary>
    public void ScrubToTime(DateTime utcTime)
    {
        if (_candles.Count == 0) return;
        var targetEpoch = new DateTimeOffset(utcTime, TimeSpan.Zero).ToUnixTimeSeconds();
        int idx = _candles.FindLastIndex(c => c.EpochUtc <= targetEpoch);
        ScrubTo(idx < 0 ? 0 : idx);
    }

    public void Play()
    {
        if (_candles.Count == 0 || CursorIndex >= _candles.Count - 1) return;
        State = ReplayState.Playing;
        UpdateTimerInterval();
        _timer.Start();
    }

    public void Pause()
    {
        State = ReplayState.Paused;
        _timer.Stop();
    }

    public void Stop()
    {
        State = ReplayState.Stopped;
        _timer.Stop();
        CursorIndex = _candles.Count > 0 ? _candles.Count - 1 : -1;
        RaiseVisibleChanged();
    }

    public void StepForward()
    {
        if (_candles.Count == 0) return;
        if (CursorIndex >= _candles.Count - 1)
        {
            Pause();
            return;
        }
        CursorIndex++;
        RaiseVisibleChanged();
    }

    public void StepBackward()
    {
        if (_candles.Count == 0) return;
        if (CursorIndex <= 0) return;
        CursorIndex--;
        RaiseVisibleChanged();
    }

    public void SetPlaybackSpeed(double candlesPerSecond)
    {
        PlaybackSpeed = Math.Max(0.1, candlesPerSecond);
        UpdateTimerInterval();
    }

    private void UpdateTimerInterval()
    {
        var intervalMs = Math.Max(20, 1000.0 / PlaybackSpeed);
        _timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
    }

    private void RaiseVisibleChanged()
    {
        if (CursorIndex < 0) return;
        var visible = _candles.GetRange(0, CursorIndex + 1);
        VisibleCandlesChanged?.Invoke(visible);
    }
}
