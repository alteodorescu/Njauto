# NjAuto - ETH Anchored VP/VWAP Strategy for NinjaTrader 8

Self-aware propfirm trading automation. Trades a single futures instrument
using only ETH-session anchored Volume Profile (VAH/VAL/POC) and ETH-session
anchored VWAP. Sizes at the current scaling-tier max contracts and derives
stop distance from the remaining daily-loss room.

## Layout

```
Strategies/
  EthVpVwapStrategy.cs          # main NinjaScript Strategy
AddOns/NjAuto/
  StrategyTypes.cs              # enums & DTOs
  SessionClock.cs               # per-instrument ETH window resolver (DST-safe)
  AnchoredVolumeProfile.cs      # VAH/VAL/POC engine
  AnchoredVwap.cs               # VWAP + 1sigma/2sigma bands + slope
  RegimeClassifier.cs           # balance vs trend day classification
  ScalingTable.cs               # propfirm scaling tier lookup
  PositionSizer.cs              # contracts + SL ticks computation
  PropfirmRules.cs              # daily loss / overall DD rule engine
  GuardState.cs                 # Armed -> Warning -> SoftHalt -> Locked latch
```

## Installation

1. Open Windows Explorer at `Documents\NinjaTrader 8\bin\Custom\`.
2. Copy `Strategies\EthVpVwapStrategy.cs` into `bin\Custom\Strategies\`.
3. Copy the `AddOns\NjAuto\` folder (with all .cs files) into
   `bin\Custom\AddOns\NjAuto\`.
4. Open the NinjaScript Editor in NT8 and press `F5` to compile. Resolve any
   reference errors (there should be none on a stock NT8 install).
5. Open a chart on the instrument you want to trade. ETH session must be
   visible in the chart's session template (use the built-in "CME US Index
   Futures ETH" template or equivalent for non-index futures).
6. Right-click the chart -> Strategies -> add `EthVpVwapStrategy`.

## Configuration

Properties are grouped on the strategy panel:

1. **Instrument** - master symbol, tick value override, profile bin size,
   ETH open/close hour in ET, maintenance break length.
2. **Profile/VWAP** - value-area %, VWAP slope lookback minutes.
3. **Regime** - warmup minutes, balance/trend thresholds.
4. **Filters** - skip-first-N-min, cooldown after exit, max spread,
   per-regime enable toggles.
5. **Propfirm** - starting balance, daily loss limit, daily loss anchor,
   max overall loss, overall loss anchor (StartingBalance /
   TrailingHighEodBalance / TrailingHighIntradayEquity), Apex-style profit
   lock threshold, warning %.
6. **Sizing** - scaling tiers as `minProfit:contracts;...`, safety buffer,
   min/max SL ticks, min R multiple.
7. **Diagnostics** - journal CSV directory, verbose log toggle.

### Scaling tiers format

Semicolon-separated `minNetProfit:maxContracts` rows. Net profit is computed
as `currentEquity - startingBalance`. Lookup returns the highest tier whose
threshold is `<= netProfit`.

Example for an Apex-style $50K eval:
```
0:1;500:2;1500:3;3000:5;5000:7;7500:10
```

## Strategy logic (summary)

- **Anchor**: ETH session start per instrument (default 18:00 ET prior day
  for CME futures), recomputed daily by `SessionClock`.
- **Volume Profile**: built tick-by-tick from a 1-tick added series. POC is
  the highest-volume bin; VAH/VAL expand symmetrically until cumulative
  volume covers `valueAreaPct` (default 70%).
- **VWAP**: cumulative volume-weighted typical price from session start;
  sigma derived from volume-weighted second moment.
- **Regime**: balance if `|VWAP-POC|` is small, VWAP slope flat, and
  >=60% of bars closed inside the value area; trend if any of those
  conditions break the other way.
- **Entries** (one open position at a time):
  - Balance: VAL fade long, VAH fade short, VWAP reclaim, POC rejection.
  - Trend: VAH/VAL acceptance + retest, VWAP +/-2sigma band fade.
- **Sizing**: `contracts = scaling.lookup(netProfit)`,
  `slTicks = floor((dailyLossRoom - safetyBuffer) / (contracts * tickValue))`,
  clamped to `[minSlTicks, maxSlTicks]`. Skip the trade if room too tight.
- **TP**: structural target (POC, VA edge, VWAP, +/-2sigma) with a min-R floor.
- **Guard**: SoftHalt at daily limit -> no new entries, open trade left to
  run on its SL/TP. Locked at overall DD breach.

## Verification

1. **Compile**: F5 in NinjaScript Editor; no errors.
2. **VP/VWAP cross-check**: enable a third-party "Anchored VWAP" and any
   "Volume Profile" indicator on the same ETH session anchor. Compare
   `vwap` and `Poc/Vah/Val` printed to Output - must match within one bin.
3. **SessionClock**: enable verbose log; verify session-start prints fire
   at the expected ET time across DST transitions.
4. **Sizing math**: in Strategy Analyzer with mocked daily loss limit, force
   a synthetic trade and assert
   `dec.SlTicks * dec.Contracts * tickValue ~= dailyLossLimit - safetyBuffer`.
5. **Guard**: simulate a losing day in Playback Connection. Watch for
   `[Guard] Armed -> Warning -> SoftHalt` transitions; confirm no further
   entries fire.
6. **Paper trade**: at least one full ETH session per asset class on Sim101
   before going to a propfirm eval.

## Caveats

- The strategy assumes 1-tick data is available for the instrument; without
  it the volume profile will be coarse (per-minute aggregated).
- Live spread is read via `GetCurrentBid()` / `GetCurrentAsk()`, which are
  unavailable in some backtest modes. The spread filter falls back to a
  1-tick assumed spread there.
- News blackout (high-impact data releases) is **not** auto-fetched. If you
  want a news filter, supply timestamps via a future input or extend
  `TryFindSetup()`.
