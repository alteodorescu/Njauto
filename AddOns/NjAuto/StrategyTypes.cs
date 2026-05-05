// Shared enums and DTOs used across NjAuto modules.
// Kept in NinjaTrader.NinjaScript.AddOns so all NinjaScript code in the
// custom assembly can reference these without extra using directives.

using System;

namespace NinjaTrader.NinjaScript.AddOns.NjAuto
{
    public enum Regime
    {
        Undefined,
        Balance,
        Trend
    }

    public enum TrendBias
    {
        None,
        Up,
        Down
    }

    public enum SetupKind
    {
        None,
        BalanceFadeVal,
        BalanceFadeVah,
        BalanceVwapReclaim,
        BalanceRejectPoc,
        TrendAcceptanceLong,
        TrendAcceptanceShort,
        TrendBandFade,
        HistoricalLevelVwapRevert
    }

    public enum DailyLossAnchor
    {
        StartOfDayEquity,
        IntradayHighEquity
    }

    public enum OverallLossAnchor
    {
        StartingBalance,
        TrailingHighEodBalance,
        TrailingHighIntradayEquity
    }

    public enum GuardLevel
    {
        Armed,
        Warning,
        SoftHalt,
        Locked
    }

    public sealed class TradeSignal
    {
        public SetupKind Kind { get; set; }
        public bool IsLong { get; set; }
        public double EntryPrice { get; set; }
        public double StructuralTarget { get; set; }
        public string Reason { get; set; }
    }

    public sealed class InstrumentMeta
    {
        public string MasterSymbol { get; set; }   // e.g. "ES", "MNQ"
        public double TickSize { get; set; }
        public double TickValue { get; set; }
        public int BinTicks { get; set; }
        public int EthOpenHourEt { get; set; }     // 18 for CME
        public int EthOpenMinuteEt { get; set; }   // 0
        public int EthCloseHourEt { get; set; }    // 17
        public int EthCloseMinuteEt { get; set; }  // 0
        public int MaintenanceBreakMinutes { get; set; } // 60 for CME index
    }
}
