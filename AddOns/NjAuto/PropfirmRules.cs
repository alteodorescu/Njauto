// Generic propfirm rule engine.
//
// Tracks the equity anchor (start-of-day or intraday-high), trailing
// drawdown anchor (configurable), and computes "daily loss room" for
// the position sizer. The strategy feeds it equity updates on every
// tick and session-start signals from the SessionClock.

using System;

namespace NinjaTrader.NinjaScript.AddOns.NjAuto
{
    public sealed class PropfirmRules
    {
        public double StartingBalance { get; set; }
        public double DailyLossLimit { get; set; }
        public DailyLossAnchor DailyLossAnchor { get; set; }
        public double MaxOverallLoss { get; set; }
        public OverallLossAnchor OverallLossAnchor { get; set; }
        public double LockProfitAt { get; set; } // Apex-style: trailing -> static once profit >= this
        public double WarningPctOfDailyLoss { get; set; } = 0.80;

        // When true, the overall floor is forced to (StartingBalance - MaxOverallLoss)
        // regardless of OverallLossAnchor or LockProfitAt. Useful when you want a
        // simple fixed DD ceiling rather than a trailing one.
        public bool UseFixedOverallFloor { get; set; } = false;

        // Live state
        public double SessionStartEquity { get; private set; }
        public double IntradayHighEquity { get; private set; }
        public double TrailingHighEodBalance { get; private set; }
        public double TrailingHighIntradayEquity { get; private set; }
        public double CurrentEquity { get; private set; }
        public bool TrailingLockedToStatic { get; private set; }
        public double EffectiveOverallFloor { get; private set; }

        public void OnSessionStart(double equityAtStart)
        {
            SessionStartEquity = equityAtStart;
            IntradayHighEquity = equityAtStart;
            CurrentEquity = equityAtStart;

            if (TrailingHighEodBalance == 0)
                TrailingHighEodBalance = StartingBalance;
            if (TrailingHighIntradayEquity == 0)
                TrailingHighIntradayEquity = StartingBalance;

            RecomputeOverallFloor();
        }

        public void OnEquityTick(double equity)
        {
            CurrentEquity = equity;
            if (equity > IntradayHighEquity) IntradayHighEquity = equity;
            if (equity > TrailingHighIntradayEquity) TrailingHighIntradayEquity = equity;
            RecomputeOverallFloor();
        }

        public void OnSessionEnd(double endOfDayBalance)
        {
            if (endOfDayBalance > TrailingHighEodBalance)
                TrailingHighEodBalance = endOfDayBalance;
            RecomputeOverallFloor();
        }

        private void RecomputeOverallFloor()
        {
            // Hard override: fixed floor at StartingBalance - MaxOverallLoss.
            if (UseFixedOverallFloor)
            {
                EffectiveOverallFloor = StartingBalance - MaxOverallLoss;
                return;
            }

            if (LockProfitAt > 0)
            {
                double profitFromStart = TrailingHighIntradayEquity - StartingBalance;
                if (profitFromStart >= LockProfitAt) TrailingLockedToStatic = true;
            }

            if (TrailingLockedToStatic)
            {
                EffectiveOverallFloor = StartingBalance - MaxOverallLoss;
                return;
            }

            switch (OverallLossAnchor)
            {
                case OverallLossAnchor.StartingBalance:
                    EffectiveOverallFloor = StartingBalance - MaxOverallLoss;
                    break;
                case OverallLossAnchor.TrailingHighEodBalance:
                    EffectiveOverallFloor = TrailingHighEodBalance - MaxOverallLoss;
                    break;
                case OverallLossAnchor.TrailingHighIntradayEquity:
                    EffectiveOverallFloor = TrailingHighIntradayEquity - MaxOverallLoss;
                    break;
            }
        }

        public double DailyLossRoom()
        {
            double anchor = DailyLossAnchor == DailyLossAnchor.IntradayHighEquity
                ? IntradayHighEquity
                : SessionStartEquity;
            double drawdown = Math.Max(0, anchor - CurrentEquity);
            return Math.Max(0, DailyLossLimit - drawdown);
        }

        public double OverallLossRoom()
        {
            return Math.Max(0, CurrentEquity - EffectiveOverallFloor);
        }

        public double NetProfit()
        {
            return CurrentEquity - StartingBalance;
        }

        // Returns the highest-severity guard level the current equity warrants.
        public GuardLevel EvaluateGuard()
        {
            double dailyRoom = DailyLossRoom();
            double overallRoom = OverallLossRoom();

            // Overall DD breach is the hardest stop -> Locked (no further trading at all).
            if (overallRoom <= 0)
                return GuardLevel.Locked;

            // Daily limit reached: SoftHalt - no new entries, open trade kept running.
            if (dailyRoom <= 0)
                return GuardLevel.SoftHalt;

            if (dailyRoom <= DailyLossLimit * (1.0 - WarningPctOfDailyLoss))
                return GuardLevel.Warning;

            return GuardLevel.Armed;
        }
    }
}
