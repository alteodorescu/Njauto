// EthVpVwapStrategy
// -----------------
// NinjaTrader 8 strategy. Trades a single futures instrument using only:
//   - ETH-session anchored Volume Profile (VAH / VAL / POC)
//   - ETH-session anchored VWAP (with sigma bands)
//
// The strategy is "self-aware" of propfirm rules:
//   - Position size = ScalingTable lookup at current net profit
//   - SL distance  = floor((dailyLossRoom - safetyBuffer) / (contracts * tickValue))
//   - GuardState transitions block new entries on Warning/SoftHalt/Locked
//
// Place this file under:
//   Documents/NinjaTrader 8/bin/Custom/Strategies/EthVpVwapStrategy.cs
// And the AddOns/NjAuto/*.cs files under:
//   Documents/NinjaTrader 8/bin/Custom/AddOns/NjAuto/

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns.NjAuto;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class EthVpVwapStrategy : Strategy
    {
        // ----- Engines -----
        private InstrumentMeta meta;
        private SessionClock sessionClock;
        private AnchoredVolumeProfile profile;
        private AnchoredVwap vwap;
        private RegimeClassifier regime;
        private ScalingTable scaling;
        private PositionSizer sizer;
        private PropfirmRules rules;
        private GuardState guard;
        private HistoricalLevelStore historicalStore;

        // ----- Trade state -----
        private string activeSignal;
        private double pendingSlPrice;
        private double pendingTpPrice;
        private int pendingContracts;
        private DateTime lastExitTime = DateTime.MinValue;

        // ----- Reclaim tracking (for VWAP reclaim setup) -----
        private int barsAboveVwap;
        private int barsBelowVwap;

        // ----- POC rejection tracking -----
        private int barsTouchingPocFromAbove;
        private int barsTouchingPocFromBelow;

        // ----- Equity bookkeeping -----
        private double realizedPnL;
        private string journalCsvPath;

        // ----- Log dedup -----
        private string lastSkipCategory;

        #region User Inputs

        [NinjaScriptProperty, Display(Name = "Master symbol (e.g. ES, MNQ)", GroupName = "1. Instrument", Order = 0)]
        public string MasterSymbol { get; set; } = "MES";

        [NinjaScriptProperty, Display(Name = "Tick value override ($/tick)", GroupName = "1. Instrument", Order = 1)]
        public double TickValueOverride { get; set; } = 1.25; // MES default

        [NinjaScriptProperty, Display(Name = "Profile bin (ticks)", GroupName = "1. Instrument", Order = 2)]
        public int BinTicks { get; set; } = 4;

        [NinjaScriptProperty, Display(Name = "ETH open hour (ET)", GroupName = "1. Instrument", Order = 3)]
        public int EthOpenHourEt { get; set; } = 18;

        [NinjaScriptProperty, Display(Name = "ETH close hour (ET)", GroupName = "1. Instrument", Order = 4)]
        public int EthCloseHourEt { get; set; } = 17;

        [NinjaScriptProperty, Display(Name = "Maintenance break (min)", GroupName = "1. Instrument", Order = 5)]
        public int MaintenanceBreakMinutes { get; set; } = 60;

        [NinjaScriptProperty, Display(Name = "Value area %", GroupName = "2. Profile/VWAP", Order = 0)]
        public double ValueAreaPct { get; set; } = 0.70;

        [NinjaScriptProperty, Display(Name = "VWAP slope lookback (min)", GroupName = "2. Profile/VWAP", Order = 1)]
        public int VwapSlopeLookbackMin { get; set; } = 30;

        [NinjaScriptProperty, Display(Name = "Warmup minutes", GroupName = "3. Regime", Order = 0)]
        public int WarmupMinutes { get; set; } = 60;

        [NinjaScriptProperty, Display(Name = "Balance |VWAP-POC| as fraction of VA width", GroupName = "3. Regime", Order = 1)]
        public double BalancePocVwapFraction { get; set; } = 0.25;

        [NinjaScriptProperty, Display(Name = "Trend |VWAP-POC| as fraction of VA width", GroupName = "3. Regime", Order = 2)]
        public double TrendPocVwapFraction { get; set; } = 0.60;

        [NinjaScriptProperty, Display(Name = "Min time-in-VA fraction (balance)", GroupName = "3. Regime", Order = 3)]
        public double MinTimeInVaFraction { get; set; } = 0.60;

        [NinjaScriptProperty, Display(Name = "Trend min |VWAP slope| (ticks/min)", GroupName = "3. Regime", Order = 4)]
        public double TrendVwapSlopeMinTicksPerMin { get; set; } = 0.30;

        [NinjaScriptProperty, Display(Name = "Skip first N min of session", GroupName = "4. Filters", Order = 0)]
        public int SkipFirstMinutes { get; set; } = 15;

        [NinjaScriptProperty, Display(Name = "Cooldown after exit (min)", GroupName = "4. Filters", Order = 1)]
        public int CooldownMinutes { get; set; } = 20;

        [NinjaScriptProperty, Display(Name = "Max spread (ticks)", GroupName = "4. Filters", Order = 2)]
        public int MaxSpreadTicks { get; set; } = 3;

        [NinjaScriptProperty, Display(Name = "Enable balance setups", GroupName = "4. Filters", Order = 3)]
        public bool EnableBalanceSetups { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Enable trend setups", GroupName = "4. Filters", Order = 4)]
        public bool EnableTrendSetups { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Starting balance ($)", GroupName = "5. Propfirm", Order = 0)]
        public double StartingBalance { get; set; } = 50000;

        [NinjaScriptProperty, Display(Name = "Daily loss limit ($)", GroupName = "5. Propfirm", Order = 1)]
        public double DailyLossLimit { get; set; } = 1250;

        [NinjaScriptProperty, Display(Name = "Daily loss anchor", GroupName = "5. Propfirm", Order = 2)]
        public DailyLossAnchor DailyLossAnchorMode { get; set; } = DailyLossAnchor.StartOfDayEquity;

        [NinjaScriptProperty, Display(Name = "Max overall loss ($)", GroupName = "5. Propfirm", Order = 3)]
        public double MaxOverallLoss { get; set; } = 2500;

        [NinjaScriptProperty, Display(Name = "Overall loss anchor", GroupName = "5. Propfirm", Order = 4)]
        public OverallLossAnchor OverallLossAnchorMode { get; set; } = OverallLossAnchor.TrailingHighEodBalance;

        [NinjaScriptProperty, Display(Name = "Lock profit at ($, 0=disabled)", GroupName = "5. Propfirm", Order = 5)]
        public double LockProfitAt { get; set; } = 0;

        [NinjaScriptProperty, Display(Name = "Warning % of daily limit", GroupName = "5. Propfirm", Order = 6)]
        public double WarningPctOfDailyLoss { get; set; } = 0.80;

        // Format: "minProfit:contracts;minProfit:contracts;..."
        // Example: "0:2;1000:4;2500:6;5000:10"
        [NinjaScriptProperty, Display(Name = "Scaling tiers", GroupName = "6. Sizing", Order = 0)]
        public string ScalingTiers { get; set; } = "0:1;500:2;1500:3;3000:5";

        [NinjaScriptProperty, Display(Name = "Safety buffer ($)", GroupName = "6. Sizing", Order = 1)]
        public double SafetyBuffer { get; set; } = 25;

        [NinjaScriptProperty, Display(Name = "Min SL ticks", GroupName = "6. Sizing", Order = 2)]
        public int MinSlTicks { get; set; } = 8;

        [NinjaScriptProperty, Display(Name = "Max SL ticks", GroupName = "6. Sizing", Order = 3)]
        public int MaxSlTicks { get; set; } = 60;

        [NinjaScriptProperty, Display(Name = "Min R multiple", GroupName = "6. Sizing", Order = 4)]
        public double MinRMultiple { get; set; } = 1.0;

        [NinjaScriptProperty, Display(Name = "Enable self-awareness (propfirm)", GroupName = "6. Sizing", Order = 5)]
        public bool EnableSelfAwareness { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Fixed contracts (when self-awareness off)", GroupName = "6. Sizing", Order = 6)]
        public int FixedContracts { get; set; } = 1;

        [NinjaScriptProperty, Display(Name = "Fixed SL ticks (when self-awareness off)", GroupName = "6. Sizing", Order = 7)]
        public int FixedSlTicks { get; set; } = 20;

        [NinjaScriptProperty, Display(Name = "Journal CSV directory (blank=disabled)", GroupName = "7. Diagnostics", Order = 0)]
        public string JournalDir { get; set; } = "";

        [NinjaScriptProperty, Display(Name = "Verbose log", GroupName = "7. Diagnostics", Order = 1)]
        public bool VerboseLog { get; set; } = false;

        [NinjaScriptProperty, Display(Name = "Show chart plots", GroupName = "7. Diagnostics", Order = 2)]
        public bool ShowChartPlots { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Enable historical levels", GroupName = "8. Historical Levels", Order = 0)]
        public bool EnableHistoricalLevels { get; set; } = true;

        [NinjaScriptProperty, Display(Name = "Lookback days", GroupName = "8. Historical Levels", Order = 1)]
        public int LookbackDays { get; set; } = 5;

        [NinjaScriptProperty, Display(Name = "Min touches", GroupName = "8. Historical Levels", Order = 2)]
        public int MinTouches { get; set; } = 10;

        [NinjaScriptProperty, Display(Name = "Touch tolerance (ticks)", GroupName = "8. Historical Levels", Order = 3)]
        public int LevelTouchToleranceTicks { get; set; } = 2;

        // Plot accessors (indexes match AddPlot order in SetDefaults).
        [Browsable(false), XmlIgnore] public Series<double> VwapPlot   { get { return Values[0]; } }
        [Browsable(false), XmlIgnore] public Series<double> Upper1Plot { get { return Values[1]; } }
        [Browsable(false), XmlIgnore] public Series<double> Lower1Plot { get { return Values[2]; } }
        [Browsable(false), XmlIgnore] public Series<double> Upper2Plot { get { return Values[3]; } }
        [Browsable(false), XmlIgnore] public Series<double> Lower2Plot { get { return Values[4]; } }
        [Browsable(false), XmlIgnore] public Series<double> PocPlot    { get { return Values[5]; } }
        [Browsable(false), XmlIgnore] public Series<double> VahPlot    { get { return Values[6]; } }
        [Browsable(false), XmlIgnore] public Series<double> ValPlot    { get { return Values[7]; } }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "ETH-session anchored Volume Profile + VWAP, propfirm-aware sizing.";
                Name = "EthVpVwapStrategy";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // Plots are added in fixed order so the Values[i] indexers
                // above resolve to the correct series.
                AddPlot(new Stroke(Brushes.DodgerBlue,        2), PlotStyle.Line, "VWAP");
                AddPlot(new Stroke(Brushes.LightSteelBlue,    1), PlotStyle.Line, "Upper1Sigma");
                AddPlot(new Stroke(Brushes.LightSteelBlue,    1), PlotStyle.Line, "Lower1Sigma");
                AddPlot(new Stroke(Brushes.SteelBlue,         1), PlotStyle.Line, "Upper2Sigma");
                AddPlot(new Stroke(Brushes.SteelBlue,         1), PlotStyle.Line, "Lower2Sigma");
                AddPlot(new Stroke(Brushes.Goldenrod,         2), PlotStyle.Line, "POC");
                AddPlot(new Stroke(Brushes.MediumSeaGreen,    2), PlotStyle.Line, "VAH");
                AddPlot(new Stroke(Brushes.IndianRed,         2), PlotStyle.Line, "VAL");
            }
            else if (State == State.Configure)
            {
                // Add 1-tick series for accurate volume binning + fill resolution.
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                meta = new InstrumentMeta
                {
                    MasterSymbol = MasterSymbol,
                    TickSize = Instrument.MasterInstrument.TickSize,
                    TickValue = TickValueOverride > 0
                        ? TickValueOverride
                        : Instrument.MasterInstrument.PointValue * Instrument.MasterInstrument.TickSize,
                    BinTicks = Math.Max(1, BinTicks),
                    EthOpenHourEt = EthOpenHourEt,
                    EthOpenMinuteEt = 0,
                    EthCloseHourEt = EthCloseHourEt,
                    EthCloseMinuteEt = 0,
                    MaintenanceBreakMinutes = MaintenanceBreakMinutes
                };

                sessionClock = new SessionClock(meta);
                profile = new AnchoredVolumeProfile(meta, ValueAreaPct);
                vwap = new AnchoredVwap();
                regime = new RegimeClassifier(
                    WarmupMinutes,
                    BalancePocVwapFraction,
                    TrendPocVwapFraction,
                    MinTimeInVaFraction,
                    TrendVwapSlopeMinTicksPerMin,
                    meta.TickSize);

                scaling = ScalingTable.Parse(ScalingTiers);

                sizer = new PositionSizer(scaling, meta, SafetyBuffer, MinSlTicks, MaxSlTicks);

                rules = new PropfirmRules
                {
                    StartingBalance = StartingBalance,
                    DailyLossLimit = DailyLossLimit,
                    DailyLossAnchor = DailyLossAnchorMode,
                    MaxOverallLoss = MaxOverallLoss,
                    OverallLossAnchor = OverallLossAnchorMode,
                    LockProfitAt = LockProfitAt,
                    WarningPctOfDailyLoss = WarningPctOfDailyLoss
                };

                guard = new GuardState(rules);
                guard.Transition += (prev, next, reason) =>
                    Print(string.Format("[Guard] {0} -> {1} : {2}", prev, next, reason));

                historicalStore = new HistoricalLevelStore(
                    LookbackDays, LevelTouchToleranceTicks, meta.TickSize);

                sessionClock.SessionStarted += OnEthSessionStart;
                sessionClock.SessionEnded += OnEthSessionEnd;

                if (!string.IsNullOrWhiteSpace(JournalDir))
                {
                    try
                    {
                        Directory.CreateDirectory(JournalDir);
                        journalCsvPath = Path.Combine(JournalDir,
                            string.Format("njauto_{0}_{1:yyyyMMdd_HHmmss}.csv",
                                MasterSymbol, DateTime.Now));
                        File.AppendAllText(journalCsvPath,
                            "time,event,regime,bias,close,vwap,poc,vah,val,equity,dailyRoom,contracts,slTicks,reason\n");
                    }
                    catch (Exception ex)
                    {
                        Print(string.Format(
                            "Journal disabled - cannot use directory '{0}': {1}. " +
                            "Leave the input blank or set a valid Windows path " +
                            "(e.g. C:\\NjAuto\\journal).",
                            JournalDir, ex.Message));
                        journalCsvPath = null;
                    }
                }
            }
            else if (State == State.Realtime)
            {
                // Seed equity from the live account once we go realtime.
                rules.OnSessionStart(GetCurrentEquity());
                guard.OnSessionStart();
            }
        }

        protected override void OnBarUpdate()
        {
            // Tick series: accumulate volume into VP and VWAP.
            if (BarsInProgress == 1)
            {
                if (CurrentBars[1] < 1) return;
                double price = Closes[1][0];
                double volume = Volumes[1][0];
                profile.AddVolume(price, volume);
                vwap.AddTrade(price, volume);

                // Equity / guard updates on every tick.
                double equity = GetCurrentEquity();
                rules.OnEquityTick(equity);
                guard.OnEquityTick();
                return;
            }

            // Primary 1-min series: signals + session bookkeeping.
            if (BarsInProgress != 0) return;
            if (CurrentBar < BarsRequiredToTrade) return;

            sessionClock.OnTime(Time[0]);

            // Maintain VWAP slope, regime, and reclaim/POC counters on minute close.
            vwap.OnMinuteClose(Time[0], VwapSlopeLookbackMin);
            profile.Recompute();

            if (profile.HasData && vwap.HasData)
            {
                regime.OnMinuteClose(Close[0], vwap.Vwap, vwap.SlopePerMin,
                    profile.Poc, profile.Vah, profile.Val);
            }

            UpdateContextCounters();

            // Tally any new touches of historical levels on this minute close.
            historicalStore.OnMinuteClose(Low[0], High[0], Time[0]);

            UpdatePlots();
            UpdateHistoricalLevelPlots();

            if (!sessionClock.IsInSession(Time[0])) return;
            if (sessionClock.TimeIntoSession(Time[0]).TotalMinutes < SkipFirstMinutes) return;

            if (Position.MarketPosition != MarketPosition.Flat) return;
            if (EnableSelfAwareness && !guard.AllowsNewEntries) return;

            if ((Time[0] - lastExitTime).TotalMinutes < CooldownMinutes) return;
            if (GetCurrentSpreadTicks() > MaxSpreadTicks) return;

            TradeSignal sig = TryFindSetup();
            if (sig == null) return;

            TryEnter(sig);
        }

        private void OnEthSessionStart(DateTime sessionStartLocal)
        {
            Print("=== ETH session start: " + sessionStartLocal);
            profile.Reset();
            vwap.Reset();
            regime.Reset();
            barsAboveVwap = barsBelowVwap = 0;
            barsTouchingPocFromAbove = barsTouchingPocFromBelow = 0;
            lastSkipCategory = null;

            // Drop any historical level whose source session is older than
            // the configured lookback window.
            historicalStore.LookbackDays = LookbackDays;
            historicalStore.PruneOlderThan(sessionStartLocal);

            // Re-anchor equity bookkeeping for the new session.
            rules.OnSessionStart(GetCurrentEquity());
            guard.OnSessionStart();
        }

        private void OnEthSessionEnd(DateTime sessionEndLocal)
        {
            rules.OnSessionEnd(GetCurrentEquity());

            // Snapshot today's POC/VAH/VAL into the historical store so
            // future sessions can react to it.
            if (profile.HasData)
            {
                historicalStore.AddSessionLevels(
                    sessionEndLocal, profile.Poc, profile.Vah, profile.Val);
            }

            Print("=== ETH session end: " + sessionEndLocal +
                ", equity=" + GetCurrentEquity().ToString("F2"));
        }

        private void UpdatePlots()
        {
            if (!ShowChartPlots) return;

            // VWAP and bands
            if (vwap.HasData)
            {
                Values[0][0] = vwap.Vwap;
                Values[1][0] = vwap.UpperBand1;
                Values[2][0] = vwap.LowerBand1;
                Values[3][0] = vwap.UpperBand2;
                Values[4][0] = vwap.LowerBand2;
            }
            else
            {
                Values[0][0] = double.NaN;
                Values[1][0] = double.NaN;
                Values[2][0] = double.NaN;
                Values[3][0] = double.NaN;
                Values[4][0] = double.NaN;
            }

            // POC / VAH / VAL
            if (profile.HasData)
            {
                Values[5][0] = profile.Poc;
                Values[6][0] = profile.Vah;
                Values[7][0] = profile.Val;
            }
            else
            {
                Values[5][0] = double.NaN;
                Values[6][0] = double.NaN;
                Values[7][0] = double.NaN;
            }

            // Regime label, top-right corner.
            string sizingTag = EnableSelfAwareness
                ? string.Format("Guard: {0}   Equity: ${1:F0}   DailyRoom: ${2:F0}",
                    guard.Level, GetCurrentEquity(), rules.DailyLossRoom())
                : string.Format("Sizing: FIXED ({0}c x {1}t)",
                    FixedContracts, FixedSlTicks);

            string label = string.Format("Regime: {0} ({1})   {2}",
                regime.Current, regime.Bias, sizingTag);

            Brush regimeBrush =
                (EnableSelfAwareness && guard.Level != GuardLevel.Armed) ? Brushes.OrangeRed
                : regime.Current == Regime.Trend ? Brushes.MediumSeaGreen
                : regime.Current == Regime.Balance ? Brushes.DodgerBlue
                : Brushes.Gray;

            Draw.TextFixed(this, "njauto_regime", label,
                TextPosition.TopRight,
                regimeBrush,
                new SimpleFont("Consolas", 11),
                Brushes.Transparent, Brushes.Transparent, 0);
        }

        private void UpdateHistoricalLevelPlots()
        {
            if (!ShowChartPlots || !EnableHistoricalLevels) return;

            // Each level gets a stable tag so NT8 replaces the existing line
            // when the level's qualification status or price changes.
            foreach (var level in historicalStore.Levels)
            {
                string tag = string.Format("histlvl_{0:yyyyMMdd}_{1}",
                    level.CreatedAt, level.Type);

                bool qualified = level.TouchCount >= MinTouches;
                if (!qualified)
                {
                    Draw.HorizontalLine(this, tag, false, level.Price,
                        Brushes.Gray, DashStyleHelper.Dot, 1);
                    continue;
                }

                Brush brush =
                    level.Type == HistoricalLevelType.Poc ? Brushes.Goldenrod
                    : level.Type == HistoricalLevelType.Vah ? Brushes.MediumSeaGreen
                    : Brushes.IndianRed;

                Draw.HorizontalLine(this, tag, false, level.Price,
                    brush, DashStyleHelper.Dash, 2);

                // Touch-count label, anchored to current bar.
                string lblTag = tag + "_lbl";
                Draw.Text(this, lblTag,
                    string.Format("{0} ({1})", level.Type, level.TouchCount),
                    0, level.Price + meta.TickSize, brush);
            }
        }

        private void DrawEntryMarker(TradeSignal sig, double slPrice, double tpPrice, int contracts)
        {
            if (!ShowChartPlots) return;
            string tag = "njauto_entry_" + CurrentBar;

            if (sig.IsLong)
            {
                Draw.ArrowUp(this, tag, true, 0, Low[0] - 2 * meta.TickSize, Brushes.LimeGreen);
            }
            else
            {
                Draw.ArrowDown(this, tag, true, 0, High[0] + 2 * meta.TickSize, Brushes.OrangeRed);
            }

            Draw.Text(this, tag + "_lbl", string.Format("{0} {1}c", sig.Kind, contracts),
                0,
                sig.IsLong ? Low[0] - 6 * meta.TickSize : High[0] + 6 * meta.TickSize,
                sig.IsLong ? Brushes.LimeGreen : Brushes.OrangeRed);

            // Faint horizontal lines at SL and TP for the bar of entry, extending forward 30 bars.
            Draw.Line(this, tag + "_sl", false, 0, slPrice, -30, slPrice,
                Brushes.IndianRed, DashStyleHelper.Dash, 1);
            Draw.Line(this, tag + "_tp", false, 0, tpPrice, -30, tpPrice,
                Brushes.MediumSeaGreen, DashStyleHelper.Dash, 1);
        }

        private void UpdateContextCounters()
        {
            if (!vwap.HasData) return;

            if (Close[0] > vwap.Vwap) { barsAboveVwap++; barsBelowVwap = 0; }
            else if (Close[0] < vwap.Vwap) { barsBelowVwap++; barsAboveVwap = 0; }

            if (!profile.HasData) return;
            double tickProx = 2 * meta.TickSize;
            if (Math.Abs(Close[0] - profile.Poc) <= tickProx)
            {
                if (Open[0] > profile.Poc) barsTouchingPocFromAbove++;
                else barsTouchingPocFromBelow++;
            }
            else
            {
                barsTouchingPocFromAbove = 0;
                barsTouchingPocFromBelow = 0;
            }
        }

        private TradeSignal TryFindSetup()
        {
            if (!vwap.HasData) return null;

            // Historical-level setups carry their own validation (touch count)
            // and run independent of the regime classifier.
            if (EnableHistoricalLevels)
            {
                var histSig = TryFindHistoricalLevelSetup();
                if (histSig != null) return histSig;
            }

            if (!profile.HasData) return null;
            if (regime.Current == Regime.Undefined) return null;

            double close = Close[0];
            double tickTol = 2 * meta.TickSize;

            if (regime.Current == Regime.Balance && EnableBalanceSetups)
            {
                // Long at VAL: tagged val from above and closed back inside, above VWAP-2sigma.
                if (Low[0] <= profile.Val + tickTol
                    && close > profile.Val
                    && close > vwap.LowerBand2)
                {
                    return new TradeSignal
                    {
                        Kind = SetupKind.BalanceFadeVal,
                        IsLong = true,
                        EntryPrice = close,
                        StructuralTarget = profile.Poc,
                        Reason = "Balance: VAL fade long, target POC"
                    };
                }

                // Short at VAH.
                if (High[0] >= profile.Vah - tickTol
                    && close < profile.Vah
                    && close < vwap.UpperBand2)
                {
                    return new TradeSignal
                    {
                        Kind = SetupKind.BalanceFadeVah,
                        IsLong = false,
                        EntryPrice = close,
                        StructuralTarget = profile.Poc,
                        Reason = "Balance: VAH fade short, target POC"
                    };
                }

                // VWAP reclaim inside VA: was below VWAP for >=5 min, now closes above, still inside VA.
                if (close >= profile.Val && close <= profile.Vah)
                {
                    if (barsBelowVwap >= 5 && close > vwap.Vwap && Open[0] < vwap.Vwap)
                    {
                        return new TradeSignal
                        {
                            Kind = SetupKind.BalanceVwapReclaim,
                            IsLong = true,
                            EntryPrice = close,
                            StructuralTarget = profile.Vah,
                            Reason = "Balance: VWAP reclaim long, target VAH"
                        };
                    }
                    if (barsAboveVwap >= 5 && close < vwap.Vwap && Open[0] > vwap.Vwap)
                    {
                        return new TradeSignal
                        {
                            Kind = SetupKind.BalanceVwapReclaim,
                            IsLong = false,
                            EntryPrice = close,
                            StructuralTarget = profile.Val,
                            Reason = "Balance: VWAP reject short, target VAL"
                        };
                    }
                }

                // POC rejection: tested POC from above for >=3 min, never closed through, now turning down.
                if (barsTouchingPocFromAbove >= 3 && close > profile.Poc && close < Open[0])
                {
                    return new TradeSignal
                    {
                        Kind = SetupKind.BalanceRejectPoc,
                        IsLong = true,
                        EntryPrice = close,
                        StructuralTarget = profile.Vah,
                        Reason = "Balance: POC reject long, target VAH"
                    };
                }
                if (barsTouchingPocFromBelow >= 3 && close < profile.Poc && close > Open[0])
                {
                    return new TradeSignal
                    {
                        Kind = SetupKind.BalanceRejectPoc,
                        IsLong = false,
                        EntryPrice = close,
                        StructuralTarget = profile.Val,
                        Reason = "Balance: POC reject short, target VAL"
                    };
                }
            }

            if (regime.Current == Regime.Trend && EnableTrendSetups)
            {
                // Long acceptance above VAH: closed above VAH, rising VWAP, POC below VWAP.
                if (regime.Bias == TrendBias.Up
                    && close > profile.Vah
                    && vwap.SlopePerMin > 0
                    && profile.Poc < vwap.Vwap)
                {
                    double extension = profile.Vah + profile.ValueAreaWidth;
                    double bandTarget = vwap.UpperBand2;
                    double target = Math.Min(extension, bandTarget);
                    return new TradeSignal
                    {
                        Kind = SetupKind.TrendAcceptanceLong,
                        IsLong = true,
                        EntryPrice = close,
                        StructuralTarget = target,
                        Reason = "Trend: acceptance above VAH"
                    };
                }
                if (regime.Bias == TrendBias.Down
                    && close < profile.Val
                    && vwap.SlopePerMin < 0
                    && profile.Poc > vwap.Vwap)
                {
                    double extension = profile.Val - profile.ValueAreaWidth;
                    double bandTarget = vwap.LowerBand2;
                    double target = Math.Max(extension, bandTarget);
                    return new TradeSignal
                    {
                        Kind = SetupKind.TrendAcceptanceShort,
                        IsLong = false,
                        EntryPrice = close,
                        StructuralTarget = target,
                        Reason = "Trend: acceptance below VAL"
                    };
                }

                // VWAP-band continuation: in trend, fade extreme back to VWAP.
                if (regime.Bias == TrendBias.Up && Low[0] <= vwap.LowerBand2 + tickTol && close > vwap.LowerBand2)
                {
                    return new TradeSignal
                    {
                        Kind = SetupKind.TrendBandFade,
                        IsLong = true,
                        EntryPrice = close,
                        StructuralTarget = vwap.Vwap,
                        Reason = "Trend: -2sigma band fade long"
                    };
                }
                if (regime.Bias == TrendBias.Down && High[0] >= vwap.UpperBand2 - tickTol && close < vwap.UpperBand2)
                {
                    return new TradeSignal
                    {
                        Kind = SetupKind.TrendBandFade,
                        IsLong = false,
                        EntryPrice = close,
                        StructuralTarget = vwap.Vwap,
                        Reason = "Trend: +2sigma band fade short"
                    };
                }
            }

            return null;
        }

        private TradeSignal TryFindHistoricalLevelSetup()
        {
            var level = historicalStore.FindTouchedQualifyingLevel(
                Low[0], High[0], Close[0], MinTouches);
            if (level == null) return null;

            // Trade direction is set by where VWAP sits relative to current price.
            // If VWAP is above price, target reverts UP to VWAP - long. Mirror short.
            double distanceToVwap = vwap.Vwap - Close[0];
            if (Math.Abs(distanceToVwap) < meta.TickSize) return null; // sitting on VWAP

            bool isLong = distanceToVwap > 0;
            return new TradeSignal
            {
                Kind = SetupKind.HistoricalLevelVwapRevert,
                IsLong = isLong,
                EntryPrice = Close[0],
                StructuralTarget = vwap.Vwap,
                Reason = string.Format(
                    "HistLvl {0}@{1:F2} touches={2} -> revert to VWAP {3:F2}",
                    level.Type, level.Price, level.TouchCount, vwap.Vwap)
            };
        }

        private void TryEnter(TradeSignal sig)
        {
            PositionSizer.SizingDecision decision;
            if (EnableSelfAwareness)
            {
                double netProfit = rules.NetProfit();
                double dailyRoom = rules.DailyLossRoom();
                decision = sizer.Compute(netProfit, dailyRoom);
            }
            else
            {
                // Fixed-size mode: ignore propfirm scaling/daily-room math.
                if (FixedContracts <= 0 || FixedSlTicks < MinSlTicks)
                {
                    if (VerboseLog) Print(string.Format(
                        "[Skip] Fixed sizing invalid: contracts={0}, slTicks={1} (min {2})",
                        FixedContracts, FixedSlTicks, MinSlTicks));
                    return;
                }
                decision = new PositionSizer.SizingDecision
                {
                    TakeTrade = true,
                    Contracts = FixedContracts,
                    SlTicks = FixedSlTicks,
                    Code = PositionSizer.SkipCode.None,
                    Reason = string.Format("Fixed: {0}c x {1}t = ${2:F2} risk",
                        FixedContracts, FixedSlTicks,
                        FixedContracts * FixedSlTicks * meta.TickValue)
                };
            }

            if (!decision.TakeTrade)
            {
                // Dedupe by category - the full reason includes numbers that
                // change every bar and would defeat per-reason dedup.
                string category = "sizing/" + decision.Code;
                if (VerboseLog && category != lastSkipCategory)
                {
                    Print("[Skip] " + decision.Reason);
                    lastSkipCategory = category;
                }
                Journal("skip", sig, decision);
                return;
            }
            lastSkipCategory = null;

            double slDistance = decision.SlTicks * meta.TickSize;
            double slPrice = sig.IsLong ? sig.EntryPrice - slDistance : sig.EntryPrice + slDistance;
            double tpPrice = sig.StructuralTarget;
            double tpDistance = Math.Abs(tpPrice - sig.EntryPrice);

            if (tpDistance < MinRMultiple * slDistance)
            {
                const string minRCategory = "minR";
                if (VerboseLog && minRCategory != lastSkipCategory)
                {
                    Print(string.Format(
                        "[Skip] TP below {0}R floor (most recent: {1:F2} vs {2:F2})",
                        MinRMultiple, tpDistance, MinRMultiple * slDistance));
                    lastSkipCategory = minRCategory;
                }
                Journal("skip-minR", sig, decision);
                return;
            }

            activeSignal = sig.Kind.ToString();
            pendingSlPrice = slPrice;
            pendingTpPrice = tpPrice;
            pendingContracts = decision.Contracts;

            SetStopLoss(activeSignal, CalculationMode.Price, slPrice, false);
            SetProfitTarget(activeSignal, CalculationMode.Price, tpPrice);

            if (sig.IsLong)
                EnterLong(decision.Contracts, activeSignal);
            else
                EnterShort(decision.Contracts, activeSignal);

            Print(string.Format(
                "[Enter] {0} {1}c @ ~{2:F2}  SL={3:F2}({4}t)  TP={5:F2}  reason={6}",
                sig.IsLong ? "LONG" : "SHORT",
                decision.Contracts, sig.EntryPrice, slPrice, decision.SlTicks, tpPrice, sig.Reason));

            DrawEntryMarker(sig, slPrice, tpPrice, decision.Contracts);
            Journal("enter", sig, decision);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            // Track realized PnL on closing executions.
            if (execution.Order != null
                && (execution.Order.OrderState == OrderState.Filled
                    || execution.Order.OrderState == OrderState.PartFilled))
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    // Position just went flat; refresh realized PnL from system performance.
                    realizedPnL = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
                    lastExitTime = time;
                    activeSignal = null;
                }
            }
        }

        // Equity = startingBalance + realized + unrealized. Works in backtest and live.
        private double GetCurrentEquity()
        {
            double unrealized = 0;
            if (Position.MarketPosition != MarketPosition.Flat && CurrentBar > 0)
            {
                unrealized = Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]);
            }
            double realized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            return StartingBalance + realized + unrealized;
        }

        private int GetCurrentSpreadTicks()
        {
            // NT8 doesn't expose live bid/ask cleanly in OnBarUpdate of backtest;
            // fall back to a synthetic 1-tick spread when not available.
            if (GetCurrentBid() > 0 && GetCurrentAsk() > 0)
            {
                double spread = GetCurrentAsk() - GetCurrentBid();
                return (int)Math.Round(spread / meta.TickSize);
            }
            return 1;
        }

        private void Journal(string evt, TradeSignal sig, PositionSizer.SizingDecision dec)
        {
            if (string.IsNullOrEmpty(journalCsvPath)) return;
            try
            {
                File.AppendAllText(journalCsvPath, string.Format(
                    "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4:F2},{5:F2},{6:F2},{7:F2},{8:F2},{9:F2},{10:F2},{11},{12},{13}\n",
                    Time[0], evt, regime.Current, regime.Bias, Close[0],
                    vwap.Vwap, profile.Poc, profile.Vah, profile.Val,
                    GetCurrentEquity(), rules.DailyLossRoom(),
                    dec.Contracts, dec.SlTicks,
                    (sig != null ? sig.Reason : "") + " | " + dec.Reason));
            }
            catch { /* ignore journal errors */ }
        }
    }
}
