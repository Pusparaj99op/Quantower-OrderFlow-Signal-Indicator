// ============================================================
// FVGEngine.cs  |  LucidGold.Core.Engines
// ============================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LucidGold.Core.Enums;
using LucidGold.Core.Models;

namespace LucidGold.Core.Engines
{
    /// <summary>
    /// Detects and manages Fair Value Gaps (FVGs) across multiple timeframes.
    /// Bullish FVG: candle1.High &lt; candle3.Low
    /// Bearish FVG: candle1.Low &gt; candle3.High
    /// Maintains up to 20 active (unfilled) FVGs per timeframe.
    /// </summary>
    public sealed class FVGEngine
    {
        private const int MaxFVGsPerTimeframe = 20;
        private const double LargeFVGThresholdTicks = 10.0;

        private readonly double _tickSize;
        private readonly ConcurrentDictionary<string, List<FairValueGap>> _fvgsByTimeframe
            = new ConcurrentDictionary<string, List<FairValueGap>>();

        private readonly object _writeLock = new object();

        public FVGEngine(double tickSize)
        {
            _tickSize = tickSize > 0 ? tickSize : 0.1;
        }

        // ─────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Process three consecutive closed bars. Call on every new bar close with the
        /// three most recent bars (c1 = oldest, c2 = middle, c3 = newest).
        /// </summary>
        public void ProcessNewBar(Bar c1, Bar c2, Bar c3, string timeframe)
        {
            // Bullish FVG: gap between c1 high and c3 low
            if (c1.High < c3.Low)
            {
                double gapSize = c3.Low - c1.High;
                double body    = Math.Abs(c2.Close - c2.Open);
                double strength = (gapSize / _tickSize) + (body / Math.Max(_tickSize, gapSize));

                var fvg = new FairValueGap
                {
                    Top         = c3.Low,
                    Bottom      = c1.High,
                    Direction   = SignalDirection.Long,
                    FormedTime  = c3.Time,
                    Strength    = strength,
                    Timeframe   = timeframe,
                    PriorityScore = strength  // refined by HTF alignment in scoring engine
                };
                AddFVG(timeframe, fvg);
            }

            // Bearish FVG: gap between c1 low and c3 high
            if (c1.Low > c3.High)
            {
                double gapSize = c1.Low - c3.High;
                double body    = Math.Abs(c2.Close - c2.Open);
                double strength = (gapSize / _tickSize) + (body / Math.Max(_tickSize, gapSize));

                var fvg = new FairValueGap
                {
                    Top         = c1.Low,
                    Bottom      = c3.High,
                    Direction   = SignalDirection.Short,
                    FormedTime  = c3.Time,
                    Strength    = strength,
                    Timeframe   = timeframe,
                    PriorityScore = strength
                };
                AddFVG(timeframe, fvg);
            }
        }

        /// <summary>
        /// Updates fill status for all active FVGs based on current price.
        /// Call on every new tick or bar close.
        /// </summary>
        public void UpdateFillStatus(double currentPrice, double tickSize)
        {
            foreach (var kvp in _fvgsByTimeframe)
            {
                lock (_writeLock)
                {
                    foreach (var fvg in kvp.Value)
                    {
                        if (!fvg.IsActive) continue;

                        if (fvg.Direction == SignalDirection.Long)
                        {
                            if (currentPrice >= fvg.Top)
                            {
                                fvg.Filled = true;  // fully filled — price traded through
                            }
                            else if (currentPrice > fvg.Bottom)
                            {
                                fvg.PartiallyFilled = true;
                            }
                        }
                        else  // Bearish FVG
                        {
                            if (currentPrice <= fvg.Bottom)
                            {
                                fvg.Filled = true;
                            }
                            else if (currentPrice < fvg.Top)
                            {
                                fvg.PartiallyFilled = true;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Returns all currently active (unfilled, not void) FVGs across all timeframes.</summary>
        public IReadOnlyList<FairValueGap> GetActiveFVGs()
        {
            var result = new List<FairValueGap>();
            foreach (var kvp in _fvgsByTimeframe)
            {
                lock (_writeLock)
                    result.AddRange(kvp.Value.Where(f => f.IsActive));
            }
            return result.OrderByDescending(f => f.PriorityScore).ToList();
        }

        /// <summary>
        /// Returns the nearest active FVG to currentPrice in the specified direction.
        /// For Long: nearest FVG below current price.
        /// For Short: nearest FVG above current price.
        /// </summary>
        public FairValueGap? GetNearestFVG(double currentPrice, SignalDirection direction)
        {
            var active = GetActiveFVGs()
                .Where(f => f.Direction == direction)
                .ToList();

            if (direction == SignalDirection.Long)
            {
                // FVG must be below current price (a pullback target)
                return active
                    .Where(f => f.Top <= currentPrice)
                    .OrderByDescending(f => f.Top)  // closest to current price
                    .FirstOrDefault();
            }
            else
            {
                return active
                    .Where(f => f.Bottom >= currentPrice)
                    .OrderBy(f => f.Bottom)  // closest above
                    .FirstOrDefault();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────

        private void AddFVG(string timeframe, FairValueGap fvg)
        {
            var list = _fvgsByTimeframe.GetOrAdd(timeframe, _ => new List<FairValueGap>());
            lock (_writeLock)
            {
                list.Add(fvg);
                // Trim: keep only last MaxFVGsPerTimeframe active
                var inactive = list.Where(f => !f.IsActive).ToList();
                if (list.Count > MaxFVGsPerTimeframe)
                {
                    // Remove oldest filled/void entries first
                    foreach (var old in inactive.Take(list.Count - MaxFVGsPerTimeframe))
                        list.Remove(old);
                }
                // If still over limit, remove weakest active
                while (list.Count > MaxFVGsPerTimeframe)
                {
                    var weakest = list.OrderBy(f => f.PriorityScore).First();
                    list.Remove(weakest);
                }
            }
        }
    }
}
