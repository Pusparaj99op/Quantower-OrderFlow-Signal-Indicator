// ============================================================
// VolumeProfileEngine.cs  |  LucidGold.Core.Engines
// ============================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LucidGold.Core.Engines
{
    /// <summary>
    /// Builds a real-time volume profile (volume-at-price histogram).
    /// Tracks current session and previous session.
    /// Computes POC (Point of Control), VAH (Value Area High), and VAL (Value Area Low).
    /// Value area = 70% of total volume centered on POC.
    /// </summary>
    public sealed class VolumeProfileEngine
    {
        private readonly double _tickSize;
        private static readonly TimeZoneInfo ET =
            TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        // ── Current session ───────────────────────────────────────────
        private readonly ConcurrentDictionary<long, long> _currentVol
            = new ConcurrentDictionary<long, long>();
        private DateTime _sessionStart = DateTime.UtcNow;

        // ── Previous session snapshot ─────────────────────────────────
        private Dictionary<long, long> _prevSessionVol = new Dictionary<long, long>();

        // ── Cached results (updated lazily every 100 trades) ──────────
        private double _poc     = 0;
        private double _vah     = 0;
        private double _val     = 0;
        private double _prevPoc = 0;
        private double _prevVah = 0;
        private double _prevVal = 0;

        private long _tradesSinceCalc = 0;
        private const long RecalcEveryN = 100;
        private readonly object _calcLock = new object();

        public VolumeProfileEngine(double tickSize)
        {
            _tickSize = tickSize > 0 ? tickSize : 0.1;
        }

        // ─────────────────────────────────────────────────────────────
        // Trade feed
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Add a trade to the current session volume profile.
        /// Thread-safe via ConcurrentDictionary.
        /// </summary>
        public void ProcessTrade(double price, long size)
        {
            long key = PriceToKey(price);
            _currentVol.AddOrUpdate(key, size, (_, v) => v + size);

            long count = Interlocked.Increment(ref _tradesSinceCalc);
            if (count % RecalcEveryN == 0)
                RecalcCurrentSession();
        }

        /// <summary>
        /// Call on each trade to check if a new session has begun (5 PM ET reset).
        /// </summary>
        public void CheckSessionReset(DateTime utcNow)
        {
            var etNow    = TimeZoneInfo.ConvertTimeFromUtc(utcNow, ET);
            var etStart  = TimeZoneInfo.ConvertTimeFromUtc(_sessionStart, ET);

            bool newDay  = etNow.Date > etStart.Date;
            bool after5  = etNow.Hour >= 17 && etStart.Hour < 17;

            if (newDay || after5)
                RollSession();
        }

        // ─────────────────────────────────────────────────────────────
        // Public accessors
        // ─────────────────────────────────────────────────────────────

        /// <summary>Point of Control — price level with highest volume this session.</summary>
        public double GetPOC() { lock (_calcLock) return _poc; }

        /// <summary>Value Area High — upper bound of 70% value area this session.</summary>
        public double GetVAH() { lock (_calcLock) return _vah; }

        /// <summary>Value Area Low — lower bound of 70% value area this session.</summary>
        public double GetVAL() { lock (_calcLock) return _val; }

        /// <summary>POC of the previous session.</summary>
        public double GetPriorSessionPOC() { lock (_calcLock) return _prevPoc; }

        /// <summary>VAH of the previous session.</summary>
        public double GetPriorSessionVAH() { lock (_calcLock) return _prevVah; }

        /// <summary>VAL of the previous session.</summary>
        public double GetPriorSessionVAL() { lock (_calcLock) return _prevVal; }

        // ─────────────────────────────────────────────────────────────
        // Private — calculation
        // ─────────────────────────────────────────────────────────────

        private void RecalcCurrentSession()
        {
            if (_currentVol.IsEmpty) return;

            var snapshot = new Dictionary<long, long>(_currentVol);
            var (poc, vah, val) = ComputeProfile(snapshot);

            lock (_calcLock)
            {
                _poc = poc;
                _vah = vah;
                _val = val;
            }
        }

        private void RollSession()
        {
            // Save current as previous and compute its profile
            _prevSessionVol = new Dictionary<long, long>(_currentVol);
            var (prevPoc, prevVah, prevVal) = ComputeProfile(_prevSessionVol);

            lock (_calcLock)
            {
                _prevPoc = prevPoc;
                _prevVah = prevVah;
                _prevVal = prevVal;
                _poc = 0; _vah = 0; _val = 0;
            }

            // Clear current session
            _currentVol.Clear();
            Interlocked.Exchange(ref _tradesSinceCalc, 0);
            _sessionStart = DateTime.UtcNow;
        }

        private (double poc, double vah, double val) ComputeProfile(
            Dictionary<long, long> volMap)
        {
            if (volMap.Count == 0) return (0, 0, 0);

            // Sort by price (key)
            var sorted = volMap.OrderBy(kv => kv.Key).ToList();

            // POC = key with highest volume
            long pocKey = sorted.OrderByDescending(kv => kv.Value).First().Key;
            double poc  = KeyToPrice(pocKey);

            // Value Area = 70% of total volume
            long totalVol = 0;
            foreach (var kv in sorted) totalVol += kv.Value;
            long vaTarget = (long)(totalVol * 0.70);

            // Expand from POC outward until 70% reached
            int pocIdx = sorted.FindIndex(kv => kv.Key == pocKey);
            int lo = pocIdx, hi = pocIdx;
            long accVol = sorted[pocIdx].Value;

            while (accVol < vaTarget && (lo > 0 || hi < sorted.Count - 1))
            {
                bool canExpandUp   = hi < sorted.Count - 1;
                bool canExpandDown = lo > 0;

                if (canExpandUp && canExpandDown)
                {
                    long upVol   = sorted[hi + 1].Value;
                    long downVol = sorted[lo - 1].Value;
                    if (upVol >= downVol) { hi++; accVol += upVol; }
                    else                  { lo--; accVol += downVol; }
                }
                else if (canExpandUp)   { hi++; accVol += sorted[hi].Value; }
                else if (canExpandDown) { lo--; accVol += sorted[lo].Value; }
                else break;
            }

            double vah = KeyToPrice(sorted[hi].Key);
            double val = KeyToPrice(sorted[lo].Key);

            return (poc, vah, val);
        }

        private long   PriceToKey(double price) => (long)Math.Round(price / _tickSize);
        private double KeyToPrice(long key)     => key * _tickSize;
    }
}
