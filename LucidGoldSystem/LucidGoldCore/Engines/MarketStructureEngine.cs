// ============================================================
// MarketStructureEngine.cs  |  LucidGold.Core.Engines
// ============================================================
using System;
using System.Collections.Generic;
using LucidGold.Core.Enums;
using LucidGold.Core.Models;

namespace LucidGold.Core.Engines
{
    /// <summary>
    /// Detects BOS (Break of Structure), CHoCH (Change of Character), and MSS
    /// (Market Structure Shift) on 5M and 15M bars using a 3-bar fractal pivot method.
    /// Maintains a rolling 100-bar buffer. Calculates Wilder's 20-bar ATR.
    /// </summary>
    public sealed class MarketStructureEngine
    {
        // ─── Rolling bar buffer (fixed array, O(1) writes) ───────────
        private const int BufferSize = 100;
        private readonly double[] _opens   = new double[BufferSize];
        private readonly double[] _highs   = new double[BufferSize];
        private readonly double[] _lows    = new double[BufferSize];
        private readonly double[] _closes  = new double[BufferSize];
        private readonly DateTime[] _times = new DateTime[BufferSize];
        private int  _head  = 0;   // index of most-recently written bar
        private int  _count = 0;   // total bars written (capped at BufferSize)

        // ─── ATR (Wilder's smoothing) ─────────────────────────────────
        private double _atr20     = 0;
        private double _prevClose = 0;

        // ─── Structure state ──────────────────────────────────────────
        private StructurePoint? _latestEvent;
        private double _lastSwingHigh = 0;
        private double _lastSwingLow  = double.MaxValue;
        private bool   _trendBullish  = false;  // current working trend assumption

        private readonly object _lock = new object();

        // ─── Pivot lookback ───────────────────────────────────────────
        private const int PivotBars = 3;

        // ─────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────

        /// <summary>Feed each new closed bar into this method.</summary>
        public void ProcessNewBar(Bar bar, string timeframe)
        {
            lock (_lock)
            {
                WriteBar(bar);
                UpdateATR(bar);
                DetectStructure(timeframe);
            }
        }

        /// <summary>Returns the most recently detected structure event, or null.</summary>
        public StructurePoint? GetLatestStructureEvent() { lock (_lock) return _latestEvent; }

        /// <summary>Returns the most recent confirmed swing high price.</summary>
        public double GetSwingHigh() { lock (_lock) return _lastSwingHigh; }

        /// <summary>Returns the most recent confirmed swing low price.</summary>
        public double GetSwingLow()  { lock (_lock) return _lastSwingLow; }

        /// <summary>Returns the current 20-bar Wilder ATR value.</summary>
        public double GetATR20()     { lock (_lock) return _atr20; }

        // ─────────────────────────────────────────────────────────────
        // Private — buffer management
        // ─────────────────────────────────────────────────────────────

        private void WriteBar(Bar bar)
        {
            _head = (_head + 1) % BufferSize;
            _opens[_head]  = bar.Open;
            _highs[_head]  = bar.High;
            _lows[_head]   = bar.Low;
            _closes[_head] = bar.Close;
            _times[_head]  = bar.Time;
            if (_count < BufferSize) _count++;
        }

        // Retrieves bar at logical index i from head (0 = newest, 1 = one before, etc.)
        private int Idx(int i) => (_head - i + BufferSize) % BufferSize;

        // ─────────────────────────────────────────────────────────────
        // Private — ATR (Wilder's method, 20-period)
        // ─────────────────────────────────────────────────────────────

        private void UpdateATR(Bar bar)
        {
            if (_prevClose == 0) { _prevClose = bar.Close; return; }

            double tr = Math.Max(bar.High - bar.Low,
                        Math.Max(Math.Abs(bar.High - _prevClose),
                                 Math.Abs(bar.Low  - _prevClose)));

            _atr20 = _atr20 == 0
                ? tr
                : (_atr20 * 19.0 + tr) / 20.0;   // Wilder's smoothing

            _prevClose = bar.Close;
        }

        // ─────────────────────────────────────────────────────────────
        // Private — structure detection
        // ─────────────────────────────────────────────────────────────

        private void DetectStructure(string timeframe)
        {
            if (_count < PivotBars * 2 + 1) return;

            // Update swing levels from fractal pivots (3-bar, needs at least 7 bars)
            if (_count >= PivotBars * 2 + 1)
            {
                TryUpdateSwingLevels();
            }

            if (_lastSwingHigh == 0 || _lastSwingLow == double.MaxValue) return;

            double close = _closes[_head];
            double body  = Math.Abs(_closes[_head] - _opens[_head]);

            // ── BOS: close beyond previous swing in direction of working trend ──
            if (_trendBullish && close > _lastSwingHigh)
            {
                _latestEvent = new StructurePoint
                {
                    EventType              = StructureEventType.BOS,
                    Direction              = SignalDirection.Long,
                    Price                  = _lastSwingHigh,
                    Time                   = _times[_head],
                    DisplacementATRMultiple= _atr20 > 0 ? body / _atr20 : 0
                };
                _lastSwingHigh = close;  // advance
                return;
            }

            if (!_trendBullish && close < _lastSwingLow)
            {
                _latestEvent = new StructurePoint
                {
                    EventType              = StructureEventType.BOS,
                    Direction              = SignalDirection.Short,
                    Price                  = _lastSwingLow,
                    Time                   = _times[_head],
                    DisplacementATRMultiple= _atr20 > 0 ? body / _atr20 : 0
                };
                _lastSwingLow = close;  // advance
                return;
            }

            // ── CHoCH: close breaks swing point OPPOSITE to current trend ──
            if (_trendBullish && close < _lastSwingLow)
            {
                bool isMSS = _atr20 > 0 && body > _atr20 * 1.5;
                (double fvgTop, double fvgBottom)? displacementFVG = null;

                if (isMSS && _count >= 3)
                {
                    // FVG = gap between candle[i-1].High and candle[i+1].Low (bearish MSS)
                    double prevHigh  = _highs[Idx(1)];
                    double nextLow   = _count >= 2 ? _lows[Idx(0)] : 0;
                    if (prevHigh > 0 && nextLow > 0 && prevHigh > nextLow)
                        displacementFVG = (prevHigh, nextLow);
                }

                _latestEvent = new StructurePoint
                {
                    EventType              = isMSS ? StructureEventType.MSS : StructureEventType.CHoCH,
                    Direction              = SignalDirection.Short,
                    Price                  = _lastSwingLow,
                    Time                   = _times[_head],
                    DisplacementATRMultiple= _atr20 > 0 ? body / _atr20 : 0,
                    DisplacementFVG        = displacementFVG
                };
                _trendBullish = false;
                return;
            }

            if (!_trendBullish && close > _lastSwingHigh)
            {
                bool isMSS = _atr20 > 0 && body > _atr20 * 1.5;
                (double fvgTop, double fvgBottom)? displacementFVG = null;

                if (isMSS && _count >= 2)
                {
                    double prevLow  = _lows[Idx(1)];
                    double nextHigh = _highs[Idx(0)];
                    if (nextHigh > 0 && prevLow > 0 && nextHigh > prevLow)
                        displacementFVG = (nextHigh, prevLow);
                }

                _latestEvent = new StructurePoint
                {
                    EventType              = isMSS ? StructureEventType.MSS : StructureEventType.CHoCH,
                    Direction              = SignalDirection.Long,
                    Price                  = _lastSwingHigh,
                    Time                   = _times[_head],
                    DisplacementATRMultiple= _atr20 > 0 ? body / _atr20 : 0,
                    DisplacementFVG        = displacementFVG
                };
                _trendBullish = true;
            }
        }

        private void TryUpdateSwingLevels()
        {
            // Check bar at position PivotBars from head (the pivot candidate)
            int pivot = PivotBars;
            double pivHigh = _highs[Idx(pivot)];
            double pivLow  = _lows[Idx(pivot)];
            bool isSwingHigh = true, isSwingLow = true;

            for (int j = 1; j <= PivotBars; j++)
            {
                if (_highs[Idx(pivot - j)] >= pivHigh || _highs[Idx(pivot + j)] >= pivHigh)
                    isSwingHigh = false;
                if (_lows[Idx(pivot - j)] <= pivLow || _lows[Idx(pivot + j)] <= pivLow)
                    isSwingLow = false;
            }

            if (isSwingHigh && pivHigh > _lastSwingHigh)
                _lastSwingHigh = pivHigh;

            if (isSwingLow && (pivLow < _lastSwingLow || _lastSwingLow == double.MaxValue))
                _lastSwingLow = pivLow;
        }
    }
}
