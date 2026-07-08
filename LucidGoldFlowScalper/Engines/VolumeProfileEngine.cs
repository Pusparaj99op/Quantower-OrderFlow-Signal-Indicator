using System;
using LucidGoldFlowScalper.Utils;

namespace LucidGoldFlowScalper.Engines
{
    /// <summary>
    /// Computes Session Volume Profile and tracks POC/VAH/VAL.
    /// Pre-allocated fixed size arrays for O(1) tick updates.
    /// </summary>
    public class VolumeProfileEngine
    {
        private const int MAX_PRICE_LEVELS = 10000; // Large enough for daily range
        private readonly double _tickSize;
        
        private double _sessionBasePrice;
        private long[] _totalVolume;
        private long[] _buyVolume;
        private long[] _sellVolume;
        private long _sessionTotalVolume;

        public double POC { get; private set; }
        public double VAH { get; private set; }
        public double VAL { get; private set; }
        
        public double PriorPOC { get; private set; }
        public double PriorVAH { get; private set; }
        public double PriorVAL { get; private set; }

        public VolumeProfileEngine(double tickSize)
        {
            _tickSize = tickSize;
            _totalVolume = new long[MAX_PRICE_LEVELS];
            _buyVolume = new long[MAX_PRICE_LEVELS];
            _sellVolume = new long[MAX_PRICE_LEVELS];
        }

        public void ResetSession(double currentPrice)
        {
            // Save priors
            if (_sessionTotalVolume > 0)
            {
                PriorPOC = POC;
                PriorVAH = VAH;
                PriorVAL = VAL;
            }

            Array.Clear(_totalVolume, 0, MAX_PRICE_LEVELS);
            Array.Clear(_buyVolume, 0, MAX_PRICE_LEVELS);
            Array.Clear(_sellVolume, 0, MAX_PRICE_LEVELS);

            // Center the array around the current price at open
            _sessionBasePrice = currentPrice - (MAX_PRICE_LEVELS / 2 * _tickSize);
            _sessionTotalVolume = 0;
            POC = currentPrice;
            VAH = currentPrice;
            VAL = currentPrice;
        }

        public void AddTrade(double price, long size, bool isBuy)
        {
            if (size <= 0) return;

            int index = (int)Math.Round((price - _sessionBasePrice) / _tickSize);
            
            // Boundary safety
            if (index < 0 || index >= MAX_PRICE_LEVELS) return;

            _totalVolume[index] += size;
            if (isBuy) _buyVolume[index] += size;
            else _sellVolume[index] += size;
            
            _sessionTotalVolume += size;
        }

        /// <summary>
        /// Recalculates POC, VAH, VAL. Call this periodically (e.g., OnNewBar) instead of every tick.
        /// </summary>
        public void RecalculateProfile()
        {
            if (_sessionTotalVolume == 0) return;

            long maxVol = -1;
            int pocIndex = -1;

            // 1. Find POC
            for (int i = 0; i < MAX_PRICE_LEVELS; i++)
            {
                if (_totalVolume[i] > maxVol)
                {
                    maxVol = _totalVolume[i];
                    pocIndex = i;
                }
            }

            if (pocIndex == -1) return;
            POC = _sessionBasePrice + (pocIndex * _tickSize);

            // 2. Compute Value Area (70%)
            long targetVolume = (long)(_sessionTotalVolume * 0.70);
            long currentVolume = _totalVolume[pocIndex];
            
            int highIndex = pocIndex;
            int lowIndex = pocIndex;

            while (currentVolume < targetVolume)
            {
                long upperVol = (highIndex + 1 < MAX_PRICE_LEVELS) ? _totalVolume[highIndex + 1] : 0;
                long lowerVol = (lowIndex - 1 >= 0) ? _totalVolume[lowIndex - 1] : 0;

                if (upperVol == 0 && lowerVol == 0) break;

                if (upperVol >= lowerVol)
                {
                    highIndex++;
                    currentVolume += upperVol;
                }
                else
                {
                    lowIndex--;
                    currentVolume += lowerVol;
                }
            }

            VAH = _sessionBasePrice + (highIndex * _tickSize);
            VAL = _sessionBasePrice + (lowIndex * _tickSize);
        }

        public long GetVolumeAtPrice(double price)
        {
            int index = (int)Math.Round((price - _sessionBasePrice) / _tickSize);
            if (index < 0 || index >= MAX_PRICE_LEVELS) return 0;
            return _totalVolume[index];
        }
    }
}
