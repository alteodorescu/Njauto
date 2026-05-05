// Resolves the ETH session window for an instrument and exposes
// session-start/session-end events to subscribers. All times are
// converted from Eastern Time (handles DST automatically) to the
// instrument bar's local time so they line up with NT8 timestamps.

using System;

namespace NinjaTrader.NinjaScript.AddOns.NjAuto
{
    public sealed class SessionClock
    {
        private static readonly TimeZoneInfo EasternTz =
            TryGetTz("Eastern Standard Time") ?? TryGetTz("America/New_York")
            ?? TimeZoneInfo.Local;

        private readonly InstrumentMeta meta;
        private DateTime currentSessionStartLocal = DateTime.MinValue;
        private DateTime currentSessionEndLocal = DateTime.MinValue;

        public event Action<DateTime> SessionStarted;
        public event Action<DateTime> SessionEnded;

        public DateTime CurrentSessionStartLocal => currentSessionStartLocal;
        public DateTime CurrentSessionEndLocal => currentSessionEndLocal;

        public SessionClock(InstrumentMeta meta)
        {
            this.meta = meta ?? throw new ArgumentNullException(nameof(meta));
        }

        // Call on every bar update with the bar's local time.
        // Fires SessionStarted when we cross into a new ETH window.
        public void OnTime(DateTime localTime)
        {
            DateTime expectedStart = ComputeSessionStartLocal(localTime);
            DateTime expectedEnd = expectedStart.AddHours(23).AddMinutes(-meta.MaintenanceBreakMinutes);

            if (expectedStart != currentSessionStartLocal)
            {
                if (currentSessionStartLocal != DateTime.MinValue)
                    SessionEnded?.Invoke(currentSessionEndLocal);

                currentSessionStartLocal = expectedStart;
                currentSessionEndLocal = expectedEnd;
                SessionStarted?.Invoke(expectedStart);
            }
        }

        public bool IsInSession(DateTime localTime)
        {
            return localTime >= currentSessionStartLocal && localTime < currentSessionEndLocal;
        }

        public TimeSpan TimeIntoSession(DateTime localTime)
        {
            return localTime - currentSessionStartLocal;
        }

        // Given a local timestamp, find the most recent ETH session open.
        // CME equity index futures open at 18:00 ET Sunday-Thursday and run
        // until 17:00 ET the following weekday.
        private DateTime ComputeSessionStartLocal(DateTime localTime)
        {
            DateTime etNow = TimeZoneInfo.ConvertTime(localTime, TimeZoneInfo.Local, EasternTz);
            DateTime todayOpenEt = new DateTime(
                etNow.Year, etNow.Month, etNow.Day,
                meta.EthOpenHourEt, meta.EthOpenMinuteEt, 0,
                DateTimeKind.Unspecified);

            // If we're past today's 18:00 ET open, the active session opened today.
            // Otherwise it opened the previous trading day's 18:00 ET.
            DateTime sessionStartEt = etNow >= todayOpenEt
                ? todayOpenEt
                : todayOpenEt.AddDays(-1);

            // Roll back over weekends (Saturday has no open).
            while (sessionStartEt.DayOfWeek == DayOfWeek.Saturday)
                sessionStartEt = sessionStartEt.AddDays(-1);

            return TimeZoneInfo.ConvertTime(
                DateTime.SpecifyKind(sessionStartEt, DateTimeKind.Unspecified),
                EasternTz, TimeZoneInfo.Local);
        }

        private static TimeZoneInfo TryGetTz(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { return null; }
        }
    }
}
