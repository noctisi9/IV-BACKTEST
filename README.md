# Boom/Crash Backtester

A TradingView/MT5-style chart replay dashboard for **Boom 1000** and **Crash 1000**
(Deriv synthetic indices). Download full history once, then scrub, play, and
step candle-by-candle through the past like TradingView's bar replay.

## Stack

- .NET 8, WPF (Windows desktop)
- SkiaSharp — custom candlestick renderer (white = bullish, red = bearish)
- SQLite (`Microsoft.Data.Sqlite`) — local candle cache, stored at
  `%LOCALAPPDATA%\BoomCrashBacktester\candles.db`
- Deriv public WebSocket API (`wss://ws.derivws.com/websockets/v3`) for
  historical + live candles

## Build (Windows, PowerShell)

```powershell
dotnet restore
dotnet build -c Debug
dotnet run
```

To produce a standalone EXE:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## How it works — offline-first

This is built for offline use. The network is only ever touched when you
explicitly click **Fetch** — everything else (opening the app, switching
asset/timeframe, replay, scrubbing) works purely off the local SQLite cache
with zero network dependency.

1. On startup, the app loads whatever is already cached locally and shows it
   immediately — no connection attempt, no waiting.
2. Clicking **Fetch** connects to Deriv and walks backward in 5000-candle
   chunks (`DerivDataService.DownloadFullHistoryAsync`) until Deriv stops
   returning older candles. That's genuinely as far back as the public API
   goes for that symbol/timeframe — there's no fixed assumption baked in
   about how many years that turns out to be, and it may well be closer to
   weeks/months than years. Whatever comes back is stored permanently, so
   you only fetch it once.
3. If Fetch fails (offline, API hiccup, etc.), whatever's already cached
   stays loaded and fully usable — a failed fetch never blocks the app.
4. `ReplayEngine` holds a cursor over the stored candle list. Scrubbing,
   stepping, and auto-play all just move that cursor and re-slice the visible
   window — purely local, no network involved.
5. `CandleChartControl` (SkiaSharp) redraws only the visible window (last 150
   candles) each time the cursor moves, so playback stays smooth at any speed.

## Known risk: shared Deriv app_id

This currently uses the shared public `app_id=1089` (see `DerivDataService.AppId`).
Bulk historical downloads are heavier than iTRADE's live-only usage and may hit
rate limits or drop connections more often. If that happens, register a personal
app_id at https://developers.deriv.com/ and set `DerivDataService.AppId` — no
other code changes needed.

## CI

`ci-templates`'s reusable workflow is Flutter-specific (JDK/Gradle setup), which
doesn't apply here, so this repo uses a standalone `.github/workflows/build.yml`
instead: lint build on every push/PR, self-contained EXE publish on `v*` tags or
manual dispatch — same trigger policy as the other projects, just a plain
workflow rather than a caller to the central repo. Worth adding a `.NET` reusable
workflow to `ci-templates` later if more desktop apps follow this stack.

## Not yet built

- Persisted watchlist/multi-timeframe sync
- Drawing tools (trendlines, etc.)
- Personal Deriv app_id config UI (currently a code constant)
