// ETH-anchored Volume Profile.
//
// Bins are keyed by floor(priceTicks / binTicks) so they survive across
// different absolute prices. Volume is accumulated tick-by-tick from the
// strategy's tick series (the strategy calls AddVolume for every market
// data print). VAH/VAL/POC are computed by symmetric expansion outward
// from POC until cumulative volume >= valueAreaPct of total.

using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.AddOns.NjAuto
{
    public sealed class AnchoredVolumeProfile
    {
        private readonly InstrumentMeta meta;
        private readonly double valueAreaPct;
        private readonly Dictionary<long, double> bins = new Dictionary<long, double>();
        private double totalVolume;

        public AnchoredVolumeProfile(InstrumentMeta meta, double valueAreaPct)
        {
            this.meta = meta ?? throw new ArgumentNullException(nameof(meta));
            this.valueAreaPct = valueAreaPct;
        }

        public double Poc { get; private set; }
        public double Vah { get; private set; }
        public double Val { get; private set; }
        public double TotalVolume => totalVolume;
        public double ValueAreaWidth => Math.Max(0, Vah - Val);
        public bool HasData => totalVolume > 0;

        public void Reset()
        {
            bins.Clear();
            totalVolume = 0;
            Poc = Vah = Val = 0;
        }

        public void AddVolume(double price, double volume)
        {
            if (volume <= 0 || meta.TickSize <= 0 || meta.BinTicks <= 0) return;

            long key = PriceToKey(price);
            if (bins.TryGetValue(key, out double existing))
                bins[key] = existing + volume;
            else
                bins[key] = volume;

            totalVolume += volume;
        }

        // Recompute POC/VAH/VAL. Cheap enough to call on every minute close.
        public void Recompute()
        {
            if (bins.Count == 0)
            {
                Poc = Vah = Val = 0;
                return;
            }

            // Find POC bin
            long pocKey = 0;
            double pocVol = -1;
            foreach (var kv in bins)
            {
                if (kv.Value > pocVol)
                {
                    pocVol = kv.Value;
                    pocKey = kv.Key;
                }
            }
            Poc = KeyToPrice(pocKey);

            // Expand outward symmetrically from POC until we cover valueAreaPct
            double target = totalVolume * valueAreaPct;
            double accumulated = pocVol;
            long highKey = pocKey;
            long lowKey = pocKey;

            while (accumulated < target)
            {
                long upCandidate = highKey + 1;
                long downCandidate = lowKey - 1;
                bins.TryGetValue(upCandidate, out double upVol);
                bins.TryGetValue(downCandidate, out double downVol);

                if (upVol <= 0 && downVol <= 0)
                {
                    // No more bins on either side; stop.
                    break;
                }

                if (upVol >= downVol)
                {
                    accumulated += upVol;
                    highKey = upCandidate;
                }
                else
                {
                    accumulated += downVol;
                    lowKey = downCandidate;
                }
            }

            // Bin keys represent the lower edge of the bin; VAH should be
            // the upper edge of the highest accepted bin.
            Vah = KeyToPrice(highKey) + meta.BinTicks * meta.TickSize;
            Val = KeyToPrice(lowKey);
        }

        private long PriceToKey(double price)
        {
            long ticks = (long)Math.Round(price / meta.TickSize);
            return ticks / meta.BinTicks;
        }

        private double KeyToPrice(long key)
        {
            return key * meta.BinTicks * meta.TickSize;
        }
    }
}
