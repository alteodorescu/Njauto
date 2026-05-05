// Guard state machine. Wraps PropfirmRules.EvaluateGuard with a layer
// that latches SoftHalt and Locked until the next session start, so a
// brief equity spike can't re-arm trading after the daily limit is hit.

using System;

namespace NinjaTrader.NinjaScript.AddOns.NjAuto
{
    public sealed class GuardState
    {
        private readonly PropfirmRules rules;
        public GuardLevel Level { get; private set; } = GuardLevel.Armed;

        public event Action<GuardLevel, GuardLevel, string> Transition;

        public GuardState(PropfirmRules rules)
        {
            this.rules = rules;
        }

        public bool AllowsNewEntries =>
            Level == GuardLevel.Armed;

        public void OnSessionStart()
        {
            Set(GuardLevel.Armed, "Session start - re-armed");
        }

        public void OnEquityTick()
        {
            // Latching: never downgrade severity within a session.
            GuardLevel computed = rules.EvaluateGuard();
            if (Compare(computed, Level) > 0)
                Set(computed, BuildReason(computed));
        }

        private static int Compare(GuardLevel a, GuardLevel b)
        {
            return ((int)a).CompareTo((int)b);
        }

        private void Set(GuardLevel next, string reason)
        {
            if (next == Level) return;
            var prev = Level;
            Level = next;
            Transition?.Invoke(prev, next, reason);
        }

        private string BuildReason(GuardLevel l)
        {
            switch (l)
            {
                case GuardLevel.Warning:
                    return string.Format("Warning: daily room ${0:F2} (<= {1:P0} of limit)",
                        rules.DailyLossRoom(), 1.0 - rules.WarningPctOfDailyLoss);
                case GuardLevel.SoftHalt:
                    return string.Format("SoftHalt: daily room ${0:F2}", rules.DailyLossRoom());
                case GuardLevel.Locked:
                    return string.Format(
                        "Locked: dailyRoom=${0:F2}, overallRoom=${1:F2}",
                        rules.DailyLossRoom(), rules.OverallLossRoom());
                default:
                    return "Armed";
            }
        }
    }
}
