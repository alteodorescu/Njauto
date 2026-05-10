# SMC NDX100 EA → NinjaScript Port

This branch (`claude/smc-ndx100-port`) is a one-file port of
`SMC_NDX100_EA.mq5` (MetaTrader 5) to NinjaTrader 8.

Source EA: `SMC_NDX100_EA.mq5` v2.0 — Smart Money Concepts strategy
trading Supply/Demand zones, Order Blocks, Fair Value Gaps and price
action confirmations on NDX100 (M5).

## File

- `Strategies/SmcNdx100Strategy.cs` — drop into
  `Documents\NinjaTrader 8\bin\Custom\Strategies\` and press F5.

No external AddOns needed — everything is contained in the single
strategy file (matching the source EA's single-file style).

## What was ported

| MQL5 module | NinjaScript section |
|---|---|
| Risk management inputs | Group "1. Risk Management" |
| Supply / Demand zone detection (impulse-base-impulse) | `DetectSupplyDemandZones()` |
| Order Block detection | `DetectOrderBlocks()` |
| Fair Value Gap detection | `DetectFVG()` |
| Liquidity sweep detection (logged) | `CheckLiquiditySweeps()` |
| Price-action confirmation (pin / engulfing / rejection) | `ConfirmPaBullish/Bearish()` |
| Higher-timeframe (H4 EMA50) trend filter | `GetHTFTrend()` via `BarsArray[1]` |
| Swing-point market structure (HH/LL) | `GetMarketStructure()` |
| Daily loss / profit guards | `SafetyChecks()` |
| London / NY / overlap session filter | `SessionAllowed()` |
| Friday PM / Monday AM block | `TimeAllowed()` |
| Trailing stop + break-even | `ManagePositions()` |
| Rectangle visuals + info panel | `DrawZones / DrawOrderBlocks / DrawFVGs / DrawInfoPanel` |

## Key MT5 → NT8 mapping decisions

- **`_Point` → `TickSize`.** All MQL5 inputs that say "points"
  (`OB_MinSize`, `FVG_MinSize`, `SL_Buffer`, `TrailStart`, `TrailStep`,
  `BE_Trigger`, `BE_Offset`, `LSweep_Points`, `ZoneBuffer`) keep the
  same names. On NDX100 cash CFD `_Point` is typically 0.01; on NT8
  NQ futures `TickSize` is 0.25 — **a number that meant 50 points
  (0.50) on MT5 NDX100 means 50 ticks (12.50) on NT8 NQ.** Scale them
  down ~25x if porting between those two instruments.
- **`AccountInfoDouble(ACCOUNT_BALANCE)`** → `GetCurrentEquity()`,
  computed as `StartingBalance + realized + unrealized`. This works
  in both backtest (StartingBalance is configurable) and live.
- **`ArraySetAsSeries(true)`** → not needed. NT8's `Open[i] / Close[i]`
  is already `i bars ago` (0 = current). Index math from the EA carries
  over directly.
- **`iMA(_Symbol, PERIOD_H4, 50, EMA, PRICE_CLOSE)`** → secondary
  data series added in `Configure` via `AddDataSeries(BarsPeriodType.Minute, 240)`,
  with `EMA(BarsArray[1], 50)` indicator. Read via `htfEma[1]` (last
  completed H4 bar) and `Closes[1][1]`.
- **`CTrade.Buy / CTrade.Sell`** → `EnterLong / EnterShort` with a
  unique signal name per setup so multiple setups can stack up to
  `MaxOpenTrades`. `EntriesPerDirection = 5` is set high enough to
  cover that headroom.
- **`PositionsTotal()`** filtered by symbol + magic → `CountOpenPositions()`
  walks `Orders` for filled entry orders while the position is non-flat.
- **`OBJ_RECTANGLE`** drawings → `Draw.Rectangle(this, tag, ...)` with
  per-detector tag prefixes (`smc_z_*`, `smc_ob_*`, `smc_fvg_*`).
- **`Comment(...)`** info panel → `Draw.TextFixed(... TopRight ...)`.

## Behavior differences worth knowing

1. **OnBarClose only.** The MQL5 EA has `if(!newBar) return;` for
   detection but manages positions on every tick. NT8 port runs
   `OnBarClose`, so trailing stops and break-even update on bar
   close instead of every tick. For tighter trail behavior set
   `Calculate = OnEachTick` in `OnStateChange` (slower backtests).
2. **Unique-entries stacking.** NT8 needs unique signal names to
   stack multiple positions; the port assigns sequential names
   (`DZ_L_1`, `DZ_L_2`, `OB_S_1`, ...) so each setup gets its own
   bracket.
3. **Risk sizing.** `CalcContracts()` mirrors `CalcLots()` but
   returns integer contracts (futures don't trade fractional
   lots). For CFD-style instruments that allow fractional sizing
   replace `Math.Floor` with the instrument's `LotStep`.
4. **HTF EMA evaluated on completed bar.** The EA reads
   `iClose(_Symbol, HigherTF, 1)` (last closed H4); the port
   uses `Closes[1][1]` and `htfEma[1]` for the same semantics.
5. **`ENUM_TIMEFRAMES HigherTF`** is replaced by an integer
   `HigherTfMinutes` (default 240 = H4). To use H1 set 60, etc.
6. **No live news API**. Friday/Monday block hours are honored;
   true news-event blocking (NFP, FOMC) would need a separate
   data source — same as the original EA.

## Testing

1. Open a chart on the instrument you want to trade (default
   intent: NQ or MNQ on a 5-minute timeframe to mirror the original
   EA's NDX100 M5).
2. Right-click → Strategies → add `SmcNdx100Strategy`.
3. **Set `Starting balance ($, for backtest sizing)`** to the
   account size you want risk-percent sizing computed against.
4. **Adjust the "ticks" inputs** for the new instrument's tick size
   (see mapping note above).
5. Run a backtest in Strategy Analyzer with a 1-tick added series
   for fill resolution if you want intrabar fills.

## Known caveats

- The MQL5 EA uses `Slippage=10 points` on order placement.
  The NT8 strategy uses `Slippage = 0` by default — set realistic
  slippage in the Strategy Analyzer.
- Liquidity sweep detection is **logged but not yet a hard entry
  filter**, matching the original EA which also only printed sweep
  events without gating entries on them. If you want a sweep
  precondition, add `&& lastBullSweep` / `&& lastBearSweep` to the
  long/short PA confirmation calls.
- The H4 EMA filter requires at least 50+3 H4 bars of history
  to warm up. On a fresh chart the strategy will skip entries
  until `htfEma[2]` is valid.
