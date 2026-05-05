// A single historical Volume Profile level (POC / VAH / VAL) carried
// forward from a prior ETH session. Levels accumulate touches across
// subsequent sessions and become "qualified" once their touch count
// reaches the user's MinTouches threshold.

using System;

namespace NinjaTrader.NinjaScript.AddOns.NjAuto
{
    public enum HistoricalLevelType
    {
        Poc,
        Vah,
        Val
    }

    public sealed class HistoricalLevel
    {
        public DateTime CreatedAt;          // When the source ETH session ended
        public HistoricalLevelType Type;
        public double Price;
        public int TouchCount;
        public DateTime LastTouchedAt;
        public bool WasTouchingLastBar;     // For touch debounce
    }
}
