// ============================================================
// KillZoneEngine.cs  |  LucidGold.Core.Engines
// ============================================================
using System;

namespace LucidGold.Core.Engines
{
    /// <summary>
    /// Determines active ICT Kill Zone sessions and Silver Bullet windows.
    /// All times are in US Eastern Time (handles DST automatically via TimeZoneInfo).
    ///
    /// Kill Zone Windows (ET):
    ///   London:       02:00 – 05:00
    ///   NY:           07:00 – 10:00  (primary)
    ///   London Close: 10:00 – 12:00
    ///   Afternoon:    13:30 – 15:00
    ///
    /// Silver Bullet Windows (ET):
    ///   SB1: 03:00 – 04:00
    ///   SB2: 10:00 – 11:00
    ///   SB3: 14:00 – 15:00
    ///
    /// COMEX Open: 08:20 ET  (±10 min window)
    /// </summary>
    public sealed class KillZoneEngine
    {
        private static readonly TimeZoneInfo ET =
            TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        // ─────────────────────────────────────────────────────────────
        // Kill Zone API
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if any enabled kill zone is currently active.
        /// </summary>
        public bool IsKillZoneActive(DateTime utcNow,
                                     bool londonEnabled     = true,
                                     bool nyEnabled         = true,
                                     bool londonCloseEnabled= false,
                                     bool afternoonEnabled  = false)
        {
            return GetActiveKillZoneName(utcNow, londonEnabled, nyEnabled,
                                         londonCloseEnabled, afternoonEnabled) != "None";
        }

        /// <summary>
        /// Returns the name of the currently active kill zone, or "None".
        /// Silver Bullet windows take precedence over their parent kill zone label.
        /// </summary>
        public string GetActiveKillZoneName(DateTime utcNow,
                                            bool londonEnabled     = true,
                                            bool nyEnabled         = true,
                                            bool londonCloseEnabled= false,
                                            bool afternoonEnabled  = false)
        {
            var et = ToET(utcNow);
            var t  = TimeOnly.FromDateTime(et);

            // Silver Bullets (sub-windows — check first for priority labeling)
            if (londonEnabled && InRange(t, 3, 0, 4, 0))   return "Silver Bullet 1";
            if (nyEnabled     && InRange(t, 10, 0, 11, 0)) return "Silver Bullet 2";
            if (afternoonEnabled && InRange(t, 14, 0, 15, 0)) return "Silver Bullet 3";

            // Kill Zones
            if (londonEnabled      && InRange(t, 2, 0, 5, 0))   return "London Kill Zone";
            if (nyEnabled          && InRange(t, 7, 0, 10, 0))  return "NY Kill Zone";
            if (londonCloseEnabled && InRange(t, 10, 0, 12, 0)) return "London Close";
            if (afternoonEnabled   && InRange(t, 13, 30, 15, 0)) return "Afternoon Session";

            return "None";
        }

        /// <summary>Returns true if current time falls within any Silver Bullet window.</summary>
        public bool IsSilverBullet(DateTime utcNow)
        {
            var t = TimeOnly.FromDateTime(ToET(utcNow));
            return InRange(t, 3, 0, 4, 0)   ||
                   InRange(t, 10, 0, 11, 0) ||
                   InRange(t, 14, 0, 15, 0);
        }

        /// <summary>
        /// Returns true if current time is within 10 minutes of the COMEX open (08:20 ET).
        /// </summary>
        public bool IsComexOpen(DateTime utcNow)
        {
            var t = TimeOnly.FromDateTime(ToET(utcNow));
            return InRange(t, 8, 10, 8, 30);
        }

        /// <summary>
        /// Returns a scoring weight for the specified kill zone name.
        /// Weights reflect the relative opportunity quality of each window.
        /// </summary>
        public int GetKillZoneWeight(string killZoneName) => killZoneName switch
        {
            "NY Kill Zone"      => 15,
            "Silver Bullet 2"   => 15,
            "Silver Bullet 1"   => 12,
            "Silver Bullet 3"   => 10,
            "London Kill Zone"  => 7,
            "London Close"      => 5,
            "Afternoon Session" => 3,
            _                   => 0
        };

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        private static DateTime ToET(DateTime utcTime)
            => TimeZoneInfo.ConvertTimeFromUtc(utcTime, ET);

        /// <summary>Returns true if t is in [startH:startM, endH:endM).</summary>
        private static bool InRange(TimeOnly t,
                                    int startH, int startM,
                                    int endH,   int endM)
        {
            var start = new TimeOnly(startH, startM);
            var end   = new TimeOnly(endH,   endM);
            return t >= start && t < end;
        }
    }
}
