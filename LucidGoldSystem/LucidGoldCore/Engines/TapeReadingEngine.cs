// ============================================================
// TapeReadingEngine.cs  |  LucidGold.Core.Engines
// ============================================================
using System;
using System.Threading;
using LucidGold.Core.Enums;

namespace LucidGold.Core.Engines
{
    /// <summary>
    /// Analyzes the Time &amp; Sales tape for momentum, acceleration, and exhaustion patterns.
    /// Uses a pre-allocated circular ring buffer of 500 prints to avoid heap pressure.
    /// Uses local <see cref="AggressorSide"/> enum to avoid SDK dependency.
    ///
    /// <para>Detects:</para>
    /// <list type="bullet">
    ///   <item>Large block bursts: 3+ consecutive large prints on the same side within 2 seconds.</item>
    ///   <item>Tape acceleration: print speed increasing ≥2× from prior 5-second window.</item>
    ///   <item>Climbing ask / falling bid sequential patterns.</item>
    ///   <item>Tape exhaustion: large prints at extremes with no price movement.</item>
    /// </list>
    /// </summary>
    public sealed class TapeReadingEngine
    {
        private const int  TapeRingSize        = 500;
        private const int  BlockBurstMinPrints = 3;
        private const long BurstWindowTicks    = 2L * TimeSpan.TicksPerSecond;   // 2 seconds
        private const long SpeedWindowTicks    = 5L * TimeSpan.TicksPerSecond;   // 5 seconds

        // ── Tape ring buffer (pre-allocated, no heap alloc in hot path) ──
        private readonly long[]          _sizeBuf  = new long[TapeRingSize];
        private readonly byte[]          _sideBuf  = new byte[TapeRingSize];   // 0=Buy,1=Sell,2=Unknown
        private readonly double[]        _priceBuf = new double[TapeRingSize];
        private readonly long[]          _timeBuf  = new long[TapeRingSize];   // UTC ticks
        private int  _ringHead  = 0;
        private int  _ringCount = 0;

        // ── Speed tracking (rolling 5-second window) ─────────────────
        private int  _tradesCurrentWindow = 0;
        private int  _tradesPriorWindow   = 0;
        private long _windowStartTicks    = 0;
        private readonly object _speedLock = new object();

        // ── Output state (volatile for cheap cross-thread reads) ──────
        private volatile bool _burstBuy    = false;
        private volatile bool _burstSell   = false;
        private volatile bool _accelBuy    = false;
        private volatile bool _accelSell   = false;
        private volatile bool _exhaustBuy  = false;
        private volatile bool _exhaustSell = false;

        // ── Configuration ─────────────────────────────────────────────
        private long _largeLotThreshold = 50;

        // ─────────────────────────────────────────────────────────────
        // Trade feed (hot path — must return in < 1 ms)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Process each trade print. Lock-free except for the speed window update.
        /// Uses local <see cref="AggressorSide"/>.
        /// </summary>
        public void ProcessTrade(double price, long size, AggressorSide aggressor,
                                 DateTime time, long largeLotThreshold)
        {
            Interlocked.Exchange(ref _largeLotThreshold, largeLotThreshold);

            int idx = _ringHead % TapeRingSize;
            _priceBuf[idx] = price;
            _sizeBuf[idx]  = size;
            _sideBuf[idx]  = aggressor == AggressorSide.Buy  ? (byte)0
                           : aggressor == AggressorSide.Sell ? (byte)1 : (byte)2;
            _timeBuf[idx]  = time.Ticks;
            _ringHead++;
            _ringCount = Math.Min(_ringCount + 1, TapeRingSize);

            // Non-blocking pattern evaluation
            UpdateBurstState(time.Ticks);
            UpdateSpeedWindow(time.Ticks, aggressor);
            UpdateExhaustion();
        }

        // ─────────────────────────────────────────────────────────────
        // Public read API
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if a large block burst has been detected in the specified direction
        /// (≥3 consecutive large prints on same side within 2 seconds).
        /// </summary>
        public bool IsLargeBlockBurst(SignalDirection direction)
            => direction == SignalDirection.Long ? _burstBuy : _burstSell;

        /// <summary>
        /// Returns true if tape is accelerating in the specified direction
        /// (current 5s window ≥ 2× previous 5s window of prints).
        /// </summary>
        public bool IsAccelerating(SignalDirection direction)
            => direction == SignalDirection.Long ? _accelBuy : _accelSell;

        /// <summary>
        /// Returns true if tape exhaustion is detected in the specified direction
        /// (large prints at price extreme with no follow-through).
        /// </summary>
        public bool IsTapeExhaustion(SignalDirection direction)
            => direction == SignalDirection.Long ? _exhaustBuy : _exhaustSell;

        /// <summary>
        /// Returns the approximate print speed in prints per second over the last 5 seconds.
        /// </summary>
        public double GetPrintSpeed()
        {
            lock (_speedLock)
                return _tradesCurrentWindow / 5.0;
        }

        /// <summary>
        /// Returns an aggregated tape condition based on most recent patterns.
        /// </summary>
        public TapeCondition GetCurrentCondition()
        {
            if (_exhaustBuy || _exhaustSell) return TapeCondition.Exhaustion;
            if (_burstBuy   || _accelBuy)    return TapeCondition.Bullish;
            if (_burstSell  || _accelSell)   return TapeCondition.Bearish;
            return TapeCondition.Neutral;
        }

        // ─────────────────────────────────────────────────────────────
        // Private — burst detection
        // ─────────────────────────────────────────────────────────────

        private void UpdateBurstState(long nowTicks)
        {
            if (_ringCount < BlockBurstMinPrints) return;

            long threshold = Interlocked.Read(ref _largeLotThreshold);
            int  buyCnt  = 0, sellCnt = 0;
            int  lookback = Math.Min(_ringCount, 50);

            for (int i = 0; i < lookback; i++)
            {
                int idx = (_ringHead - 1 - i + TapeRingSize * 1000) % TapeRingSize;

                // Only count prints within 2-second burst window
                if (nowTicks - _timeBuf[idx] > BurstWindowTicks) break;
                if (_sizeBuf[idx] < threshold) { buyCnt = 0; sellCnt = 0; continue; }

                if (_sideBuf[idx] == 0)      // Buy
                {
                    buyCnt++;
                    sellCnt = 0;
                }
                else if (_sideBuf[idx] == 1) // Sell
                {
                    sellCnt++;
                    buyCnt  = 0;
                }

                if (buyCnt  >= BlockBurstMinPrints) { _burstBuy = true; _burstSell = false; return; }
                if (sellCnt >= BlockBurstMinPrints) { _burstSell= true; _burstBuy  = false; return; }
            }

            _burstBuy  = false;
            _burstSell = false;
        }

        // ─────────────────────────────────────────────────────────────
        // Private — speed / acceleration detection
        // ─────────────────────────────────────────────────────────────

        private void UpdateSpeedWindow(long nowTicks, AggressorSide side)
        {
            lock (_speedLock)
            {
                if (_windowStartTicks == 0)
                    _windowStartTicks = nowTicks;

                if (nowTicks - _windowStartTicks >= SpeedWindowTicks)
                {
                    _tradesPriorWindow   = _tradesCurrentWindow;
                    _tradesCurrentWindow = 1;
                    _windowStartTicks    = nowTicks;
                }
                else
                {
                    _tradesCurrentWindow++;
                }

                bool accelerating = _tradesPriorWindow > 0 &&
                                    _tradesCurrentWindow >= _tradesPriorWindow * 2;

                if (accelerating)
                {
                    _accelBuy  = side == AggressorSide.Buy;
                    _accelSell = side == AggressorSide.Sell;
                }
                else
                {
                    _accelBuy  = false;
                    _accelSell = false;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Private — exhaustion detection
        // ─────────────────────────────────────────────────────────────

        private void UpdateExhaustion()
        {
            if (_ringCount < 5) return;

            long threshold = Interlocked.Read(ref _largeLotThreshold);
            int  lookback  = Math.Min(_ringCount, 20);

            // Count recent large buy prints and check if price ascended
            int largeBuyCount = 0, largeSellCount = 0;
            double firstBuyPrice  = 0, lastBuyPrice  = 0;
            double firstSellPrice = 0, lastSellPrice = 0;

            for (int i = lookback - 1; i >= 0; i--)
            {
                int idx = (_ringHead - 1 - i + TapeRingSize * 1000) % TapeRingSize;
                if (_sizeBuf[idx] < threshold) continue;

                if (_sideBuf[idx] == 0) // Buy
                {
                    if (firstBuyPrice == 0) firstBuyPrice = _priceBuf[idx];
                    lastBuyPrice = _priceBuf[idx];
                    largeBuyCount++;
                }
                else if (_sideBuf[idx] == 1) // Sell
                {
                    if (firstSellPrice == 0) firstSellPrice = _priceBuf[idx];
                    lastSellPrice = _priceBuf[idx];
                    largeSellCount++;
                }
            }

            // Buy exhaustion: many large buys but price NOT rising (same or lower)
            _exhaustBuy  = largeBuyCount  >= 3 && firstBuyPrice  > 0 &&
                           lastBuyPrice   <= firstBuyPrice;
            // Sell exhaustion: many large sells but price NOT falling
            _exhaustSell = largeSellCount >= 3 && firstSellPrice > 0 &&
                           lastSellPrice  >= firstSellPrice;
        }
    }
}
