// ============================================================
// CumulativeDeltaEngine.cs  |  LucidGold.Core.Engines
// ============================================================
using System;
using System.Threading;
using LucidGold.Core.Enums;

namespace LucidGold.Core.Engines
{
    /// <summary>
    /// Tracks cumulative delta (buy volume minus sell volume) with lock-free
    /// Interlocked operations for use on Quantower's event threads.
    /// Resets each session at 5 PM ET (CME gold futures session open).
    /// Uses local <see cref="AggressorSide"/> enum to avoid SDK dependency in Core.
    /// </summary>
    public sealed class CumulativeDeltaEngine
    {
        private static readonly TimeZoneInfo ET =
            TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        // ── Thread-safe accumulators (Interlocked) ───────────────────
        private long _sessionBuyVol;
        private long _sessionSellVol;
        private long _barBuyVol;
        private long _barSellVol;
        private long _cumulativeDelta;   // running session total

        // ── Rolling bar delta ring buffer (20 bars) ───────────────────
        private const int RingSize = 20;
        private readonly long[]   _barDeltaRing   = new long[RingSize];
        private readonly double[] _barHighRing     = new double[RingSize];
        private readonly double[] _barLowRing      = new double[RingSize];
        private readonly long[]   _cumDeltaAtClose = new long[RingSize];
        private int _ringHead  = 0;
        private int _ringCount = 0;

        // ── Session reset ─────────────────────────────────────────────
        private long _lastResetTicks = DateTime.UtcNow.Ticks;
        private readonly object _resetLock = new object();

        // ─────────────────────────────────────────────────────────────
        // Trade feed (hot path — must be < 1 µs)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Process each trade print. Completely lock-free via Interlocked.
        /// Uses local <see cref="AggressorSide"/> — callers must map from platform enum.
        /// </summary>
        public void ProcessTrade(double price, long size, AggressorSide aggressor)
        {
            if (aggressor == AggressorSide.Buy)
            {
                Interlocked.Add(ref _sessionBuyVol,    size);
                Interlocked.Add(ref _barBuyVol,        size);
                Interlocked.Add(ref _cumulativeDelta,  size);
            }
            else if (aggressor == AggressorSide.Sell)
            {
                Interlocked.Add(ref _sessionSellVol,  size);
                Interlocked.Add(ref _barSellVol,       size);
                Interlocked.Add(ref _cumulativeDelta, -size);
            }
        }

        /// <summary>
        /// Call on each bar close. Stores bar delta history and resets bar accumulators.
        /// </summary>
        public void OnBarClose(double barHigh, double barLow)
        {
            long buyVol  = Interlocked.Read(ref _barBuyVol);
            long sellVol = Interlocked.Read(ref _barSellVol);
            long delta   = buyVol - sellVol;
            long cumDelta= Interlocked.Read(ref _cumulativeDelta);

            // Write ring buffer (single-threaded from bar-close context)
            int idx = _ringHead % RingSize;
            _barDeltaRing[idx]   = delta;
            _barHighRing[idx]    = barHigh;
            _barLowRing[idx]     = barLow;
            _cumDeltaAtClose[idx]= cumDelta;
            _ringHead++;
            _ringCount = Math.Min(_ringCount + 1, RingSize);

            // Reset bar accumulators
            Interlocked.Exchange(ref _barBuyVol,  0);
            Interlocked.Exchange(ref _barSellVol, 0);

            // Session reset check
            CheckSessionReset();
        }

        // ─────────────────────────────────────────────────────────────
        // Public read accessors
        // ─────────────────────────────────────────────────────────────

        /// <summary>Current bar's delta (buys minus sells since last bar close).</summary>
        public long GetBarDelta()
            => Interlocked.Read(ref _barBuyVol) - Interlocked.Read(ref _barSellVol);

        /// <summary>Running session cumulative delta.</summary>
        public long GetCumulativeDelta()
            => Interlocked.Read(ref _cumulativeDelta);

        /// <summary>Full session delta (buy vol – sell vol).</summary>
        public long GetSessionDelta()
            => Interlocked.Read(ref _sessionBuyVol) - Interlocked.Read(ref _sessionSellVol);

        /// <summary>Buy volume as a percentage of total session volume (0–100).</summary>
        public double GetDeltaPercentage()
        {
            long buy  = Interlocked.Read(ref _sessionBuyVol);
            long sell = Interlocked.Read(ref _sessionSellVol);
            long total = buy + sell;
            return total > 0 ? (double)buy / total * 100.0 : 50.0;
        }

        /// <summary>
        /// Returns the trend direction of cumulative delta over the last N bars.
        /// Uses linear regression slope.
        /// </summary>
        public DeltaTrend GetDeltaSlope(int lookback = 5)
        {
            if (_ringCount < 2) return DeltaTrend.Flat;
            int n = Math.Min(lookback, _ringCount);

            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int i = 0; i < n; i++)
            {
                int idx = (_ringHead - 1 - i + RingSize * 100) % RingSize;
                double x = i, y = _barDeltaRing[idx];
                sumX  += x;  sumY += y;
                sumXY += x * y;  sumX2 += x * x;
            }
            double denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-10) return DeltaTrend.Flat;
            double slope = (n * sumXY - sumX * sumY) / denom;

            // Slope threshold relative to average bar volume
            if (slope >  100) return DeltaTrend.Rising;
            if (slope < -100) return DeltaTrend.Falling;
            return DeltaTrend.Flat;
        }

        /// <summary>
        /// Returns true when price makes a new extreme but delta fails to confirm —
        /// a classic delta divergence indicating potential reversal.
        /// </summary>
        public bool IsDeltaDivergent(SignalDirection direction)
        {
            if (_ringCount < 4) return false;

            int idx0 = (_ringHead - 1 + RingSize * 100) % RingSize;
            int idx1 = (_ringHead - 2 + RingSize * 100) % RingSize;

            if (direction == SignalDirection.Long)
            {
                // Bullish divergence: price makes lower low but delta makes higher low
                bool priceLowerLow  = _barLowRing[idx0]   < _barLowRing[idx1];
                bool deltaHigherLow = _barDeltaRing[idx0] > _barDeltaRing[idx1];
                return priceLowerLow && deltaHigherLow;
            }
            else
            {
                // Bearish divergence: price makes higher high but delta makes lower high
                bool priceHigherHigh = _barHighRing[idx0]  > _barHighRing[idx1];
                bool deltaLowerHigh  = _barDeltaRing[idx0] < _barDeltaRing[idx1];
                return priceHigherHigh && deltaLowerHigh;
            }
        }

        /// <summary>Returns bar delta value for historical bar at ring offset (0=newest).</summary>
        public long GetHistoricalBarDelta(int barsAgo)
        {
            if (barsAgo < 0 || barsAgo >= _ringCount) return 0;
            int idx = (_ringHead - 1 - barsAgo + RingSize * 100) % RingSize;
            return _barDeltaRing[idx];
        }

        /// <summary>Returns cumulative delta at close of historical bar at ring offset (0=newest).</summary>
        public long GetHistoricalCumDelta(int barsAgo)
        {
            if (barsAgo < 0 || barsAgo >= _ringCount) return 0;
            int idx = (_ringHead - 1 - barsAgo + RingSize * 100) % RingSize;
            return _cumDeltaAtClose[idx];
        }

        // ─────────────────────────────────────────────────────────────
        // Session reset (5 PM ET)
        // ─────────────────────────────────────────────────────────────

        private void CheckSessionReset()
        {
            lock (_resetLock)
            {
                var now     = DateTime.UtcNow;
                var nowEt   = TimeZoneInfo.ConvertTimeFromUtc(now, ET);
                var lastEt  = TimeZoneInfo.ConvertTimeFromUtc(
                                  new DateTime(_lastResetTicks, DateTimeKind.Utc), ET);

                bool newDay = nowEt.Date > lastEt.Date;
                bool after5 = nowEt.Hour >= 17 && lastEt.Hour < 17;

                if (newDay || after5)
                {
                    Interlocked.Exchange(ref _sessionBuyVol,   0);
                    Interlocked.Exchange(ref _sessionSellVol,  0);
                    Interlocked.Exchange(ref _cumulativeDelta, 0);
                    _lastResetTicks = now.Ticks;
                }
            }
        }
    }
}
