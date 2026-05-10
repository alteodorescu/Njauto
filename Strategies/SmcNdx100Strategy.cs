// SmcNdx100Strategy
// -----------------
// NinjaTrader 8 port of SMC_NDX100_EA.mq5 (MetaTrader 5).
// Smart Money Concepts entries on a single instrument:
//   - Supply / Demand zones (impulse-base-impulse pattern)
//   - Order Blocks
//   - Fair Value Gaps
//   - Liquidity sweep detection (logged, not yet a hard entry filter)
//   - Price-action confirmation: pin bar, engulfing, rejection close
//   - Higher-timeframe (H4) EMA50 trend filter (multi-bar swing structure)
//   - Daily loss / profit guards, session + Friday/Monday filters
//   - Trailing stop and break-even position management
//
// Bar indexing in NT8 ([0]=current, [1]=previous) is identical in
// direction to MQL5 with ArraySetAsSeries(true), so the index math
// from the original EA carries over directly.
//
// Sizing: MQL5's _Point (smallest price increment) maps to TickSize
// in NT8. Numeric inputs that are stated "in points" (OB_MinSize,
// FVG_MinSize, SL_Buffer, TrailStart, TrailStep, BE_Trigger,
// BE_Offset, LSweep_Points) keep the same names; on a NT instrument
// with a different tick size you may need to scale them.
//
// Place under: Documents/NinjaTrader 8/bin/Custom/Strategies/

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class SmcNdx100Strategy : Strategy
    {
        #region Inner data structures

        private struct Zone
        {
            public double High;
            public double Low;
            public int    StartBar;
            public bool   Bullish;     // true = demand, false = supply
            public bool   Active;
            public bool   Tested;
            public string Label;
        }

        private struct OrderBlock
        {
            public double High;
            public double Low;
            public int    Bar;
            public bool   Bullish;
            public bool   Active;
        }

        private struct FairValueGap
        {
            public double High;
            public double Low;
            public int    Bar;
            public bool   Bullish;
            public bool   Active;
        }

        private const int MAX_ZONES = 20;
        private const int MAX_OB    = 20;
        private const int MAX_FVG   = 20;

        private List<Zone>           zones        = new List<Zone>();
        private List<OrderBlock>     orderBlocks  = new List<OrderBlock>();
        private List<FairValueGap>   fvgs         = new List<FairValueGap>();

        // ----- Daily P&L tracking -----
        private DateTime lastDay = DateTime.MinValue;
        private double   dailyStartBalance;

        // ----- HTF EMA -----
        private EMA htfEma;

        // ----- Per-direction unique signal counter (for max concurrent) -----
        private int longSignalSeq;
        private int shortSignalSeq;

        // ----- Liquidity sweep flags (set in CheckLiquiditySweeps) -----
        private bool lastBearSweep;
        private bool lastBullSweep;

        #endregion

        #region User Inputs

        // === Risk Management ===
        [NinjaScriptProperty, Display(Name = "Risk % per trade", GroupName = "1. Risk Management", Order = 0)]
        public double RiskPercent { get; set; } = 1.0;

        [NinjaScriptProperty, Display(Name = "Max daily loss %", GroupName = "1. Risk Management", Order = 1)]
        public double MaxDailyLossPct { get; set; } = 3.0;

        [NinjaScriptProperty, Display(Name = "Max daily profit %", GroupName = "1. Risk Management", Order = 2)]
        public double MaxDailyProfitPct { get; set; } = 5.0;

        [NinjaScriptProperty, Display(Name = "Max concurrent positions", GroupName = "1. Risk Management", Order = 3)]
        public int MaxOpenTrades { get; set; } = 2;

        [NinjaScriptProperty, Display(Name = "Min lot size (contracts)", GroupName = "1. Risk Management", Order = 4)]
        public double MinLotSize { get; set; } = 1;

        [NinjaScriptProperty, Display(Name = "Max lot size (contracts)", GroupName = "1. Risk Management", Order = 5)]
        public double MaxLotSize { get; set; } = 5;

        [NinjaScriptProperty, Display(Name = "Starting balance ($, for backtest sizing)", GroupName = "1. Risk Management", Order = 6)]
        public double StartingBalance { get; set; } = 100000;

        // === SMC Zone Detection ===
        [NinjaScriptProperty, Display(Name = "Zone lookback bars", GroupName = "2. SMC Zone Detection", Order = 0)]
        public int ZoneLookback { get; set; } = 50;

        [NinjaScriptProperty, Display(Name = "Zone strength (impulse bars)", GroupName = "2. SMC Zone Detection", Order = 1)]
        public int ZoneStrength { get; set; } = 3;

        [NinjaScriptProperty, Display(Name = "Zone buffer (ticks)", GroupName = "2. SMC Zone Detection", Order = 2)]
        public double ZoneBuffer { get; set; } = 10;

        [NinjaScriptProperty, Display(Name = "Use higher-timeframe trend filter", GroupName = "2. SMC Zone Detection", Order = 3)]
        public bool UseMTF { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Higher-timeframe minutes", GroupName = "2. SMC Zone Detection", Order = 4)]
        public int HigherTfMinutes { get; set; } = 240;   // PERIOD_H4

        // === Order Blocks ===
        [NinjaScriptProperty, Display(Name = "Use order blocks", GroupName = "3. Order Blocks", Order = 0)]
        public bool UseOrderBlocks { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "OB lookback bars", GroupName = "3. Order Blocks", Order = 1)]
        public int OB_Lookback { get; set; } = 30;

        [NinjaScriptProperty, Display(Name = "OB min size (ticks)", GroupName = "3. Order Blocks", Order = 2)]
        public int OB_MinSize { get; set; } = 5;

        // === Fair Value Gaps ===
        [NinjaScriptProperty, Display(Name = "Use fair-value gaps", GroupName = "4. Fair Value Gaps", Order = 0)]
        public bool UseFVG { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "FVG lookback bars", GroupName = "4. Fair Value Gaps", Order = 1)]
        public int FVG_Lookback { get; set; } = 20;

        [NinjaScriptProperty, Display(Name = "FVG min size (ticks)", GroupName = "4. Fair Value Gaps", Order = 2)]
        public int FVG_MinSize { get; set; } = 15;

        // === Liquidity Sweeps ===
        [NinjaScriptProperty, Display(Name = "Detect liquidity sweeps", GroupName = "5. Liquidity Sweeps", Order = 0)]
        public bool UseLiquiditySweep { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Sweep lookback bars", GroupName = "5. Liquidity Sweeps", Order = 1)]
        public int LSweep_Lookback { get; set; } = 40;

        [NinjaScriptProperty, Display(Name = "Sweep margin (ticks)", GroupName = "5. Liquidity Sweeps", Order = 2)]
        public int LSweep_Points { get; set; } = 5;

        // === Price Action Filters ===
        [NinjaScriptProperty, Display(Name = "Require PA confirmation", GroupName = "6. Price Action", Order = 0)]
        public bool RequirePA { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Use engulfing", GroupName = "6. Price Action", Order = 1)]
        public bool UseEngulfing { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Use pin bar", GroupName = "6. Price Action", Order = 2)]
        public bool UsePinBar { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Use rejection close", GroupName = "6. Price Action", Order = 3)]
        public bool UseRejection { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Pin-bar wick:body ratio", GroupName = "6. Price Action", Order = 4)]
        public double PinBarRatio { get; set; } = 2.5;

        [NinjaScriptProperty, Display(Name = "Engulfing body ratio", GroupName = "6. Price Action", Order = 5)]
        public double EngulfRatio { get; set; } = 1.1;

        // === Market Structure ===
        [NinjaScriptProperty, Display(Name = "Trade with trend only", GroupName = "7. Market Structure", Order = 0)]
        public bool TradeWithTrend { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Structure lookback bars", GroupName = "7. Market Structure", Order = 1)]
        public int StructureLookback { get; set; } = 100;

        [NinjaScriptProperty, Display(Name = "Swing-point sensitivity", GroupName = "7. Market Structure", Order = 2)]
        public int SwingPoints { get; set; } = 5;

        // === Trade Execution ===
        [NinjaScriptProperty, Display(Name = "Min R:R ratio", GroupName = "8. Trade Execution", Order = 0)]
        public double RR_Ratio { get; set; } = 2.0;

        [NinjaScriptProperty, Display(Name = "SL buffer beyond zone (ticks)", GroupName = "8. Trade Execution", Order = 1)]
        public int SL_Buffer { get; set; } = 5;

        [NinjaScriptProperty, Display(Name = "Use trailing stop", GroupName = "8. Trade Execution", Order = 2)]
        public bool UseTrailingStop { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Trail activation (ticks)", GroupName = "8. Trade Execution", Order = 3)]
        public int TrailStart { get; set; } = 50;

        [NinjaScriptProperty, Display(Name = "Trail step (ticks)", GroupName = "8. Trade Execution", Order = 4)]
        public int TrailStep { get; set; } = 20;

        [NinjaScriptProperty, Display(Name = "Use break-even", GroupName = "8. Trade Execution", Order = 5)]
        public bool UseBreakEven { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "BE trigger (ticks)", GroupName = "8. Trade Execution", Order = 6)]
        public int BE_Trigger { get; set; } = 40;

        [NinjaScriptProperty, Display(Name = "BE offset (ticks)", GroupName = "8. Trade Execution", Order = 7)]
        public int BE_Offset { get; set; } = 2;

        // === Session Filter ===
        [NinjaScriptProperty, Display(Name = "Use session filter", GroupName = "9. Session", Order = 0)]
        public bool UseSessionFilter { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "London open hour (server)", GroupName = "9. Session", Order = 1)]
        public int LondonOpen { get; set; } = 8;

        [NinjaScriptProperty, Display(Name = "London close hour", GroupName = "9. Session", Order = 2)]
        public int LondonClose { get; set; } = 16;

        [NinjaScriptProperty, Display(Name = "NY open hour", GroupName = "9. Session", Order = 3)]
        public int NYOpen { get; set; } = 13;

        [NinjaScriptProperty, Display(Name = "NY close hour", GroupName = "9. Session", Order = 4)]
        public int NYClose { get; set; } = 21;

        [NinjaScriptProperty, Display(Name = "Trade London", GroupName = "9. Session", Order = 5)]
        public bool TradeLondon { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Trade NY", GroupName = "9. Session", Order = 6)]
        public bool TradeNY { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Trade overlap", GroupName = "9. Session", Order = 7)]
        public bool TradeOverlap { get; set; } = true;

        // === News / Time Filter ===
        [NinjaScriptProperty, Display(Name = "Block Friday PM", GroupName = "10. Time Filter", Order = 0)]
        public bool BlockFriday { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Friday block hour", GroupName = "10. Time Filter", Order = 1)]
        public int FridayBlockHour { get; set; } = 19;

        [NinjaScriptProperty, Display(Name = "Block Monday morning", GroupName = "10. Time Filter", Order = 2)]
        public bool BlockMonday { get; set; } = false;

        [NinjaScriptProperty, Display(Name = "Monday start hour", GroupName = "10. Time Filter", Order = 3)]
        public int MondayStartHour { get; set; } = 10;

        // === Diagnostics ===
        [NinjaScriptProperty, Display(Name = "Verbose log", GroupName = "11. Diagnostics", Order = 0)]
        public bool VerboseLog { get; set; } = false;

        [NinjaScriptProperty, Display(Name = "Show info panel", GroupName = "11. Diagnostics", Order = 1)]
        public bool ShowInfo { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Show zones / OB / FVG rectangles", GroupName = "11. Diagnostics", Order = 2)]
        public bool ShowVisuals { get; set; } = true;

        #endregion

        #region OnStateChange / OnBarUpdate

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                          = "Smart Money Concepts EA port (NDX100). Supply/Demand + Order Blocks + FVG + price action + HTF trend filter.";
                Name                                 = "SmcNdx100Strategy";
                Calculate                            = Calculate.OnBarClose;
                EntriesPerDirection                  = 5; // covers MaxOpenTrades=2 plus headroom for unique signal names
                EntryHandling                        = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy         = false;
                ExitOnSessionCloseSeconds            = 30;
                IsFillLimitOnTouch                   = false;
                MaximumBarsLookBack                  = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution                  = OrderFillResolution.Standard;
                Slippage                             = 0;
                StartBehavior                        = StartBehavior.WaitUntilFlat;
                TimeInForce                          = TimeInForce.Gtc;
                TraceOrders                          = false;
                RealtimeErrorHandling                = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling                   = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade                  = 110; // > StructureLookback
                IsInstantiatedOnEachOptimizationIteration = true;
            }
            else if (State == State.Configure)
            {
                if (UseMTF)
                    AddDataSeries(BarsPeriodType.Minute, HigherTfMinutes);
            }
            else if (State == State.DataLoaded)
            {
                zones        = new List<Zone>();
                orderBlocks  = new List<OrderBlock>();
                fvgs         = new List<FairValueGap>();
                if (UseMTF)
                    htfEma = EMA(BarsArray[1], 50);
            }
        }

        protected override void OnBarUpdate()
        {
            // Only act on the primary series.
            if (BarsInProgress != 0) return;
            if (CurrentBar < BarsRequiredToTrade) return;

            // Manage existing positions on every bar (cheap; runs OnBarClose).
            ManagePositions();

            // Daily reset: track day-start equity for daily loss/profit guards.
            if (lastDay != Time[0].Date)
            {
                dailyStartBalance = GetCurrentEquity();
                lastDay           = Time[0].Date;
            }

            if (!SafetyChecks())   return;
            if (!SessionAllowed()) return;
            if (!TimeAllowed())    return;

            int trend = GetMarketStructure();

            DetectSupplyDemandZones();
            if (UseOrderBlocks)    DetectOrderBlocks();
            if (UseFVG)            DetectFVG();
            if (UseLiquiditySweep) CheckLiquiditySweeps();

            if (ShowVisuals) { DrawZones(); DrawOrderBlocks(); DrawFVGs(); }
            if (ShowInfo)    DrawInfoPanel(trend);

            if (CountOpenPositions() >= MaxOpenTrades) return;

            CheckEntries(trend);
        }

        #endregion

        #region Safety / Session / Time filters

        private bool SafetyChecks()
        {
            double equity     = GetCurrentEquity();
            double dailyPL    = equity - dailyStartBalance;
            double dailyPLPct = (dailyStartBalance > 0) ? (dailyPL / dailyStartBalance * 100.0) : 0;

            if (dailyPLPct <= -MaxDailyLossPct)
            {
                if (VerboseLog) Print(string.Format("[SMC] Daily loss limit reached: {0:F2}%", dailyPLPct));
                return false;
            }
            if (dailyPLPct >= MaxDailyProfitPct)
            {
                if (VerboseLog) Print(string.Format("[SMC] Daily profit target reached: {0:F2}%", dailyPLPct));
                return false;
            }
            return true;
        }

        private bool SessionAllowed()
        {
            if (!UseSessionFilter) return true;
            int hour = Time[0].Hour;

            bool inLondon  = (hour >= LondonOpen  && hour < LondonClose);
            bool inNY      = (hour >= NYOpen      && hour < NYClose);
            bool inOverlap = (hour >= NYOpen      && hour < LondonClose);

            if (TradeOverlap && inOverlap) return true;
            if (TradeLondon  && inLondon)  return true;
            if (TradeNY      && inNY)      return true;
            return false;
        }

        private bool TimeAllowed()
        {
            DateTime t   = Time[0];
            int dow      = (int)t.DayOfWeek;  // Sun=0, Mon=1, ..., Fri=5
            if (BlockFriday && dow == 5 && t.Hour >= FridayBlockHour) return false;
            if (BlockMonday && dow == 1 && t.Hour <  MondayStartHour) return false;
            return true;
        }

        #endregion

        #region Market structure (HH/LL + HTF EMA)

        private int GetMarketStructure()
        {
            int bars = Math.Min(StructureLookback, CurrentBar - SwingPoints - 2);
            if (bars < 20) return 0;

            // Find last 3 swing highs / swing lows (newest first).
            double[] sh = new double[3];
            double[] sl = new double[3];
            int shIdx = 0, slIdx = 0;

            for (int i = SwingPoints; i < bars - SwingPoints && (shIdx < 3 || slIdx < 3); i++)
            {
                bool isSwingH = true, isSwingL = true;
                for (int j = 1; j <= SwingPoints; j++)
                {
                    if (High[i] <= High[i - j] || High[i] <= High[i + j]) isSwingH = false;
                    if (Low[i]  >= Low[i - j]  || Low[i]  >= Low[i + j])  isSwingL = false;
                }
                if (isSwingH && shIdx < 3) { sh[shIdx++] = High[i]; }
                if (isSwingL && slIdx < 3) { sl[slIdx++] = Low[i];  }
            }

            if (shIdx < 2 || slIdx < 2) return 0;

            bool bullish = (sh[0] > sh[1] && sl[0] > sl[1]);
            bool bearish = (sh[0] < sh[1] && sl[0] < sl[1]);
            int  ltf     = bullish ? 1 : (bearish ? -1 : 0);

            if (UseMTF)
            {
                int htf = GetHTFTrend();
                if (htf != 0 && htf != ltf)
                {
                    if (VerboseLog) Print(string.Format("[SMC] MTF divergence: LTF={0} HTF={1} - skipping", ltf, htf));
                    return 0;
                }
            }
            return ltf;
        }

        private int GetHTFTrend()
        {
            if (htfEma == null) return 0;
            if (CurrentBars[1] < 3) return 0;

            double maPrev1 = htfEma[1];
            double maPrev2 = htfEma[2];
            double price   = Closes[1][1];

            if (price > maPrev1 && maPrev1 > maPrev2) return  1;
            if (price < maPrev1 && maPrev1 < maPrev2) return -1;
            return 0;
        }

        #endregion

        #region Supply / Demand zone detection (impulse-base-impulse)

        private void DetectSupplyDemandZones()
        {
            zones.Clear();
            int copySize = Math.Min(ZoneLookback + ZoneStrength + 5, CurrentBar);
            if (copySize < ZoneStrength + 2) return;

            double currentClose = Close[0];

            for (int i = 1; i < ZoneLookback && zones.Count < MAX_ZONES; i++)
            {
                if (i + ZoneStrength + 1 >= copySize) break;

                double baseBody  = Math.Abs(Close[i] - Open[i]);
                double baseRange = High[i] - Low[i];
                if (baseRange <= 0) continue;

                bool isBase = (baseBody / baseRange < 0.6);
                if (!isBase) continue;

                // ----- DEMAND ZONE: prior bearish drop -> base -> bullish impulse up -----
                bool impulseUp = true;
                for (int k = 1; k <= ZoneStrength; k++)
                {
                    if (i - k < 0)                 { impulseUp = false; break; }
                    if (Close[i - k] <= Open[i - k]) { impulseUp = false; break; }
                }
                bool priorDrop = (Close[i + 1] < Open[i + 1]);

                if (priorDrop && impulseUp && currentClose > Low[i])
                {
                    zones.Add(new Zone {
                        High = High[i], Low = Low[i], StartBar = i,
                        Bullish = true, Active = true, Tested = false, Label = "DZ"
                    });
                }

                // ----- SUPPLY ZONE: prior bullish rally -> base -> bearish impulse down -----
                bool impulseDown = true;
                for (int k = 1; k <= ZoneStrength; k++)
                {
                    if (i - k < 0)                 { impulseDown = false; break; }
                    if (Close[i - k] >= Open[i - k]) { impulseDown = false; break; }
                }
                bool priorRise = (Close[i + 1] > Open[i + 1]);

                if (priorRise && impulseDown && currentClose < High[i])
                {
                    zones.Add(new Zone {
                        High = High[i], Low = Low[i], StartBar = i,
                        Bullish = false, Active = true, Tested = false, Label = "SZ"
                    });
                }
            }
        }

        #endregion

        #region Order block detection

        private void DetectOrderBlocks()
        {
            orderBlocks.Clear();
            int bars = Math.Min(OB_Lookback, CurrentBar - 3);

            for (int i = 1; i < bars && orderBlocks.Count < MAX_OB; i++)
            {
                double bodyPts = Math.Abs(Close[i] - Open[i]) / TickSize;
                if (bodyPts < OB_MinSize) continue;

                // Bullish OB: bearish candle followed by strong bullish move
                if (Close[i] < Open[i])
                {
                    bool strongBullAfter = (Close[i - 1] > High[i]);
                    if (strongBullAfter)
                    {
                        var ob = new OrderBlock {
                            High = Open[i],   // bearish open
                            Low  = Close[i],  // bearish close
                            Bar  = i,
                            Bullish = true
                        };
                        ob.Active = (Close[0] > ob.Low);
                        orderBlocks.Add(ob);
                    }
                }
                // Bearish OB: bullish candle followed by strong bearish move
                if (Close[i] > Open[i])
                {
                    bool strongBearAfter = (Close[i - 1] < Low[i]);
                    if (strongBearAfter)
                    {
                        var ob = new OrderBlock {
                            High = Close[i],  // bullish close
                            Low  = Open[i],   // bullish open
                            Bar  = i,
                            Bullish = false
                        };
                        ob.Active = (Close[0] < ob.High);
                        orderBlocks.Add(ob);
                    }
                }
            }
        }

        #endregion

        #region Fair-value gap detection

        private void DetectFVG()
        {
            fvgs.Clear();
            int bars = Math.Min(FVG_Lookback, CurrentBar - 3);

            for (int i = 1; i < bars - 1 && fvgs.Count < MAX_FVG; i++)
            {
                // Bullish FVG: candle[i+1].high < candle[i-1].low
                if (High[i + 1] < Low[i - 1])
                {
                    double gap = (Low[i - 1] - High[i + 1]) / TickSize;
                    if (gap >= FVG_MinSize)
                    {
                        var f = new FairValueGap {
                            High = Low[i - 1],
                            Low  = High[i + 1],
                            Bar  = i,
                            Bullish = true
                        };
                        f.Active = (Close[0] > f.Low);
                        fvgs.Add(f);
                    }
                }
                // Bearish FVG: candle[i+1].low > candle[i-1].high
                if (Low[i + 1] > High[i - 1])
                {
                    double gap = (Low[i + 1] - High[i - 1]) / TickSize;
                    if (gap >= FVG_MinSize)
                    {
                        var f = new FairValueGap {
                            High = Low[i + 1],
                            Low  = High[i - 1],
                            Bar  = i,
                            Bullish = false
                        };
                        f.Active = (Close[0] < f.High);
                        fvgs.Add(f);
                    }
                }
            }
        }

        #endregion

        #region Liquidity sweep detection (logged, used for context)

        private void CheckLiquiditySweeps()
        {
            int bars = Math.Min(LSweep_Lookback, CurrentBar - 5);
            if (bars < 12) { lastBearSweep = lastBullSweep = false; return; }

            double swingHigh = High[2], swingLow = Low[2];
            for (int i = 2; i < 12; i++)
            {
                swingHigh = Math.Max(swingHigh, High[i]);
                swingLow  = Math.Min(swingLow,  Low[i]);
            }

            lastBearSweep = (High[1] > swingHigh + LSweep_Points * TickSize &&
                             Close[1] < swingHigh);
            lastBullSweep = (Low[1]  < swingLow  - LSweep_Points * TickSize &&
                             Close[1] > swingLow);

            if (VerboseLog && lastBearSweep) Print(string.Format("[SMC] Bearish liquidity sweep at {0:F2}", swingHigh));
            if (VerboseLog && lastBullSweep) Print(string.Format("[SMC] Bullish liquidity sweep at {0:F2}", swingLow));
        }

        #endregion

        #region Price-action confirmation

        private bool ConfirmPaBullish()
        {
            if (!RequirePA) return true;

            double o1 = Open[1], h1 = High[1], l1 = Low[1], c1 = Close[1];
            double o2 = Open[2], c2 = Close[2];
            double body  = Math.Abs(c1 - o1);
            double range = h1 - l1;
            double lWick = (o1 < c1) ? (o1 - l1) : (c1 - l1);

            if (UsePinBar)
                if (c1 > o1 && lWick >= body * PinBarRatio && range > 0) return true;

            if (UseEngulfing)
                if (c2 < o2 && c1 > o1 && c1 > o2 && o1 < c2 && body > Math.Abs(c2 - o2) * EngulfRatio) return true;

            if (UseRejection)
                if (c1 > o1 && lWick > body && c1 > (l1 + range * 0.7)) return true;

            return false;
        }

        private bool ConfirmPaBearish()
        {
            if (!RequirePA) return true;

            double o1 = Open[1], h1 = High[1], l1 = Low[1], c1 = Close[1];
            double o2 = Open[2], c2 = Close[2];
            double body  = Math.Abs(c1 - o1);
            double range = h1 - l1;
            double uWick = (o1 > c1) ? (h1 - o1) : (h1 - c1);

            if (UsePinBar)
                if (c1 < o1 && uWick >= body * PinBarRatio && range > 0) return true;

            if (UseEngulfing)
                if (c2 > o2 && c1 < o1 && c1 < o2 && o1 > c2 && body > Math.Abs(c2 - o2) * EngulfRatio) return true;

            if (UseRejection)
                if (c1 < o1 && uWick > body && c1 < (h1 - range * 0.7)) return true;

            return false;
        }

        #endregion

        #region Entry logic

        private void CheckEntries(int trend)
        {
            double price = Close[0];   // proxy for both bid/ask in OnBarClose mode

            bool allowLong  = (!TradeWithTrend || trend >= 0);
            bool allowShort = (!TradeWithTrend || trend <= 0);

            if (allowLong)
            {
                // Demand zones
                for (int i = 0; i < zones.Count; i++)
                {
                    if (!zones[i].Active || !zones[i].Bullish) continue;
                    if (price < zones[i].Low || price > zones[i].High) continue;
                    if (!ConfirmPaBullish()) continue;

                    double sl  = zones[i].Low - SL_Buffer * TickSize;
                    double tp  = price + (price - sl) * RR_Ratio;
                    int    qty = CalcContracts(price, sl);
                    if (!ValidRR(price, sl, tp) || qty <= 0) continue;

                    string sig = "DZ_L_" + (++longSignalSeq);
                    SetStopLoss(sig, CalculationMode.Price, sl, false);
                    SetProfitTarget(sig, CalculationMode.Price, tp);
                    EnterLong(qty, sig);
                    var z = zones[i]; z.Tested = true; zones[i] = z;
                    if (VerboseLog) Print(string.Format("[SMC] DZ Buy {0}c @ {1:F2} sl={2:F2} tp={3:F2}", qty, price, sl, tp));
                }

                if (UseOrderBlocks)
                {
                    for (int i = 0; i < orderBlocks.Count; i++)
                    {
                        if (!orderBlocks[i].Active || !orderBlocks[i].Bullish) continue;
                        if (price < orderBlocks[i].Low || price > orderBlocks[i].High) continue;
                        if (!ConfirmPaBullish()) continue;

                        double sl  = orderBlocks[i].Low - SL_Buffer * TickSize;
                        double tp  = price + (price - sl) * RR_Ratio;
                        int    qty = CalcContracts(price, sl);
                        if (!ValidRR(price, sl, tp) || qty <= 0) continue;

                        string sig = "OB_L_" + (++longSignalSeq);
                        SetStopLoss(sig, CalculationMode.Price, sl, false);
                        SetProfitTarget(sig, CalculationMode.Price, tp);
                        EnterLong(qty, sig);
                        var ob = orderBlocks[i]; ob.Active = false; orderBlocks[i] = ob;
                        if (VerboseLog) Print(string.Format("[SMC] OB Buy {0}c @ {1:F2} sl={2:F2} tp={3:F2}", qty, price, sl, tp));
                    }
                }

                if (UseFVG)
                {
                    for (int i = 0; i < fvgs.Count; i++)
                    {
                        if (!fvgs[i].Active || !fvgs[i].Bullish) continue;
                        if (price < fvgs[i].Low || price > fvgs[i].High) continue;
                        if (!ConfirmPaBullish()) continue;

                        double sl  = fvgs[i].Low - SL_Buffer * TickSize;
                        double tp  = price + (price - sl) * RR_Ratio;
                        int    qty = CalcContracts(price, sl);
                        if (!ValidRR(price, sl, tp) || qty <= 0) continue;

                        string sig = "FVG_L_" + (++longSignalSeq);
                        SetStopLoss(sig, CalculationMode.Price, sl, false);
                        SetProfitTarget(sig, CalculationMode.Price, tp);
                        EnterLong(qty, sig);
                        var f = fvgs[i]; f.Active = false; fvgs[i] = f;
                        if (VerboseLog) Print(string.Format("[SMC] FVG Buy {0}c @ {1:F2} sl={2:F2} tp={3:F2}", qty, price, sl, tp));
                    }
                }
            }

            if (allowShort)
            {
                for (int i = 0; i < zones.Count; i++)
                {
                    if (!zones[i].Active || zones[i].Bullish) continue;
                    if (price < zones[i].Low || price > zones[i].High) continue;
                    if (!ConfirmPaBearish()) continue;

                    double sl  = zones[i].High + SL_Buffer * TickSize;
                    double tp  = price - (sl - price) * RR_Ratio;
                    int    qty = CalcContracts(price, sl);
                    if (!ValidRR(price, sl, tp) || qty <= 0) continue;

                    string sig = "SZ_S_" + (++shortSignalSeq);
                    SetStopLoss(sig, CalculationMode.Price, sl, false);
                    SetProfitTarget(sig, CalculationMode.Price, tp);
                    EnterShort(qty, sig);
                    var z = zones[i]; z.Tested = true; zones[i] = z;
                    if (VerboseLog) Print(string.Format("[SMC] SZ Sell {0}c @ {1:F2} sl={2:F2} tp={3:F2}", qty, price, sl, tp));
                }

                if (UseOrderBlocks)
                {
                    for (int i = 0; i < orderBlocks.Count; i++)
                    {
                        if (!orderBlocks[i].Active || orderBlocks[i].Bullish) continue;
                        if (price < orderBlocks[i].Low || price > orderBlocks[i].High) continue;
                        if (!ConfirmPaBearish()) continue;

                        double sl  = orderBlocks[i].High + SL_Buffer * TickSize;
                        double tp  = price - (sl - price) * RR_Ratio;
                        int    qty = CalcContracts(price, sl);
                        if (!ValidRR(price, sl, tp) || qty <= 0) continue;

                        string sig = "OB_S_" + (++shortSignalSeq);
                        SetStopLoss(sig, CalculationMode.Price, sl, false);
                        SetProfitTarget(sig, CalculationMode.Price, tp);
                        EnterShort(qty, sig);
                        var ob = orderBlocks[i]; ob.Active = false; orderBlocks[i] = ob;
                        if (VerboseLog) Print(string.Format("[SMC] OB Sell {0}c @ {1:F2} sl={2:F2} tp={3:F2}", qty, price, sl, tp));
                    }
                }

                if (UseFVG)
                {
                    for (int i = 0; i < fvgs.Count; i++)
                    {
                        if (!fvgs[i].Active || fvgs[i].Bullish) continue;
                        if (price < fvgs[i].Low || price > fvgs[i].High) continue;
                        if (!ConfirmPaBearish()) continue;

                        double sl  = fvgs[i].High + SL_Buffer * TickSize;
                        double tp  = price - (sl - price) * RR_Ratio;
                        int    qty = CalcContracts(price, sl);
                        if (!ValidRR(price, sl, tp) || qty <= 0) continue;

                        string sig = "FVG_S_" + (++shortSignalSeq);
                        SetStopLoss(sig, CalculationMode.Price, sl, false);
                        SetProfitTarget(sig, CalculationMode.Price, tp);
                        EnterShort(qty, sig);
                        var f = fvgs[i]; f.Active = false; fvgs[i] = f;
                        if (VerboseLog) Print(string.Format("[SMC] FVG Sell {0}c @ {1:F2} sl={2:F2} tp={3:F2}", qty, price, sl, tp));
                    }
                }
            }
        }

        #endregion

        #region Position management (trailing + break-even)

        private void ManagePositions()
        {
            if (Position.MarketPosition == MarketPosition.Flat) return;

            double price = Close[0];
            double openPrice = Position.AveragePrice;

            // We tighten the stop on whichever signal name the position was opened
            // under. SetStopLoss is keyed by signal name; calling it here moves
            // the stop forward only when newSL is more favorable.
            // Because EntryHandling = UniqueEntries with multiple sig names,
            // we iterate orders to find the active long/short signal.
            // Simpler approach: track which side we are on via Position.MarketPosition.

            // Use the MOST RECENT entry signal name for SetStopLoss. If multiple
            // unique entries were filled, NT manages each bracket independently;
            // the per-position trail below applies to whichever direction is open.
            string activeSig = FindActiveEntrySignal();
            if (activeSig == null) return;

            if (Position.MarketPosition == MarketPosition.Long)
            {
                double profitPts = (price - openPrice) / TickSize;

                if (UseBreakEven && profitPts >= BE_Trigger)
                {
                    double newSl = openPrice + BE_Offset * TickSize;
                    SetStopLoss(activeSig, CalculationMode.Price, newSl, false);
                }

                if (UseTrailingStop && profitPts >= TrailStart)
                {
                    double trail = price - TrailStep * TickSize;
                    SetStopLoss(activeSig, CalculationMode.Price, trail, false);
                }
            }
            else // Short
            {
                double profitPts = (openPrice - price) / TickSize;

                if (UseBreakEven && profitPts >= BE_Trigger)
                {
                    double newSl = openPrice - BE_Offset * TickSize;
                    SetStopLoss(activeSig, CalculationMode.Price, newSl, false);
                }

                if (UseTrailingStop && profitPts >= TrailStart)
                {
                    double trail = price + TrailStep * TickSize;
                    SetStopLoss(activeSig, CalculationMode.Price, trail, false);
                }
            }
        }

        private string FindActiveEntrySignal()
        {
            // Walk live orders and pick the first entry signal name still open.
            for (int i = 0; i < Orders.Count; i++)
            {
                Order o = Orders[i];
                if (o.OrderState == OrderState.Filled
                    && (o.OrderAction == OrderAction.Buy || o.OrderAction == OrderAction.SellShort)
                    && !string.IsNullOrEmpty(o.Name))
                    return o.Name;
            }
            return null;
        }

        #endregion

        #region Helpers

        private bool ValidRR(double entry, double sl, double tp)
        {
            double risk   = Math.Abs(entry - sl);
            double reward = Math.Abs(tp - entry);
            if (risk <= 0) return false;
            return (reward / risk >= RR_Ratio);
        }

        // Risk-percent sizing in contracts. tick value pulled from instrument.
        private int CalcContracts(double entry, double sl)
        {
            double balance  = GetCurrentEquity();
            double riskAmt  = balance * RiskPercent / 100.0;
            double slTicks  = Math.Abs(entry - sl) / TickSize;
            if (slTicks <= 0) return 0;

            double tickValue = Instrument.MasterInstrument.PointValue * TickSize;
            if (tickValue <= 0) return 0;

            double rawQty = riskAmt / (slTicks * tickValue);
            int qty = (int)Math.Floor(rawQty);
            qty = (int)Math.Max(qty, MinLotSize);
            qty = (int)Math.Min(qty, MaxLotSize);
            return qty;
        }

        private int CountOpenPositions()
        {
            // EntriesPerDirection lets multiple unique entries stack; count
            // them by checking live working/filled entry orders.
            int count = 0;
            for (int i = 0; i < Orders.Count; i++)
            {
                Order o = Orders[i];
                if (o.OrderState == OrderState.Filled
                    && (o.OrderAction == OrderAction.Buy || o.OrderAction == OrderAction.SellShort))
                {
                    // Treat each filled entry as a slot until the position is flat
                    if (Position.MarketPosition != MarketPosition.Flat) count++;
                }
            }
            // If the strategy has been running but Orders is stale, fall back to
            // a single-position count derived from Position.MarketPosition.
            if (count == 0 && Position.MarketPosition != MarketPosition.Flat) count = 1;
            return count;
        }

        // Equity = StartingBalance + realized + unrealized. Works in backtest and live.
        private double GetCurrentEquity()
        {
            double unrealized = 0;
            if (Position.MarketPosition != MarketPosition.Flat && CurrentBar > 0)
                unrealized = Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]);
            double realized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            return StartingBalance + realized + unrealized;
        }

        #endregion

        #region Visuals

        private void DrawZones()
        {
            for (int i = 0; i < zones.Count; i++)
            {
                if (!zones[i].Active) continue;
                string tag = "smc_z_" + i;
                int startBarsAgo = zones[i].StartBar;
                Brush brush = zones[i].Bullish ? Brushes.ForestGreen : Brushes.FireBrick;
                Draw.Rectangle(this, tag, false, startBarsAgo, zones[i].Low,
                    -10, zones[i].High, brush, brush, 30);
            }
        }

        private void DrawOrderBlocks()
        {
            for (int i = 0; i < orderBlocks.Count; i++)
            {
                if (!orderBlocks[i].Active) continue;
                string tag = "smc_ob_" + i;
                int startBarsAgo = orderBlocks[i].Bar;
                Brush brush = orderBlocks[i].Bullish ? Brushes.DodgerBlue : Brushes.OrangeRed;
                Draw.Rectangle(this, tag, false, startBarsAgo, orderBlocks[i].Low,
                    -5, orderBlocks[i].High, brush, brush, 20);
            }
        }

        private void DrawFVGs()
        {
            for (int i = 0; i < fvgs.Count; i++)
            {
                if (!fvgs[i].Active) continue;
                string tag = "smc_fvg_" + i;
                int startBarsAgo = fvgs[i].Bar;
                Draw.Rectangle(this, tag, false, startBarsAgo, fvgs[i].Low,
                    -5, fvgs[i].High, Brushes.Goldenrod, Brushes.Goldenrod, 15);
            }
        }

        private void DrawInfoPanel(int trend)
        {
            string trendStr =
                trend ==  1 ? "BULLISH" :
                trend == -1 ? "BEARISH" : "RANGING";

            double dailyPL = GetCurrentEquity() - dailyStartBalance;
            int    openPos = CountOpenPositions();

            string info =
                  "SMC NDX100 Strategy\n"
                + "Trend:     " + trendStr + "\n"
                + "Zones:     " + zones.Count       + " active\n"
                + "OBs:       " + orderBlocks.Count + " active\n"
                + "FVGs:      " + fvgs.Count        + " active\n"
                + "Positions: " + openPos + "/" + MaxOpenTrades + "\n"
                + "Daily P/L: " + (dailyPL >= 0 ? "+" : "") + dailyPL.ToString("F2");

            Brush brush =
                trend ==  1 ? Brushes.MediumSeaGreen :
                trend == -1 ? Brushes.IndianRed     : Brushes.Gray;

            Draw.TextFixed(this, "smc_info", info, TextPosition.TopRight,
                brush, new SimpleFont("Consolas", 11),
                Brushes.Transparent, Brushes.Transparent, 0);
        }

        #endregion
    }
}
