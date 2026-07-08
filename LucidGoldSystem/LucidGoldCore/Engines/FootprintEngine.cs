// ============================================================
// FootprintEngine.cs  |  LucidGold.Core.Engines
// ============================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using LucidGold.Core.Enums;

namespace LucidGold.Core.Engines
{
    /// <summary>Single price-level cell in a footprint bar.</summary>
    public struct FootprintCell
    {
        /// <summary>Seller-aggressed volume at this price level.</summary>
        public long BidVolume;
        /// <summary>Buyer-aggressed volume at this price level.</summary>
        public long AskVolume;
        /// <summary>Net delta at this level (AskVolume - BidVolume).</summary>
        public long Delta;
        /// <summary>Total volume traded at this level.</summary>
        public long TotalVolume;
    }

    /// <summary>Complete footprint for one bar.</summary>
    public sealed class FootprintBar
    {
        public Dictionary<double, FootprintCell> Cells { get; } = new();
        public double High  { get; set; }
        public double Low   { get; set; }
        public double Open  { get; set; }
        public double Close { get; set; }
        public DateTime Time { get; set; }
    }

    /// <summary>Summary signal output from the footprint engine for a single bar.</summary>
    public sealed class FootprintSignalSummary
    {
        public bool   HasStackedBullishImbalance  { get; set; }
        public bool   HasStackedBearishImbalance  { get; set; }
        public bool   HasBullishAbsorption        { get; set; }
        public bool   HasBearishAbsorption        { get; set; }
        public bool   HasUnfinishedAuctionHigh    { get; set; }
        public bool   HasUnfinishedAuctionLow     { get; set; }
        public bool   HasDeltaExhaustion          { get; set; }
        public double BullishImbalanceRatio       { get; set; }
        public double BearishImbalanceRatio       { get; set; }
        public double HighVolumePriceLevelPrice   { get; set; }
        public long   HighVolumePriceLevelVolume  { get; set; }
        public long   BarDelta                    { get; set; }
    }

    /// <summary>
    /// Reconstructs footprint candles from trade prints.
    /// Tracks bid/ask volume at each price level within a bar.
    /// Detects stacked imbalances, absorption, unfinished auctions, and delta exhaustion.
    /// Uses local <see cref="AggressorSide"/> enum to avoid SDK dependency.
    ///
    /// <para>Stacked imbalance: 3+ consecutive levels with imbalance ratio ≥ 3:1.</para>
    /// <para>Absorption at extreme: volume at extreme level > 2× average cell volume.</para>
    /// <para>Unfinished auction: zero volume on one side at the high or low of the bar.</para>
    /// <para>Delta exhaustion: delta strongly positive for 3+ bars but price momentum slowing.</para>
    /// </summary>
    public sealed class FootprintEngine
    {
        private readonly double _tickSize;

        // Current bar state (keyed by price-key = round(price/tickSize))
        private readonly ConcurrentDictionary<long, long> _currentBidVol
            = new ConcurrentDictionary<long, long>();
        private readonly ConcurrentDictionary<long, long> _currentAskVol
            = new ConcurrentDictionary<long, long>();

        private double _barOpen  = 0;
        private double _barHigh  = double.MinValue;
        private double _barLow   = double.MaxValue;
        private DateTime _barTime;

        // Completed bar history ring buffer (last 5 bars)
        private const int HistorySize = 5;
        private readonly FootprintSignalSummary[] _barHistory
            = new FootprintSignalSummary[HistorySize];
        private readonly long[] _barDeltaHistory = new long[HistorySize];
        private int _barHistoryIdx = 0;

        private readonly object _barLock = new object();

        // Pattern detection thresholds
        private const double ImbalanceRatio   = 3.0;   // 3:1 per spec
        private const int    StackMinLevels   = 3;
        private const double AbsorptionMult   = 2.0;

        public FootprintEngine(double tickSize)
        {
            _tickSize = tickSize > 0 ? tickSize : 0.1;
        }

        // ─────────────────────────────────────────────────────────────
        // Trade feed (hot path — called on platform event thread)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Call on every trade print. Uses local <see cref="AggressorSide"/>.
        /// Aggressor.Buy = ask side (buyer-initiated), Aggressor.Sell = bid side.
        /// </summary>
        public void ProcessTrade(double price, long size, AggressorSide aggressor)
        {
            long key = PriceToKey(price);

            if (aggressor == AggressorSide.Buy)
                _currentAskVol.AddOrUpdate(key, size, (_, v) => v + size);
            else if (aggressor == AggressorSide.Sell)
                _currentBidVol.AddOrUpdate(key, size, (_, v) => v + size);

            if (price > _barHigh) _barHigh = price;
            if (price < _barLow)  _barLow  = price;
            if (_barOpen == 0)    _barOpen  = price;
        }

        /// <summary>
        /// Call on every bar close. Computes footprint metrics and stores summary.
        /// </summary>
        public void OnBarClose(double high, double low, double close, DateTime barTime)
        {
            lock (_barLock)
            {
                _barHigh = high;
                _barLow  = low;

                var summary = ComputeSummary(high, low);
                int slot = _barHistoryIdx % HistorySize;
                _barHistory[slot]      = summary;
                _barDeltaHistory[slot] = summary.BarDelta;
                _barHistoryIdx++;

                // Reset for next bar
                _currentBidVol.Clear();
                _currentAskVol.Clear();
                _barHigh = double.MinValue;
                _barLow  = double.MaxValue;
                _barOpen = 0;
                _barTime = barTime;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Public read API
        // ─────────────────────────────────────────────────────────────

        /// <summary>Returns the signal summary for the most recently completed bar.</summary>
        public FootprintSignalSummary GetSignalSummary()
        {
            lock (_barLock)
            {
                int lastIdx = (_barHistoryIdx - 1 + HistorySize * 100) % HistorySize;
                return _barHistory[lastIdx] ?? new FootprintSignalSummary();
            }
        }

        /// <summary>Returns the live footprint cells for the current incomplete bar.</summary>
        public FootprintBar GetCurrentBar()
        {
            var bar = new FootprintBar
            {
                High = _barHigh == double.MinValue ? 0 : _barHigh,
                Low  = _barLow  == double.MaxValue ? 0 : _barLow,
                Time = _barTime
            };
            var allKeys = new HashSet<long>(_currentAskVol.Keys);
            allKeys.UnionWith(_currentBidVol.Keys);
            foreach (long key in allKeys)
            {
                _currentAskVol.TryGetValue(key, out long ask);
                _currentBidVol.TryGetValue(key, out long bid);
                bar.Cells[KeyToPrice(key)] = new FootprintCell
                {
                    AskVolume   = ask,
                    BidVolume   = bid,
                    Delta       = ask - bid,
                    TotalVolume = ask + bid
                };
            }
            return bar;
        }

        /// <summary>
        /// Returns true if there is a stacked imbalance (3+ consecutive levels, ≥3:1 ratio)
        /// in the specified direction in the current/last completed bar.
        /// </summary>
        public bool HasStackedImbalance(SignalDirection direction, int minLevels = 3)
        {
            var summary = GetSignalSummary();
            return direction == SignalDirection.Long
                ? summary.HasStackedBullishImbalance
                : summary.HasStackedBearishImbalance;
        }

        /// <summary>
        /// Returns true if absorption is detected at the specified bar extreme.
        /// </summary>
        public bool HasAbsorptionAtExtreme(BarExtreme extreme)
        {
            var summary = GetSignalSummary();
            return extreme == BarExtreme.Low
                ? summary.HasBullishAbsorption   // sells absorbed at low
                : summary.HasBearishAbsorption;  // buys absorbed at high
        }

        /// <summary>
        /// Returns true if there is an unfinished auction at the specified extreme
        /// (zero volume on one side at the high or low of the bar).
        /// </summary>
        public bool HasUnfinishedAuction(BarExtreme extreme)
        {
            var summary = GetSignalSummary();
            return extreme == BarExtreme.High
                ? summary.HasUnfinishedAuctionHigh
                : summary.HasUnfinishedAuctionLow;
        }

        /// <summary>
        /// Returns true if delta exhaustion is detected (3+ bars of same-direction delta
        /// with diminishing magnitude).
        /// </summary>
        public bool HasDeltaExhaustion()
        {
            lock (_barLock)
            {
                if (_barHistoryIdx < 3) return false;
                int n = Math.Min(3, _barHistoryIdx);
                long[] recent = new long[n];
                for (int i = 0; i < n; i++)
                    recent[i] = _barDeltaHistory[(_barHistoryIdx - 1 - i + HistorySize * 100) % HistorySize];

                // All same sign and diminishing magnitude
                bool allBullish = recent.All(d => d > 0);
                bool allBearish = recent.All(d => d < 0);
                if (allBullish && recent[0] < recent[1] && recent[1] < recent[2]) return true;
                if (allBearish && recent[0] > recent[1] && recent[1] > recent[2]) return true;
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Private — analysis
        // ─────────────────────────────────────────────────────────────

        private FootprintSignalSummary ComputeSummary(double high, double low)
        {
            var summary = new FootprintSignalSummary();

            var allKeys = new HashSet<long>(_currentAskVol.Keys);
            allKeys.UnionWith(_currentBidVol.Keys);
            var sortedKeys = new List<long>(allKeys);
            sortedKeys.Sort();

            if (sortedKeys.Count == 0) return summary;

            // ── Bar delta ─────────────────────────────────────────────
            long totalAsk = 0, totalBid = 0;
            foreach (long key in sortedKeys)
            {
                _currentAskVol.TryGetValue(key, out long a);
                _currentBidVol.TryGetValue(key, out long b);
                totalAsk += a;
                totalBid += b;
            }
            summary.BarDelta = totalAsk - totalBid;

            if (sortedKeys.Count < StackMinLevels)
            {
                summary.HighVolumePriceLevelPrice = 0;
                return summary;
            }

            // ── Total and average volume per level ────────────────────
            long totalVol = totalAsk + totalBid;
            double avgLevelVol = (double)totalVol / sortedKeys.Count;

            // ── Stacked imbalance scan ────────────────────────────────
            int bullishStack = 0, bearishStack = 0;
            double totalBullishRatio = 0, totalBearishRatio = 0;

            foreach (long key in sortedKeys)
            {
                _currentAskVol.TryGetValue(key, out long askVol);
                _currentBidVol.TryGetValue(key, out long bidVol);

                // Bullish imbalance: asks dominate (buyer aggression)
                if (askVol > 0 && bidVol > 0 && (double)askVol / bidVol >= ImbalanceRatio)
                {
                    bullishStack++;
                    totalBullishRatio += (double)askVol / bidVol;
                    if (bullishStack >= StackMinLevels)
                        summary.HasStackedBullishImbalance = true;
                }
                else if (askVol > 0 && bidVol == 0)
                {
                    bullishStack++;
                    totalBullishRatio += ImbalanceRatio;
                    if (bullishStack >= StackMinLevels)
                        summary.HasStackedBullishImbalance = true;
                }
                else
                {
                    bullishStack = 0;
                }

                // Bearish imbalance: bids dominate (seller aggression)
                if (bidVol > 0 && askVol > 0 && (double)bidVol / askVol >= ImbalanceRatio)
                {
                    bearishStack++;
                    totalBearishRatio += (double)bidVol / askVol;
                    if (bearishStack >= StackMinLevels)
                        summary.HasStackedBearishImbalance = true;
                }
                else if (bidVol > 0 && askVol == 0)
                {
                    bearishStack++;
                    totalBearishRatio += ImbalanceRatio;
                    if (bearishStack >= StackMinLevels)
                        summary.HasStackedBearishImbalance = true;
                }
                else
                {
                    bearishStack = 0;
                }
            }

            summary.BullishImbalanceRatio = bullishStack > 0 ? totalBullishRatio / bullishStack : 0;
            summary.BearishImbalanceRatio = bearishStack > 0 ? totalBearishRatio / bearishStack : 0;

            // ── Absorption at extremes ─────────────────────────────────
            long highKey = PriceToKey(high);
            long lowKey  = PriceToKey(low);

            _currentBidVol.TryGetValue(lowKey,  out long bidAtLow);
            _currentAskVol.TryGetValue(highKey, out long askAtHigh);

            // Bullish absorption: heavy selling at the low but bar survived
            summary.HasBullishAbsorption = bidAtLow  > avgLevelVol * AbsorptionMult;
            // Bearish absorption: heavy buying at the high but bar didn't continue higher
            summary.HasBearishAbsorption = askAtHigh > avgLevelVol * AbsorptionMult;

            // ── Unfinished auction ────────────────────────────────────
            // An extreme where one side has zero volume → market will return
            _currentAskVol.TryGetValue(highKey, out long askAtHighU);
            _currentBidVol.TryGetValue(highKey, out long bidAtHighU);
            _currentAskVol.TryGetValue(lowKey,  out long askAtLowU);
            _currentBidVol.TryGetValue(lowKey,  out long bidAtLowU);

            summary.HasUnfinishedAuctionHigh = bidAtHighU == 0 && askAtHighU > 0;
            summary.HasUnfinishedAuctionLow  = askAtLowU  == 0 && bidAtLowU  > 0;

            // ── High volume price level ───────────────────────────────
            long maxVol = 0;
            foreach (long key in sortedKeys)
            {
                _currentAskVol.TryGetValue(key, out long a);
                _currentBidVol.TryGetValue(key, out long b);
                long vol = a + b;
                if (vol > maxVol)
                {
                    maxVol = vol;
                    summary.HighVolumePriceLevelVolume = maxVol;
                    summary.HighVolumePriceLevelPrice  = KeyToPrice(key);
                }
            }

            return summary;
        }

        private long   PriceToKey(double price) => (long)Math.Round(price / _tickSize);
        private double KeyToPrice(long key)     => key * _tickSize;
    }
}
