using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using BoomCrashBacktester.Models;
using BoomCrashBacktester.Services;

namespace BoomCrashBacktester;

public partial class FetchDialog : Window
{
    private readonly DerivDataService _deriv;
    private readonly CandleStore _store;
    private readonly string _symbol;
    private readonly int _granularitySeconds;
    private readonly string _timeframeLabel;

    private CancellationTokenSource? _cts;
    private bool _isDownloading;

    /// <summary>True if a download completed (fully or partially) and the caller should reload the chart.</summary>
    public bool DataChanged { get; private set; }

    // (Label, days-back). "Custom" and "All available" are handled specially.
    private static readonly (string Label, int Days)[] Presets =
    {
        ("Today", 1),
        ("Last 2 days", 2),
        ("Last 7 days", 7),
        ("Last 14 days", 14),
        ("Last 30 days", 30),
        ("Custom (days)", -1),
        ("All available history", -2),
    };

    public FetchDialog(DerivDataService deriv, CandleStore store, string symbol, int granularitySeconds, string timeframeLabel)
    {
        InitializeComponent();
        _deriv = deriv;
        _store = store;
        _symbol = symbol;
        _granularitySeconds = granularitySeconds;
        _timeframeLabel = timeframeLabel;

        ContextText.Text = $"{Symbols.DisplayName(symbol)} — {timeframeLabel} candles";
        RangeCombo.ItemsSource = Presets.Select(p => p.Label).ToList();
        RangeCombo.SelectedIndex = 0;
        UpdateEstimate();
    }

    private void RangeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CustomDaysPanel.Visibility = SelectedPreset().Days == -1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateEstimate();
    }

    private void CustomDaysBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateEstimate();

    private (string Label, int Days) SelectedPreset() =>
        RangeCombo.SelectedIndex >= 0 ? Presets[RangeCombo.SelectedIndex] : Presets[0];

    private int GetRequestedDays()
    {
        var preset = SelectedPreset();
        if (preset.Days == -1)
            return int.TryParse(CustomDaysBox.Text, out var d) && d > 0 ? d : 1;
        return preset.Days;
    }

    private void UpdateEstimate()
    {
        var preset = SelectedPreset();

        if (preset.Days == -2)
        {
            EstimateText.Text = "Walks backward until Deriv stops returning older candles. " +
                                 "Total size depends on however much history Deriv actually has — " +
                                 "you'll see the running count as it downloads.";
            return;
        }

        int days = GetRequestedDays();
        long candleCount = (long)days * 86400 / _granularitySeconds;
        double estKb = candleCount * 0.5; // ~500 bytes/candle over the wire (JSON), rough estimate

        string sizeText = estKb > 1024
            ? $"{estKb / 1024:F1} MB"
            : $"{estKb:F0} KB";

        EstimateText.Text = $"~{candleCount:N0} candles, roughly {sizeText} to download. " +
                             "If Deriv has less than this available for the period, you'll get " +
                             "whatever it actually has instead of an error.";
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;
        _isDownloading = true;
        RangeCombo.IsEnabled = false;
        CustomDaysBox.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        ProgressBar.Value = 0;
        ProgressBar.IsIndeterminate = false;
        _cts = new CancellationTokenSource();

        try
        {
            if (!_deriv.IsConnected)
            {
                ProgressStatusText.Text = "Connecting...";
                await _deriv.ConnectAsync(_cts.Token);
            }

            var preset = SelectedPreset();

            if (preset.Days == -2)
            {
                ProgressBar.IsIndeterminate = true;
                ProgressStatusText.Text = "Downloading...";
                await _deriv.DownloadFullHistoryAsync(_symbol, _granularitySeconds, _store,
                    onBatch: fetched => Dispatcher.Invoke(() =>
                        ProgressStatusText.Text = $"{fetched:N0} candles downloaded so far..."),
                    ct: _cts.Token);
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
                ProgressStatusText.Text = "Done.";
                DataChanged = true;
            }
            else
            {
                int days = GetRequestedDays();
                var toUtc = DateTime.UtcNow;
                var fromUtc = toUtc.AddDays(-days);

                var progress = new Progress<(int fetched, int expectedTotal)>(p =>
                {
                    int pct = p.expectedTotal > 0 ? (int)Math.Min(100, 100.0 * p.fetched / p.expectedTotal) : 0;
                    ProgressBar.Value = pct;
                    ProgressStatusText.Text = $"{p.fetched:N0} / ~{p.expectedTotal:N0} candles";
                });

                var result = await _deriv.DownloadRangeAsync(
                    _symbol, _granularitySeconds, fromUtc, toUtc, _store, progress, _cts.Token);

                if (result.Error is not null)
                {
                    ProgressStatusText.Text = $"Stopped: {result.Error}. " +
                        (result.CandlesFetched > 0
                            ? $"{result.CandlesFetched:N0} candles were saved before the error — nothing lost."
                            : "Nothing was downloaded; your existing local data is untouched.");
                    DataChanged = result.CandlesFetched > 0;
                }
                else if (result.WasCancelled)
                {
                    ProgressStatusText.Text = $"Cancelled — {result.CandlesFetched:N0} candles saved so far.";
                    DataChanged = result.CandlesFetched > 0;
                }
                else if (result.GotLessThanRequested)
                {
                    ProgressBar.Value = 100;
                    ProgressStatusText.Text = $"Got {result.CandlesFetched:N0} candles " +
                        "(Deriv didn't have the full period you asked for — this is all that's available for that range).";
                    DataChanged = true;
                }
                else
                {
                    ProgressBar.Value = 100;
                    ProgressStatusText.Text = $"Done — {result.CandlesFetched:N0} candles saved.";
                    DataChanged = true;
                }
            }
        }
        catch (Exception ex)
        {
            ProgressStatusText.Text = $"Failed: {ex.Message}. Your existing local data is untouched.";
        }
        finally
        {
            _isDownloading = false;
            RangeCombo.IsEnabled = true;
            CustomDaysBox.IsEnabled = true;
            DownloadButton.IsEnabled = true;
            CancelButton.Content = "Close";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            _cts?.Cancel();
            return;
        }
        Close();
    }
}
