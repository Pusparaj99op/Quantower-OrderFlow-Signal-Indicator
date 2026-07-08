// ============================================================
// OrderBlockEngine.cs  |  LucidGold.Core.Engines
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using LucidGold.Core.Enums;
using LucidGold.Core.Models;

namespace LucidGold.Core.Engines
{
    /// <summary>
    /// Detects and manages Order Block zones.
    /// Bullish OB: the last bearish candle before a bullish impulse that caused a BOS
    ///             where the impulse is &gt; 1.5 × ATR20.
    /// Bearish OB: the last bullish candle before a bearish impulse causing a bearish BOS.
    /// Invalidated OBs flip polarity to become Breaker Blocks.
    /// </summary>
    public sealed class OrderBlockEngine
    {
        private const int MaxOrderBlocks = 10;

        private readonly double _tickSize;
        private readonly List<OrderBlockZone> _orderBlocks = new List<OrderBlockZone>();
        private readonly object _lock = new object();

        public OrderBlockEngine(double tickSize)
        {
            _tickSize = tickSize > 0 ? tickSize : 0.1;
        }

        // ─────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Processes the rolling bar window to detect new order blocks.
        /// Call on every new bar close with the last 30 bars (oldest first).
        /// </summary>
        public void ProcessBars(IReadOnlyList<Bar> bars, double atr20, double tickSize)
        {
            if (bars.Count < 5 || atr20 <= 0) return;
            double impulseThr = atr20 * 1.5;

            lock (_lock)
            {
                // Scan from bar 2 to second-to-last (need i-1 and i+1)
                for (int i = 2; i < bars.Count - 1; i++)
                {
                    // Update barsAgo for existing blocks
                    foreach (var ob in _orderBlocks) ob.BarsAgo++;

                    var c0 = bars[i - 2];  // candidate OB candle (pre-impulse)
                    var c1 = bars[i - 1];  // impulse candle
                    var c2 = bars[i];      // follow-through

                    bool c1Bearish = c1.Close < c1.Open;
                    bool c1Bullish = c1.Close > c1.Open;
                    double c1Body  = Math.Abs(c1.Close - c1.Open);

                    if (c1Body < impulseThr) continue;

                    // ── Bullish OB: last bearish candle before bullish impulse ──
                    bool c0IsBearish = c0.Close < c0.Open;
                    if (c1Bullish && c0IsBearish && c2.Close > c1.High)
                    {
                        // Verify it hasn't already been detected (same timestamp)
                        if (_orderBlocks.Any(ob => ob.FormedTime == c0.Time)) continue;

                        _orderBlocks.Add(new OrderBlockZone
                        {
                            Top        = c0.High,
                            Bottom     = c0.Low,
                            Direction  = SignalDirection.Long,
                            FormedTime = c0.Time,
                            Valid      = true,
                            BarsAgo    = bars.Count - i
                        });
                    }

                    // ── Bearish OB: last bullish candle before bearish impulse ──
                    bool c0IsBullish = c0.Close > c0.Open;
                    if (c1Bearish && c0IsBullish && c2.Close < c1.Low)
                    {
                        if (_orderBlocks.Any(ob => ob.FormedTime == c0.Time)) continue;

                        _orderBlocks.Add(new OrderBlockZone
                        {
                            Top        = c0.High,
                            Bottom     = c0.Low,
                            Direction  = SignalDirection.Short,
                            FormedTime = c0.Time,
                            Valid      = true,
                            BarsAgo    = bars.Count - i
                        });
                    }
                }

                TrimOldBlocks();
            }
        }

        /// <summary>
        /// Checks each order block's validity against the current close price.
        /// Invalidated OBs (price closes through them) become Breaker Blocks.
        /// </summary>
        public void UpdateValidity(double currentPrice, double tickSize)
        {
            lock (_lock)
            {
                foreach (var ob in _orderBlocks)
                {
                    if (!ob.Valid && !ob.IsBreakerBlock) continue;

                    if (ob.Valid)
                    {
                        bool invalidated = ob.Direction == SignalDirection.Long
                            ? currentPrice < ob.Bottom - (tickSize * 2)  // price closed below bullish OB
                            : currentPrice > ob.Top    + (tickSize * 2); // price closed above bearish OB

                        if (invalidated)
                        {
                            ob.Valid          = false;
                            ob.IsBreakerBlock = true;
                            // Flip polarity: bullish OB → now acts as bearish resistance (breaker)
                            // Direction flips are tracked via IsBreakerBlock flag; Direction stays for history
                        }
                        else if (currentPrice >= ob.Bottom && currentPrice <= ob.Top)
                        {
                            ob.Tested     = true;
                            ob.TestCount++;
                        }
                    }
                }
            }
        }

        /// <summary>Returns the last MaxOrderBlocks valid (non-invalidated) order blocks.</summary>
        public IReadOnlyList<OrderBlockZone> GetActiveOrderBlocks()
        {
            lock (_lock)
                return _orderBlocks.Where(ob => ob.Valid || ob.IsBreakerBlock)
                                   .OrderByDescending(ob => ob.FormedTime)
                                   .Take(MaxOrderBlocks)
                                   .ToList();
        }

        /// <summary>
        /// Returns the nearest valid order block to currentPrice in the specified direction.
        /// For Long: nearest bullish OB below current price.
        /// For Short: nearest bearish OB above current price.
        /// </summary>
        public OrderBlockZone? GetNearestOrderBlock(double currentPrice, SignalDirection direction)
        {
            lock (_lock)
            {
                var active = _orderBlocks
                    .Where(ob => ob.Valid && ob.Direction == direction)
                    .ToList();

                if (direction == SignalDirection.Long)
                    return active
                        .Where(ob => ob.Top <= currentPrice)
                        .OrderByDescending(ob => ob.Top)
                        .FirstOrDefault();

                return active
                    .Where(ob => ob.Bottom >= currentPrice)
                    .OrderBy(ob => ob.Bottom)
                    .FirstOrDefault();
            }
        }

        /// <summary>Returns true if currentPrice is inside a valid OB of the specified direction.</summary>
        public bool IsInsideOrderBlock(double currentPrice, SignalDirection direction)
        {
            lock (_lock)
                return _orderBlocks.Any(ob =>
                    ob.Valid &&
                    ob.Direction == direction &&
                    currentPrice >= ob.Bottom &&
                    currentPrice <= ob.Top);
        }

        // ─────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────

        private void TrimOldBlocks()
        {
            // Keep only last MaxOrderBlocks total; prioritize valid over invalidated
            while (_orderBlocks.Count > MaxOrderBlocks * 2)
            {
                var oldest = _orderBlocks
                    .OrderBy(ob => ob.Valid ? 1 : 0)
                    .ThenBy(ob => ob.FormedTime)
                    .First();
                _orderBlocks.Remove(oldest);
            }
        }
    }
}
