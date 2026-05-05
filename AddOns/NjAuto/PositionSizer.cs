// Computes contracts and stop-loss distance for the next trade.
//
// Sizing model (per user spec):
//   contracts  = scalingTable.MaxContractsFor(currentNetProfit)
//   slTicks    = floor((dailyLossRoom - safetyBuffer) / (contracts * tickValue))
//   slTicks    is then clamped to [minSlTicks, maxSlTicks]
//   if slTicks < minSlTicks  -> SkipTrade (insufficient daily room)
//
// SL price is computed by the caller from slTicks and the chosen entry.

using System;

namespace NinjaTrader.NinjaScript.AddOns.NjAuto
{
    public sealed class PositionSizer
    {
        public enum SkipCode
        {
            None,
            NoTier,
            DailyRoomZero,
            SlTooTight
        }

        public sealed class SizingDecision
        {
            public bool TakeTrade;
            public int Contracts;
            public int SlTicks;
            public string Reason;
            public SkipCode Code;
        }

        private readonly ScalingTable scaling;
        private readonly InstrumentMeta meta;
        private readonly double safetyBuffer;
        private readonly int minSlTicks;
        private readonly int maxSlTicks;

        public PositionSizer(ScalingTable scaling, InstrumentMeta meta,
            double safetyBuffer, int minSlTicks, int maxSlTicks)
        {
            this.scaling = scaling;
            this.meta = meta;
            this.safetyBuffer = safetyBuffer;
            this.minSlTicks = minSlTicks;
            this.maxSlTicks = maxSlTicks;
        }

        public SizingDecision Compute(double currentNetProfit, double dailyLossRoom)
        {
            int contracts = scaling.MaxContractsFor(currentNetProfit);
            if (contracts <= 0)
            {
                return new SizingDecision
                {
                    TakeTrade = false,
                    Contracts = 0,
                    SlTicks = 0,
                    Code = SkipCode.NoTier,
                    Reason = "No scaling tier matched (contracts = 0)"
                };
            }

            double effectiveRoom = dailyLossRoom - safetyBuffer;
            if (effectiveRoom <= 0)
            {
                return new SizingDecision
                {
                    TakeTrade = false,
                    Contracts = contracts,
                    SlTicks = 0,
                    Code = SkipCode.DailyRoomZero,
                    Reason = "Daily loss room exhausted"
                };
            }

            double dollarsPerTickAtSize = contracts * meta.TickValue;
            int rawSlTicks = (int)Math.Floor(effectiveRoom / dollarsPerTickAtSize);

            if (rawSlTicks < minSlTicks)
            {
                return new SizingDecision
                {
                    TakeTrade = false,
                    Contracts = contracts,
                    SlTicks = rawSlTicks,
                    Code = SkipCode.SlTooTight,
                    Reason = string.Format(
                        "SL ticks {0} below minimum {1} at {2} contracts",
                        rawSlTicks, minSlTicks, contracts)
                };
            }

            int finalSlTicks = Math.Min(rawSlTicks, maxSlTicks);

            return new SizingDecision
            {
                TakeTrade = true,
                Contracts = contracts,
                SlTicks = finalSlTicks,
                Code = SkipCode.None,
                Reason = string.Format("OK: {0}c x {1}t = ${2:F2} risk",
                    contracts, finalSlTicks, finalSlTicks * dollarsPerTickAtSize)
            };
        }
    }
}
