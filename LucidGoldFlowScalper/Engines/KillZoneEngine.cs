using System;
using LucidGoldFlowScalper.Utils;

namespace LucidGoldFlowScalper.Engines
{
    public enum KillZone
    {
        None,
        London,
        NY,
        ComexOpen,
        SilverBullet1,
        SilverBullet2,
        Afternoon
    }

    /// <summary>
    /// Manages session timing and kill zone detection.
    /// All times are strictly in Eastern Time (ET).
    /// </summary>
    public class KillZoneEngine
    {
        private bool _enableLondon;
        private bool _enableNY;
        private bool _enableAfternoon;

        public bool IsKillZoneActive { get; private set; }
        public KillZone CurrentKillZone { get; private set; }
        public float KillZoneWeight { get; private set; }
        public bool IsNoTradeZone { get; private set; }
        public TimeSpan TimeUntilNextKillZone { get; private set; }
        public TimeSpan TimeRemainingInKillZone { get; private set; }

        public KillZoneEngine(bool enableLondon, bool enableNY, bool enableAfternoon)
        {
            _enableLondon = enableLondon;
            _enableNY = enableNY;
            _enableAfternoon = enableAfternoon;
        }

        public void Update(DateTime utcNow)
        {
            DateTime etNow = SessionTime.GetEasternTime(utcNow);
            TimeSpan time = etNow.TimeOfDay;

            IsNoTradeZone = CheckNoTradeZones(time);
            if (IsNoTradeZone)
            {
                SetNoKillZone();
                return;
            }

            DetermineKillZone(time);
        }

        private bool CheckNoTradeZones(TimeSpan time)
        {
            // Lunch Lull: 11:30 - 13:30 ET
            if (time >= new TimeSpan(11, 30, 0) && time < new TimeSpan(13, 30, 0)) return true;
            
            // Settlement: 17:00 - 18:00 ET
            if (time >= new TimeSpan(17, 0, 0) && time < new TimeSpan(18, 0, 0)) return true;
            
            // Asian Low Vol: 23:00 - 02:00 ET
            if (time >= new TimeSpan(23, 0, 0) || time < new TimeSpan(2, 0, 0)) return true;

            return false;
        }

        private void DetermineKillZone(TimeSpan time)
        {
            // Priority order check: COMEX Open > NY SB > NY > London SB > London > Afternoon
            
            // COMEX Open: 08:20 - 08:40 ET
            if (_enableNY && time >= new TimeSpan(8, 20, 0) && time < new TimeSpan(8, 40, 0))
            {
                SetActiveKillZone(KillZone.ComexOpen, 1.2f, new TimeSpan(8, 40, 0) - time);
                return;
            }

            // Silver Bullet 2 (NY): 10:00 - 11:00 ET
            if (_enableNY && time >= new TimeSpan(10, 0, 0) && time < new TimeSpan(11, 0, 0))
            {
                SetActiveKillZone(KillZone.SilverBullet2, 0.9f, new TimeSpan(11, 0, 0) - time);
                return;
            }

            // NY Kill Zone: 07:00 - 10:00 ET
            if (_enableNY && time >= new TimeSpan(7, 0, 0) && time < new TimeSpan(10, 0, 0))
            {
                SetActiveKillZone(KillZone.NY, 1.0f, new TimeSpan(10, 0, 0) - time);
                return;
            }

            // Silver Bullet 1 (London): 03:00 - 04:00 ET
            if (_enableLondon && time >= new TimeSpan(3, 0, 0) && time < new TimeSpan(4, 0, 0))
            {
                SetActiveKillZone(KillZone.SilverBullet1, 0.9f, new TimeSpan(4, 0, 0) - time);
                return;
            }

            // London Kill Zone: 02:00 - 05:00 ET
            if (_enableLondon && time >= new TimeSpan(2, 0, 0) && time < new TimeSpan(5, 0, 0))
            {
                SetActiveKillZone(KillZone.London, 0.8f, new TimeSpan(5, 0, 0) - time);
                return;
            }

            // Afternoon Window: 13:30 - 15:00 ET
            if (_enableAfternoon && time >= new TimeSpan(13, 30, 0) && time < new TimeSpan(15, 0, 0))
            {
                SetActiveKillZone(KillZone.Afternoon, 0.7f, new TimeSpan(15, 0, 0) - time);
                return;
            }

            SetNoKillZone();
        }

        private void SetActiveKillZone(KillZone kz, float weight, TimeSpan remaining)
        {
            IsKillZoneActive = true;
            CurrentKillZone = kz;
            KillZoneWeight = weight;
            TimeRemainingInKillZone = remaining;
            TimeUntilNextKillZone = TimeSpan.Zero;
        }

        private void SetNoKillZone()
        {
            IsKillZoneActive = false;
            CurrentKillZone = KillZone.None;
            KillZoneWeight = 0f;
            TimeRemainingInKillZone = TimeSpan.Zero;
            // Simplified TimeUntilNext computation omitted for brevity, default to zero
            TimeUntilNextKillZone = TimeSpan.Zero;
        }
    }
}
