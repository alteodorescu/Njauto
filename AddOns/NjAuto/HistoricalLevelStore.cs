// Stores Volume Profile levels (POC / VAH / VAL) snapshotted at the end
// of each ETH session, prunes levels older than LookbackDays, and
// counts how many times subsequent price action touches each level.
//
// Touch definition: a bar's [low - tol, high + tol] range contains the
// level price. To avoid double-counting an extended interaction, we
// only count a NEW touch when the previous bar was NOT touching - so
// each "approach" registers as one touch regardless of how many bars
// the price hovers near the level.

using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaTrader.NinjaScript.AddOns.NjAuto
{
    public sealed class HistoricalLevelStore
    {
        private readonly List<HistoricalLevel> levels = new List<HistoricalLevel>();
        private readonly double touchTolerance;

        public int LookbackDays { get; set; }

        public HistoricalLevelStore(int lookbackDays, int touchToleranceTicks, double tickSize)
        {
            LookbackDays = lookbackDays;
            touchTolerance = touchToleranceTicks * tickSize;
        }

        public IReadOnlyList<HistoricalLevel> Levels => levels;

        public void AddSessionLevels(DateTime sessionEndTime, double poc, double vah, double val)
        {
            levels.Add(new HistoricalLevel { CreatedAt = sessionEndTime, Type = HistoricalLevelType.Poc, Price = poc });
            levels.Add(new HistoricalLevel { CreatedAt = sessionEndTime, Type = HistoricalLevelType.Vah, Price = vah });
            levels.Add(new HistoricalLevel { CreatedAt = sessionEndTime, Type = HistoricalLevelType.Val, Price = val });
        }

        public void PruneOlderThan(DateTime now)
        {
            DateTime cutoff = now.AddDays(-LookbackDays);
            levels.RemoveAll(l => l.CreatedAt < cutoff);
        }

        // Update touch counts for every level whose creation predates this bar.
        // Levels created in the still-running session don't count their own
        // formation as touches.
        public void OnMinuteClose(double barLow, double barHigh, DateTime barTime)
        {
            foreach (var level in levels)
            {
                if (barTime <= level.CreatedAt)
                {
                    level.WasTouchingLastBar = false;
                    continue;
                }

                bool touching =
                    (barLow - touchTolerance) <= level.Price &&
                    level.Price <= (barHigh + touchTolerance);

                if (touching && !level.WasTouchingLastBar)
                {
                    level.TouchCount++;
                    level.LastTouchedAt = barTime;
                }
                level.WasTouchingLastBar = touching;
            }
        }

        // Return the qualifying level closest to the given price, or null.
        public HistoricalLevel FindTouchedQualifyingLevel(
            double barLow, double barHigh, double currentPrice, int minTouches)
        {
            HistoricalLevel best = null;
            double bestDist = double.MaxValue;

            foreach (var level in levels)
            {
                if (level.TouchCount < minTouches) continue;

                bool touching =
                    (barLow - touchTolerance) <= level.Price &&
                    level.Price <= (barHigh + touchTolerance);
                if (!touching) continue;

                double dist = Math.Abs(currentPrice - level.Price);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = level;
                }
            }
            return best;
        }

        public IEnumerable<HistoricalLevel> Qualifying(int minTouches)
        {
            return levels.Where(l => l.TouchCount >= minTouches);
        }
    }
}
