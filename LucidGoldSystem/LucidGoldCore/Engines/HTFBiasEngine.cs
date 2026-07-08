// ============================================================
// HTFBiasEngine.cs  |  LucidGold.Core.Engines
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using LucidGold.Core.Enums;
using LucidGold.Core.Models;

namespace LucidGold.Core.Engines
{
    /// <summary>
    /// Analyzes Daily and 4H bar structure to determine higher timeframe market bias.
    /// Uses fractal pivot method (2 bars each side).
    /// </summary>
    public sealed class HTFBiasEngine
    {
        private MarketBias _currentBias    = MarketBias.Neutral;
        private DateTime   _lastRefreshUtc = DateTime.MinValue;
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
        private readonly object _lock = new object();

        // Cached bar data stored as (High, Low) tuples
        private IReadOnlyList<(double High, double Low)> _dailyBars   = Array.Empty<(double, double)>();
        private IReadOnlyList<(double High, double Low)> _h4Bars       = Array.Empty<(double, double)>();

        private readonly Action<string> _debugLog;

        public HTFBiasEngine(Action<string>? debugLog = null)
        {
            _debugLog = debugLog ?? (_ => { });
        }

        /// <summary>Returns the current market bias. Thread-safe.</summary>
        public MarketBias GetCurrentBias()
        {
            lock (_lock) return _currentBias;
        }

        /// <summary>
        /// Refreshes bias from Daily and 4H bars. Call from OnNewBar or a background timer.
        /// The Indicator/Strategy is responsible for providing the bars (abstracts SDK dependency).
        /// Bars should be ordered oldest-first.
        /// </summary>
        public void Refresh(IEnumerable<Bar> dailyBars, IEnumerable<Bar> h4Bars)
        {
            if (DateTime.UtcNow - _lastRefreshUtc < RefreshInterval) return;

            try
            {
                _dailyBars = dailyBars.Select(b => (b.High, b.Low)).ToList();
                _h4Bars    = h4Bars.Select(b => (b.High, b.Low)).ToList();

                lock (_lock)
                {
                    _currentBias    = CalculateCombinedBias();
                    _lastRefreshUtc = DateTime.UtcNow;
                }

                _debugLog($"[HTFBias] Bias={_currentBias} | Daily={_dailyBars.Count} bars | H4={_h4Bars.Count} bars");
            }
            catch (Exception ex)
            {
                _debugLog($"[HTFBias] Refresh error: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────

        private MarketBias CalculateCombinedBias()
        {
            var daily = AnalyzeStructure(_dailyBars);
            var h4    = AnalyzeStructure(_h4Bars);

            if      (daily == MarketBias.Bullish && h4 == MarketBias.Bullish) return MarketBias.StrongBullish;
            else if (daily == MarketBias.Bearish && h4 == MarketBias.Bearish) return MarketBias.StrongBearish;
            else if (daily == MarketBias.Bullish || h4 == MarketBias.Bullish) return MarketBias.Bullish;
            else if (daily == MarketBias.Bearish || h4 == MarketBias.Bearish) return MarketBias.Bearish;
            else                                                              return MarketBias.Neutral;
        }

        /// <summary>
        /// Determines directional bias using 2-bar fractal pivots.
        /// HH + HL = Bullish, LH + LL = Bearish, otherwise Neutral.
        /// </summary>
        private static MarketBias AnalyzeStructure(IReadOnlyList<(double High, double Low)> bars)
        {
            if (bars.Count < 10) return MarketBias.Neutral;

            var highs = FindFractalHighs(bars, pivotBars: 2);
            var lows  = FindFractalLows(bars,  pivotBars: 2);

            if (highs.Count < 2 || lows.Count < 2) return MarketBias.Neutral;

            double prevHigh = highs[highs.Count - 2];
            double lastHigh = highs[highs.Count - 1];
            double prevLow  = lows[lows.Count - 2];
            double lastLow  = lows[lows.Count - 1];

            bool hh = lastHigh > prevHigh;
            bool hl = lastLow  > prevLow;
            bool lh = lastHigh < prevHigh;
            bool ll = lastLow  < prevLow;

            if (hh && hl) return MarketBias.Bullish;
            if (lh && ll) return MarketBias.Bearish;
            return MarketBias.Neutral;
        }

        private static List<double> FindFractalHighs(IReadOnlyList<(double High, double Low)> bars, int pivotBars)
        {
            var result = new List<double>();
            for (int i = pivotBars; i < bars.Count - pivotBars; i++)
            {
                double high = bars[i].High;
                bool   ok   = true;
                for (int j = 1; j <= pivotBars && ok; j++)
                    if (bars[i - j].High >= high || bars[i + j].High >= high) ok = false;
                if (ok) result.Add(high);
            }
            return result;
        }

        private static List<double> FindFractalLows(IReadOnlyList<(double High, double Low)> bars, int pivotBars)
        {
            var result = new List<double>();
            for (int i = pivotBars; i < bars.Count - pivotBars; i++)
            {
                double low = bars[i].Low;
                bool   ok  = true;
                for (int j = 1; j <= pivotBars && ok; j++)
                    if (bars[i - j].Low <= low || bars[i + j].Low <= low) ok = false;
                if (ok) result.Add(low);
            }
            return result;
        }
    }
}
