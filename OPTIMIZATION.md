# Optimization Guide

## Templates included

`templates/Strategy/EthVpVwapStrategy/` ships two strategy templates as
starting baselines:

- `MES_baseline.xml` - Micro E-mini S&P, $1.25 / tick, 4-tick bin
- `MNQ_baseline.xml` - Micro E-mini Nasdaq, $0.50 / tick, 2-tick bin

Both use the same generic propfirm rules (`$50K start, $1250 daily loss,
$2500 overall, trailing-high-EOD anchor`) and the same regime / entry
thresholds.

## Install

Copy the `templates/Strategy/EthVpVwapStrategy/` folder into:

```
Documents\NinjaTrader 8\templates\Strategy\
```

Final layout:

```
Documents\NinjaTrader 8\templates\Strategy\EthVpVwapStrategy\
    MES_baseline.xml
    MNQ_baseline.xml
```

In Strategy Analyzer, after picking `EthVpVwapStrategy` from the Strategy
dropdown, click the small dropdown arrow next to **Template** (top-right
of the strategy panel) and pick `MES_baseline` or `MNQ_baseline`. Run a
single backtest first to confirm the template loaded cleanly.

## Optimizer ranges

NT8 keeps optimizer ranges in the Analyzer UI rather than the template
file. Configure them per-run by clicking the gear icon next to each
parameter row in the Analyzer's Parameters pane.

### Pass 1 - coarse (entry quality)

Goal: find which entry style + risk geometry produces a positive curve.
Roughly 2,400 iterations.

| Parameter           | Min  | Max  | Step  | Notes                                   |
|---------------------|------|------|-------|-----------------------------------------|
| `MinRMultiple`      | 0.5  | 1.5  | 0.25  | TP-vs-SL floor                          |
| `MaxSlTicks`        | 16   | 64   | 8     | SL cap. Lower = entries closer to level |
| `EnableBalanceSetups` | -  | -    | -     | Sweep both true/false                   |
| `EnableTrendSetups`   | -  | -    | -     | Sweep both true/false                   |
| `CooldownMinutes`   | 10   | 40   | 10    | Time after exit before next entry       |

Other parameters: leave at template default.

**Optimizer**: Default (Exhaustive).
**Fitness**: `Profit Factor` for Pass 1 (filters out random profitable
runs that lost on most trades). Sort the result list by **Sharpe Ratio**
descending afterward to pick top candidates.

Discard any row with fewer than 30 trades over your test window.

### Pass 2 - fine (regime thresholds)

Take the top 3 rows from Pass 1 and lock those values. Then sweep:

| Parameter                      | Min  | Max  | Step  |
|--------------------------------|------|------|-------|
| `BalancePocVwapFraction`       | 0.15 | 0.40 | 0.05  |
| `TrendPocVwapFraction`         | 0.45 | 0.80 | 0.05  |
| `MinTimeInVaFraction`          | 0.40 | 0.75 | 0.05  |
| `TrendVwapSlopeMinTicksPerMin` | 0.10 | 0.60 | 0.10  |
| `WarmupMinutes`                | 30   | 90   | 15    |

**Optimizer**: Genetic (faster across this many combinations).
**Fitness**: `Sharpe Ratio`.

### Pass 3 - sanity (out-of-sample)

Pick exactly **one** parameter set from Pass 2 (highest Sharpe with >=50
trades). Run `Walk Forward` mode in Strategy Analyzer:

- IS = 60 days, OOS = 20 days, step = 20 days, 6 windows.
- Anchored = false (rolling).

If average OOS profit factor > 1.2 across all windows, the parameter set
is robust enough for paper trading. If it collapses on OOS, the IS fit
was overfit - go back to Pass 1 and widen ranges.

## Time period and bar settings

- **Bars period**: `1 Minute` - matches the strategy's design cadence.
- **Date range**: minimum 60 days for Pass 1 stability, ideally 6-12
  months for Pass 3 walk-forward.
- **Order fill resolution**: leave on `Standard`. The added 1-tick series
  inside the strategy already gives tick-accurate fills.
- **Slippage**: 1 tick.
- **Commission**: set to your broker's actual round-trip - results
  ignoring commissions are useless for propfirm sizing.

## Parameters to leave fixed

These should NOT be in any optimizer pass:

- `StartingBalance`, `DailyLossLimit`, `MaxOverallLoss`, `ScalingTiers`,
  `MinSlTicks`, `SafetyBuffer` - these are propfirm rules, not strategy
  knobs. Optimizing them produces curve-fits that won't survive on a
  real account.
- `MasterSymbol`, `TickValueOverride`, `BinTicks`, ETH session times -
  per-instrument constants.
- `SkipFirstMinutes`, `MaxSpreadTicks` - microstructure filters that
  shouldn't drift.

## Common pitfalls

- **Tick data missing for the date range** - the volume profile silently
  shows zero volume, no setups fire. Download tick data first.
- **Slippage at 0 ticks** - Pass 1 will look amazing, Pass 3 will be a
  cliff. Always run with realistic slippage.
- **Optimizer says "no results"** - the strategy template was reset to
  factory defaults after picking it from a different optimizer pass.
  Re-load the template before each pass.
