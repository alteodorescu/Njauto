// ETH-anchored VWAP and standard-deviation bands.
//
// Computed cumulatively per tick using typical price = (h+l+c)/3 ~= last
// trade price (we use the trade price directly since we are running on
// market data prints, which is the standard NT8 approach for tick VWAP).
// Variance is the volume-weighted second moment around VWAP, taken as
// E[p^2] - E[p]^2 to avoid a second pass.

using System;

namespace NinjaTrader.NinjaScript.AddOns.NjAuto
{
    public sealed class AnchoredVwap
    {
        private double sumPv;     // Sum(price * volume)
        private double sumP2v;    // Sum(price^2 * volume)
        private double sumV;      // Sum(volume)
        private double lastSlopePerMin;
        private double prevVwapForSlope;
        private DateTime prevSlopeStamp = DateTime.MinValue;

        public double Vwap { get; private set; }
        public double StdDev { get; private set; }
        public double UpperBand1 => Vwap + StdDev;
        public double LowerBand1 => Vwap - StdDev;
        public double UpperBand2 => Vwap + 2 * StdDev;
        public double LowerBand2 => Vwap - 2 * StdDev;
        public double SlopePerMin => lastSlopePerMin;
        public bool HasData => sumV > 0;

        public void Reset()
        {
            sumPv = sumP2v = sumV = 0;
            Vwap = StdDev = 0;
            lastSlopePerMin = 0;
            prevVwapForSlope = 0;
            prevSlopeStamp = DateTime.MinValue;
        }

        public void AddTrade(double price, double volume)
        {
            if (volume <= 0) return;
            sumPv += price * volume;
            sumP2v += price * price * volume;
            sumV += volume;

            Vwap = sumPv / sumV;
            double variance = (sumP2v / sumV) - (Vwap * Vwap);
            StdDev = variance > 0 ? Math.Sqrt(variance) : 0;
        }

        // Call on each minute close to maintain a coarse slope estimate
        // (VWAP delta over the last `lookbackMinutes` minutes).
        public void OnMinuteClose(DateTime barTime, int lookbackMinutes)
        {
            if (prevSlopeStamp == DateTime.MinValue)
            {
                prevSlopeStamp = barTime;
                prevVwapForSlope = Vwap;
                return;
            }

            double minutesElapsed = (barTime - prevSlopeStamp).TotalMinutes;
            if (minutesElapsed >= lookbackMinutes)
            {
                lastSlopePerMin = (Vwap - prevVwapForSlope) / Math.Max(1, minutesElapsed);
                prevSlopeStamp = barTime;
                prevVwapForSlope = Vwap;
            }
        }
    }
}
