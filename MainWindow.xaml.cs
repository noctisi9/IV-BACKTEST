using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BoomCrashBacktester.Models;
using BoomCrashBacktester.Services;

namespace BoomCrashBacktester;

public partial class MainWindow : Window
{
    private readonly CandleStore _store = new();
    private readonly DerivDataService _deriv = new();
    private readonly ReplayEngine _replay = new();

    private string _currentSymbol = Symbols.Boom1000;
    private int _currentGranularity = Timeframes.Default;
    private bool _isUpdatingScrubFromEngine;

    public MainWindow()
    {
        InitializeComponent();

        AssetCombo.ItemsSource = Symbols.All.Select(Symbols.DisplayName).ToList();
        AssetCombo.SelectedIndex = 0;

        TimeframeCombo.ItemsSource = Timeframes.All.Select(t => t.Label).ToList();
        TimeframeCombo.SelectedItem = "1m";

        _replay.VisibleCandlesChanged += OnVisibleCandlesChanged;
        _deriv.ConnectionError += msg => Dispatcher.Invoke(() => DownloadStatusText.Text = $"Error: {msg}");

        Loaded += (_, _) => InitializeOffline();
        Closed += async (_, _) => await _deriv.DisposeAsync();
    }

    /// <summary>
    /// App-open behavior: load whatever is already stored locally and make it
    /// fully usable immediately, with zero dependency on network availability.
    /// Fetching/refreshing is a separate, explicit action (see RefreshButton_Click).
    /// </summary>
    private void InitializeOffline()
    {
        var cached = _store.LoadCandles(_currentSymbol, _currentGranularity);
        if (cached.Count > 0)
        {
            LoadIntoReplay(cached);
            DownloadStatusText.Text = $"{cached.Count} candles (offline / local cache)";
        }
        else
        {
            DownloadStatusText.Text = "No local data yet — click Fetch to download";
        }
    }

    private void LoadIntoReplay(List<Candle> candles)
    {
        _replay.LoadCandles(candles);
        ScrubSlider.Maximum = Math.Max(0, _replay.TotalCandles - 1);
        ScrubSlider.Value = _replay.TotalCandles - 1;
    }

    private void OnVisibleCandlesChanged(IReadOnlyList<Candle> visible)
    {
        Dispatcher.Invoke(() =>
        {
            Chart.Candles = visible;
            Chart.InvalidateVisual();

            if (visible.Count > 0)
                CursorTimeText.Text = visible[^1].OpenTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

            if (!_isUpdatingScrubFromEngine)
            {
                _isUpdatingScrubFromEngine = true;
                ScrubSlider.Value = _replay.CursorIndex;
                _isUpdatingScrubFromEngine = false;
            }

            PlayPauseButton.Content = _replay.State == ReplayState.Playing ? "⏸" : "▶";
        });
    }

    private void AssetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AssetCombo.SelectedIndex < 0) return;
        _currentSymbol = Symbols.All[AssetCombo.SelectedIndex];
        InitializeOffline();
    }

    private void TimeframeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimeframeCombo.SelectedItem is not string label) return;
        var match = Timeframes.All.FirstOrDefault(t => t.Label == label);
        _currentGranularity = match.Seconds == 0 ? Timeframes.Default : match.Seconds;
        InitializeOffline();
    }

    private void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        var timeframeLabel = TimeframeCombo.SelectedItem as string ?? "1m";
        var dialog = new FetchDialog(_deriv, _store, _currentSymbol, _currentGranularity, timeframeLabel)
        {
            Owner = this
        };
        dialog.ShowDialog();

        if (dialog.DataChanged)
            InitializeOffline(); // reload from local cache — existing chart stays untouched until this succeeds
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_replay.State == ReplayState.Playing)
            _replay.Pause();
        else
            _replay.Play();

        PlayPauseButton.Content = _replay.State == ReplayState.Playing ? "⏸" : "▶";
    }

    private void StepBackButton_Click(object sender, RoutedEventArgs e)
    {
        _replay.Pause();
        _replay.StepBackward();
    }

    private void StepForwardButton_Click(object sender, RoutedEventArgs e)
    {
        _replay.Pause();
        _replay.StepForward();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _replay.SetPlaybackSpeed(e.NewValue);
    }

    private void ScrubSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingScrubFromEngine) return;
        _replay.Pause();
        _replay.ScrubTo((int)e.NewValue);
    }
}
