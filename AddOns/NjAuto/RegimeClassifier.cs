// Classifies the current ETH session as Balance, Trend, or Undefined.
// Inputs are recomputed each minute close. Tracks how many minutes
// closed inside [VAL,VAH] for the time-in-VA test.

using System;

namespace NinjaTrader.NinjaScript.AddOns.NjAuto
{
    public sealed class RegimeClassifier
    {
        private readonly int warmupMinutes;
        private readonly double balancePocVwapDistanceFraction;
        private readonly double trendPocVwapDistanceFraction;
        private readonly double minTimeInVaFraction;
        private readonly double trendVwapSlopeMinTicksPerMin;
        private readonly double tickSize;

        private int barsSinceSessionStart;
        private int barsInsideVa;
        private double vahAtWarmup;
        private double valAtWarmup;
        private bool warmupCaptured;

        public Regime Current { get; private set; } = Regime.Undefined;
        public TrendBias Bias { get; private set; } = TrendBias.None;

        public RegimeClassifier(
            int warmupMinutes,
            double balancePocVwapDistanceFraction,
            double trendPocVwapDistanceFraction,
            double minTimeInVaFraction,
            double trendVwapSlopeMinTicksPerMin,
            double tickSize)
        {
            this.warmupMinutes = warmupMinutes;
            this.balancePocVwapDistanceFraction = balancePocVwapDistanceFraction;
            this.trendPocVwapDistanceFraction = trendPocVwapDistanceFraction;
            this.minTimeInVaFraction = minTimeInVaFraction;
            this.trendVwapSlopeMinTicksPerMin = trendVwapSlopeMinTicksPerMin;
            this.tickSize = tickSize;
        }

        public void Reset()
        {
            barsSinceSessionStart = 0;
            barsInsideVa = 0;
            vahAtWarmup = valAtWarmup = 0;
            warmupCaptured = false;
            Current = Regime.Undefined;
            Bias = TrendBias.None;
        }

        public void OnMinuteClose(double closePrice, double vwap, double vwapSlopePerMin,
            double poc, double vah, double val)
        {
            barsSinceSessionStart++;
            if (closePrice >= val && closePrice <= vah) barsInsideVa++;

            if (!warmupCaptured && barsSinceSessionStart >= warmupMinutes)
            {
                vahAtWarmup = vah;
                valAtWarmup = val;
                warmupCaptured = true;
            }

            if (!warmupCaptured || vah <= val)
            {
                Current = Regime.Undefined;
                Bias = TrendBias.None;
                return;
            }

            double vaWidth = vah - val;
            double pocVwapDist = Math.Abs(vwap - poc);
            double timeInVa = (double)barsInsideVa / Math.Max(1, barsSinceSessionStart);
            double slopeTicks = vwapSlopePerMin / Math.Max(tickSize, 1e-9);

            bool balance =
                pocVwapDist < balancePocVwapDistanceFraction * vaWidth
                && Math.Abs(slopeTicks) < trendVwapSlopeMinTicksPerMin
                && timeInVa >= minTimeInVaFraction;

            double migration = Math.Max(
                Math.Abs(vah - vahAtWarmup),
                Math.Abs(val - valAtWarmup));

            bool trend =
                pocVwapDist > trendPocVwapDistanceFraction * vaWidth
                || Math.Abs(slopeTicks) > trendVwapSlopeMinTicksPerMin
                || migration > vaWidth;

            if (balance && !trend)
            {
                Current = Regime.Balance;
                Bias = TrendBias.None;
            }
            else if (trend)
            {
                Current = Regime.Trend;
                Bias = (vwap > poc && slopeTicks >= 0) ? TrendBias.Up
                     : (vwap < poc && slopeTicks <= 0) ? TrendBias.Down
                     : (slopeTicks > 0 ? TrendBias.Up : TrendBias.Down);
            }
            else
            {
                Current = Regime.Undefined;
                Bias = TrendBias.None;
            }
        }
    }
}
