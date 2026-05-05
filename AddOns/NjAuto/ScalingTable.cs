// Propfirm scaling table: maps current net profit ($) to max contracts.
// Configured by the user as a semicolon-separated string of
// "minNetProfit:maxContracts" rows. Tiers are sorted ascending by
// minNetProfit. Lookup returns the maxContracts for the highest tier
// whose threshold is <= currentNetProfit.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NinjaTrader.NinjaScript.AddOns.NjAuto
{
    public sealed class ScalingTable
    {
        private readonly List<Tier> tiers = new List<Tier>();

        public struct Tier
        {
            public double MinNetProfit;
            public int MaxContracts;
        }

        public IReadOnlyList<Tier> Tiers => tiers;

        // Format: "0:2;1000:4;2500:6;5000:10"
        public static ScalingTable Parse(string spec)
        {
            var t = new ScalingTable();
            if (string.IsNullOrWhiteSpace(spec)) return t;

            foreach (var row in spec.Split(';'))
            {
                var trimmed = row.Trim();
                if (trimmed.Length == 0) continue;
                var parts = trimmed.Split(':');
                if (parts.Length != 2) continue;

                if (double.TryParse(parts[0].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double minProfit)
                    && int.TryParse(parts[1].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int contracts)
                    && contracts > 0)
                {
                    t.tiers.Add(new Tier { MinNetProfit = minProfit, MaxContracts = contracts });
                }
            }
            t.tiers.Sort((a, b) => a.MinNetProfit.CompareTo(b.MinNetProfit));
            return t;
        }

        public int MaxContractsFor(double currentNetProfit)
        {
            if (tiers.Count == 0) return 0;

            // Floor: if profit is below the lowest tier's threshold, return that
            // tier's contract count. The propfirm's daily-loss / DD rules — not
            // the scaling table — are what halt trading on a losing day.
            if (currentNetProfit < tiers[0].MinNetProfit)
                return tiers[0].MaxContracts;

            int contracts = tiers[0].MaxContracts;
            for (int i = 0; i < tiers.Count; i++)
            {
                if (currentNetProfit >= tiers[i].MinNetProfit)
                    contracts = tiers[i].MaxContracts;
                else
                    break;
            }
            return contracts;
        }
    }
}
