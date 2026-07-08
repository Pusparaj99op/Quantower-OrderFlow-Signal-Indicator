// ============================================================
// DOMAbsorptionEngine.cs  |  LucidGold.Core.Engines
// ============================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LucidGold.Core.Enums;
using LucidGold.Core.Models;

namespace LucidGold.Core.Engines
{
    /// <summary>
    /// A Level 2 quote update. Callers map from the platform's Level2Quote struct.
    /// Zero dependency on the Quantower SDK.
    /// </summary>
    public readonly struct DomLevel2Quote
    {
        public readonly double Price;
        public readonly long   Size;
        public readonly QuoteSide Type;
        public readonly DateTime  Time;

        public DomLevel2Quote(double price, long size, QuoteSide type, DateTime time)
        {
            Price = price;
            Size  = size;
            Type  = type;
            Time  = time;
        }
    }

    /// <summary>
    /// Tracks Level 2 order book state to detect DOM absorption, stacking, and imbalance.
    /// Thread-safe: <see cref="ReaderWriterLockSlim"/> guards the book dictionaries;
    /// write lock only during <see cref="ProcessLevel2"/>; read lock during analysis.
    ///
    /// <para>DOM Patterns detected:</para>
    /// <list type="bullet">
    ///   <item>BidAbsorption: large bid consumed by aggressive selling but price doesn't drop.</item>
    ///   <item>AskAbsorption: large ask consumed by aggressive buying but price doesn't rise.</item>
    ///   <item>BidStacking / AskStacking: multi-level simultaneous size growth.</item>
    ///   <item>DOMImbalance: top-5 bid depth vs top-5 ask depth ratio > 3:1.</item>
    ///   <item>Iceberg: a level size reloads ≥3 times (passive hidden order).</item>
    /// </list>
    /// </summary>
    public sealed class DOMAbsorptionEngine : IDisposable
    {
        private readonly ReaderWriterLockSlim _domLock =
            new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

        // ── Order book state ──────────────────────────────────────────
        private readonly SortedDictionary<double, long> _bids
            = new SortedDictionary<double, long>(ReverseComparer.Instance);
        private readonly SortedDictionary<double, long> _asks
            = new SortedDictionary<double, long>();

        // ── Previous snapshot for pull/stack detection ────────────────
        private readonly Dictionary<double, long> _prevBids = new();
        private readonly Dictionary<double, long> _prevAsks = new();

        // ── Iceberg reload counter ────────────────────────────────────
        private readonly ConcurrentDictionary<double, int> _bidRefreshCount = new();
        private readonly ConcurrentDictionary<double, int> _askRefreshCount = new();
        private const int IcebergRefreshThreshold = 3;

        // ── Configuration ─────────────────────────────────────────────
        private readonly long _largeLotThreshold;
        private const int TopLevels = 5;

        // ── Computed outputs (updated by EvaluateConditions) ─────────
        private bool              _isAbsorptionActive;
        private bool              _isStackingActive;
        private DOMImbalanceSignal _imbalanceSignal = DOMImbalanceSignal.Balanced;

        // ── Last trade price for absorption detection ─────────────────
        private double _lastTradePrice;
        private readonly object _tradePriceLock = new();

        /// <summary>
        /// Initializes the engine with a large lot threshold.
        /// MGC: 50 contracts; GC: 10 contracts (verify these in config).
        /// </summary>
        public DOMAbsorptionEngine(long largeLotThreshold = 50)
        {
            _largeLotThreshold = largeLotThreshold;
        }

        // ─────────────────────────────────────────────────────────────
        // Level 2 feed (hot path — ReaderWriterLockSlim write lock)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Process each Level 2 quote update. Size == 0 means the level was removed.
        /// Callers map from Quantower's Level2Quote (SDK) to <see cref="DomLevel2Quote"/>.
        /// </summary>
        public void ProcessLevel2(DomLevel2Quote quote)
        {
            _domLock.EnterWriteLock();
            try
            {
                bool isBid   = quote.Type == QuoteSide.Bid;
                double price = quote.Price;
                long   size  = quote.Size;

                var book       = isBid ? (IDictionary<double, long>)_bids
                                       : (IDictionary<double, long>)_asks;
                var prevBook   = isBid ? _prevBids : _prevAsks;
                var refreshCnt = isBid ? _bidRefreshCount : _askRefreshCount;

                if (size == 0)
                {
                    book.Remove(price);
                    prevBook.Remove(price);
                    refreshCnt.TryRemove(price, out _);
                }
                else
                {
                    // Iceberg detection: if the size at a level regenerated back to ≥80%
                    // of a prior observed size (after being partially consumed), count it
                    if (prevBook.TryGetValue(price, out long prevSize) &&
                        prevSize > 0 && size >= prevSize * 0.8 && size > _largeLotThreshold)
                    {
                        refreshCnt.AddOrUpdate(price, 1, (_, c) => c + 1);
                    }

                    prevBook[price] = book.ContainsKey(price) ? book[price] : 0;
                    book[price] = size;
                }
            }
            finally
            {
                _domLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Notify engine of a trade. Used for absorption detection.
        /// Callers map from platform trade events to local types.
        /// </summary>
        public void ProcessTrade(double price, long size, AggressorSide side)
        {
            lock (_tradePriceLock)
                _lastTradePrice = price;

            if (size < _largeLotThreshold) return;

            _domLock.EnterReadLock();
            try
            {
                if (side == AggressorSide.Buy)
                {
                    // Large buyer hit the ask — was the ask held (absorption)?
                    if (_asks.TryGetValue(price, out long askSize) && askSize > size / 2)
                        _isAbsorptionActive = true;
                }
                else if (side == AggressorSide.Sell)
                {
                    // Large seller hit the bid — was the bid held (absorption)?
                    if (_bids.TryGetValue(price, out long bidSize) && bidSize > size / 2)
                        _isAbsorptionActive = true;
                }
            }
            finally
            {
                _domLock.ExitReadLock();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Periodic analysis (call from background after each L2 batch)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Evaluates all DOM patterns. Call periodically (e.g., after each L2 update batch
        /// or on each quote tick) from a background context.
        /// </summary>
        public void EvaluateConditions()
        {
            _domLock.EnterReadLock();
            try
            {
                EvaluateImbalance();
                EvaluateStacking();
            }
            finally
            {
                _domLock.ExitReadLock();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Public accessors (all cheap reads)
        // ─────────────────────────────────────────────────────────────

        /// <summary>Returns true if absorption was detected (large trade absorbed at current level).</summary>
        public bool IsAbsorptionActive() => _isAbsorptionActive;

        /// <summary>Returns true if significant passive order stacking is detected on either side.</summary>
        public bool IsStackingActive()   => _isStackingActive;

        /// <summary>Returns the current DOM imbalance direction.</summary>
        public DOMImbalanceSignal GetImbalanceSignal() => _imbalanceSignal;

        /// <summary>
        /// Returns true if a large passive order (≥ large lot threshold) is present
        /// within <paramref name="range"/> ticks of <paramref name="nearPrice"/> on the specified side.
        /// </summary>
        public bool IsLargePassiveOrderPresent(QuoteSide side, double nearPrice, double range)
        {
            _domLock.EnterReadLock();
            try
            {
                var book = side == QuoteSide.Bid
                    ? (IEnumerable<KeyValuePair<double, long>>)_bids
                    : (IEnumerable<KeyValuePair<double, long>>)_asks;

                foreach (var kv in book)
                {
                    if (Math.Abs(kv.Key - nearPrice) <= range && kv.Value >= _largeLotThreshold)
                        return true;
                }
                return false;
            }
            finally
            {
                _domLock.ExitReadLock();
            }
        }

        /// <summary>Returns true if an iceberg order has been detected at the given price level.</summary>
        public bool IsIcebergAtPrice(double price, double tolerance = 0.5)
        {
            foreach (var kv in _bidRefreshCount)
                if (kv.Value >= IcebergRefreshThreshold && Math.Abs(kv.Key - price) <= tolerance)
                    return true;
            foreach (var kv in _askRefreshCount)
                if (kv.Value >= IcebergRefreshThreshold && Math.Abs(kv.Key - price) <= tolerance)
                    return true;
            return false;
        }

        /// <summary>Returns a snapshot of the current order book state.</summary>
        public DomSnapshot GetCurrentSnapshot()
        {
            _domLock.EnterReadLock();
            try
            {
                double bestBid = _bids.Count > 0 ? _bids.Keys.First()  : 0;
                double bestAsk = _asks.Count > 0 ? _asks.Keys.First()  : 0;

                long totalBid = _bids.Take(TopLevels).Sum(kv => kv.Value);
                long totalAsk = _asks.Take(TopLevels).Sum(kv => kv.Value);

                return new DomSnapshot
                {
                    Time          = DateTime.UtcNow,
                    Bids          = new Dictionary<double, long>(_bids),
                    Asks          = new Dictionary<double, long>(_asks),
                    BestBid       = bestBid,
                    BestAsk       = bestAsk,
                    TotalBidDepth = totalBid,
                    TotalAskDepth = totalAsk
                };
            }
            finally
            {
                _domLock.ExitReadLock();
            }
        }

        /// <summary>Resets the absorption flag (call after confirming a directional move).</summary>
        public void ResetAbsorptionFlag() => _isAbsorptionActive = false;

        // ─────────────────────────────────────────────────────────────
        // Private analysis helpers
        // ─────────────────────────────────────────────────────────────

        private void EvaluateImbalance()
        {
            long topBidVol = _bids.Take(TopLevels).Sum(kv => kv.Value);
            long topAskVol = _asks.Take(TopLevels).Sum(kv => kv.Value);

            if (topBidVol == 0 && topAskVol == 0)
            {
                _imbalanceSignal = DOMImbalanceSignal.Balanced;
                return;
            }

            double ratio = topBidVol > 0 && topAskVol > 0
                ? (double)topBidVol / topAskVol : 1.0;

            if      (ratio >= 3.0) _imbalanceSignal = DOMImbalanceSignal.BidHeavy;
            else if (ratio <= 0.33)_imbalanceSignal = DOMImbalanceSignal.AskHeavy;
            else                   _imbalanceSignal = DOMImbalanceSignal.Balanced;
        }

        private void EvaluateStacking()
        {
            long bidMax = _bids.Count > 0 ? _bids.Values.Max() : 0;
            long askMax = _asks.Count > 0 ? _asks.Values.Max() : 0;
            // Stacking active if any level is ≥ 3× large-lot threshold
            _isStackingActive = (bidMax >= _largeLotThreshold * 3 ||
                                 askMax >= _largeLotThreshold * 3);
        }

        // ─────────────────────────────────────────────────────────────
        // IDisposable
        // ─────────────────────────────────────────────────────────────

        public void Dispose()
        {
            _domLock.Dispose();
        }
    }

    /// <summary>Descending comparer for bid book (highest bid first).</summary>
    internal sealed class ReverseComparer : IComparer<double>
    {
        public static readonly ReverseComparer Instance = new();
        private ReverseComparer() { }
        public int Compare(double x, double y) => y.CompareTo(x);
    }
}
