using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using BoomCrashBacktester.Models;

namespace BoomCrashBacktester.Rendering;

/// <summary>
/// Custom-rendered candlestick chart. White = bullish, red = bearish.
/// Draws the full chart area itself (no third-party chart control) so the
/// replay engine can repaint at any frame rate without layout overhead.
/// </summary>
public sealed class CandleChartControl : SKElement
{
    public static readonly DependencyProperty CandlesProperty =
        DependencyProperty.Register(nameof(Candles), typeof(IReadOnlyList<Candle>),
            typeof(CandleChartControl),
            new PropertyMetadata(null, OnCandlesChanged));

    public IReadOnlyList<Candle>? Candles
    {
        get => (IReadOnlyList<Candle>?)GetValue(CandlesProperty);
        set => SetValue(CandlesProperty, value);
    }

    // Theme colors: white background/bullish candles, red bearish candles, black text/grid.
    private static readonly SKColor BackgroundColor = SKColors.White;
    private static readonly SKColor GridColor = new(0xE0, 0xE0, 0xE0);
    private static readonly SKColor TextColor = new(0x20, 0x20, 0x20);
    private static readonly SKColor BullColor = SKColors.White;
    private static readonly SKColor BullBorder = new(0x20, 0x20, 0x20);
    private static readonly SKColor BearColor = new(0xD8, 0x2B, 0x2B); // red

    private const int MaxVisibleCandles = 150; // right-hand window of the chart
    private const float PriceAxisWidth = 70f;
    private const float TimeAxisHeight = 28f;

    private static void OnCandlesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CandleChartControl)d).InvalidateVisual();

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(BackgroundColor);

        var candles = Candles;
        if (candles is null || candles.Count == 0)
            return;

        var visible = candles.Count > MaxVisibleCandles
            ? candles.Skip(candles.Count - MaxVisibleCandles).ToList()
            : candles.ToList();

        float width = e.Info.Width;
        float height = e.Info.Height;
        float chartWidth = width - PriceAxisWidth;
        float chartHeight = height - TimeAxisHeight;

        double min = visible.Min(c => c.Low);
        double max = visible.Max(c => c.High);
        double padding = (max - min) * 0.08;
        if (padding <= 0) padding = max * 0.001 + 0.01;
        min -= padding;
        max += padding;

        float PriceToY(double price) =>
            (float)(chartHeight - (price - min) / (max - min) * chartHeight);

        DrawGrid(canvas, chartWidth, chartHeight, width, min, max, PriceToY);
        DrawCandles(canvas, visible, chartWidth, chartHeight, PriceToY);
        DrawTimeAxis(canvas, visible, chartWidth, chartHeight, height);
    }

    private void DrawGrid(SKCanvas canvas, float chartWidth, float chartHeight, float fullWidth,
        double min, double max, Func<double, float> priceToY)
    {
        using var gridPaint = new SKPaint { Color = GridColor, StrokeWidth = 1 };
        using var textPaint = new SKPaint { Color = TextColor, TextSize = 12, IsAntialias = true };

        const int lines = 6;
        for (int i = 0; i <= lines; i++)
        {
            double price = min + (max - min) * i / lines;
            float y = priceToY(price);
            canvas.DrawLine(0, y, chartWidth, y, gridPaint);
            canvas.DrawText(price.ToString("F2"), chartWidth + 6, y + 4, textPaint);
        }
    }

    private void DrawCandles(SKCanvas canvas, List<Candle> visible, float chartWidth, float chartHeight,
        Func<double, float> priceToY)
    {
        float slot = chartWidth / visible.Count;
        float bodyWidth = Math.Max(2f, slot * 0.6f);

        using var wickPaint = new SKPaint { StrokeWidth = 1, IsAntialias = true };
        using var bullFill = new SKPaint { Color = BullColor, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var bullBorder = new SKPaint { Color = BullBorder, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        using var bearFill = new SKPaint { Color = BearColor, Style = SKPaintStyle.Fill, IsAntialias = true };

        for (int i = 0; i < visible.Count; i++)
        {
            var c = visible[i];
            float xCenter = i * slot + slot / 2f;
            float yHigh = priceToY(c.High);
            float yLow = priceToY(c.Low);
            float yOpen = priceToY(c.Open);
            float yClose = priceToY(c.Close);

            bool bull = c.IsBullish;
            var wickColor = bull ? BullBorder : BearColor;
            wickPaint.Color = wickColor;
            canvas.DrawLine(xCenter, yHigh, xCenter, yLow, wickPaint);

            float top = Math.Min(yOpen, yClose);
            float bottom = Math.Max(yOpen, yClose);
            if (bottom - top < 1f) bottom = top + 1f; // doji visibility

            var rect = new SKRect(xCenter - bodyWidth / 2, top, xCenter + bodyWidth / 2, bottom);
            if (bull)
            {
                canvas.DrawRect(rect, bullFill);
                canvas.DrawRect(rect, bullBorder);
            }
            else
            {
                canvas.DrawRect(rect, bearFill);
            }
        }
    }

    private void DrawTimeAxis(SKCanvas canvas, List<Candle> visible, float chartWidth, float chartHeight, float fullHeight)
    {
        using var textPaint = new SKPaint { Color = TextColor, TextSize = 11, IsAntialias = true };
        float slot = chartWidth / visible.Count;

        int labelEvery = Math.Max(1, visible.Count / 8);
        for (int i = 0; i < visible.Count; i += labelEvery)
        {
            var c = visible[i];
            float x = i * slot + slot / 2f;
            var label = c.OpenTimeUtc.ToLocalTime().ToString("HH:mm");
            canvas.DrawText(label, x - 15, fullHeight - 8, textPaint);
        }
    }
}
