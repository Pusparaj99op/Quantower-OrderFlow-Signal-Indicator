using System;
using System.Collections.Generic;
using LucidGoldFlowScalper.Utils;

namespace LucidGoldFlowScalper.Engines
{
    public enum TapeSignal
    {
        Neutral,
        BuyHeavy,
        SellHeavy
    }

    /// <summary>
    /// Handles order flow: cumulative delta, footprint imbalances, DOM absorption, tape reading.
    /// Lock-free, zero-allocation hot paths.
    /// </summary>
    public class OrderFlowEngine
    {
        // 5.1 Cumulative Delta
        public long SessionDelta { get; private set; }
        public long BarDelta { get; private set; }
        
        private CircularBuffer<long> _rollingDeltaTicks;
        public long RollingDelta { get; private set; }
        
        // 5.2 Imbalance
        private Dictionary<decimal, (long BuyVol, long SellVol)> _barImbalances;
        public bool HasBullishStackedImbalance { get; private set; }
        public bool HasBearishStackedImbalance { get; private set; }
        
        // 5.3 DOM Absorption
        private Dictionary<decimal, long> _bidBook;
        private Dictionary<decimal, long> _askBook;
        private int _largeOrderThreshold;
        
        public bool IsBidAbsorptionActive { get; private set; }
        public bool IsAskAbsorptionActive { get; private set; }
        public decimal AbsorptionPriceLevel { get; private set; }
        
        // 5.4 Tape Reader
        private CircularBuffer<(long Size, bool IsBuy)> _largePrints;
        private int _largePrintThreshold;
        
        public TapeSignal CurrentTapeSignal { get; private set; }
        public long LargePrintDelta { get; private set; }
        
        // 5.5 Delta Velocity
        private DateTime _lastVelocityTime;
        private long _lastVelocityDelta;
        public double DeltaVelocity { get; private set; }
        private double _velocityThreshold;

        public OrderFlowEngine(int deltaWindowTicks, int largeOrderThreshold, int largePrintThreshold, double velocityThreshold)
        {
            _rollingDeltaTicks = new CircularBuffer<long>(deltaWindowTicks);
            _largePrints = new CircularBuffer<(long, bool)>(20); // Last 20 large prints
            
            _barImbalances = new Dictionary<decimal, (long, long)>(100);
            _bidBook = new Dictionary<decimal, long>(500);
            _askBook = new Dictionary<decimal, long>(500);
            
            _largeOrderThreshold = largeOrderThreshold;
            _largePrintThreshold = largePrintThreshold;
            _velocityThreshold = velocityThreshold;
            _lastVelocityTime = DateTime.UtcNow;
        }

        public void ResetSession()
        {
            SessionDelta = 0;
            _rollingDeltaTicks.Clear();
            RollingDelta = 0;
        }

        public void OnNewBar()
        {
            BarDelta = 0;
            _barImbalances.Clear();
            HasBullishStackedImbalance = false;
            HasBearishStackedImbalance = false;
        }

        public void OnNewTrade(decimal price, long size, bool isBuy)
        {
            if (size <= 0) return;

            // Update Delta
            long signedSize = isBuy ? size : -size;
            SessionDelta += signedSize;
            BarDelta += signedSize;
            
            _rollingDeltaTicks.Push(signedSize);
            // Recompute rolling delta (simplified O(N), for extreme perf use a running sum)
            long sum = 0;
            int count = _rollingDeltaTicks.Count;
            for(int i=0; i < count; i++) sum += _rollingDeltaTicks[i];
            RollingDelta = sum;

            // Imbalance
            if (!_barImbalances.TryGetValue(price, out var vols))
                vols = (0, 0);
            if (isBuy) vols.BuyVol += size;
            else vols.SellVol += size;
            _barImbalances[price] = vols;

            // Tape Reader
            if (size >= _largePrintThreshold)
            {
                _largePrints.Push((size, isBuy));
                EvaluateTape();
            }

            // Delta Velocity (checked every ~1 second)
            var now = DateTime.UtcNow;
            if ((now - _lastVelocityTime).TotalSeconds >= 1.0)
            {
                DeltaVelocity = (SessionDelta - _lastVelocityDelta) / (now - _lastVelocityTime).TotalSeconds;
                _lastVelocityDelta = SessionDelta;
                _lastVelocityTime = now;
            }
            
            // Check Absorption
            CheckAbsorptionOnTrade(price, size, isBuy);
        }

        public void OnLevel2Update(decimal price, long size, bool isBid)
        {
            var book = isBid ? _bidBook : _askBook;
            if (size <= 0)
                book.Remove(price);
            else
                book[price] = size;
        }

        private void EvaluateTape()
        {
            int buyCount = 0;
            int sellCount = 0;
            long delta = 0;
            int n = _largePrints.Count;
            
            for (int i = 0; i < n; i++)
            {
                var p = _largePrints[i];
                if (p.IsBuy) { buyCount++; delta += p.Size; }
                else { sellCount++; delta -= p.Size; }
            }

            LargePrintDelta = delta;
            
            if (delta > 0 && buyCount >= sellCount * 2) CurrentTapeSignal = TapeSignal.BuyHeavy;
            else if (delta < 0 && sellCount >= buyCount * 2) CurrentTapeSignal = TapeSignal.SellHeavy;
            else CurrentTapeSignal = TapeSignal.Neutral;
        }
        
        private void CheckAbsorptionOnTrade(decimal price, long size, bool isBuy)
        {
            IsBidAbsorptionActive = false;
            IsAskAbsorptionActive = false;
            AbsorptionPriceLevel = 0;

            if (!isBuy) // Market Sell hitting bid
            {
                if (_bidBook.TryGetValue(price, out long restingSize) && restingSize >= _largeOrderThreshold)
                {
                    IsBidAbsorptionActive = true;
                    AbsorptionPriceLevel = price;
                }
            }
            else // Market Buy hitting ask
            {
                if (_askBook.TryGetValue(price, out long restingSize) && restingSize >= _largeOrderThreshold)
                {
                    IsAskAbsorptionActive = true;
                    AbsorptionPriceLevel = price;
                }
            }
        }
        
        /// <summary>
        /// Called periodically (e.g. on bar close) to scan for stacked imbalances.
        /// </summary>
        public void EvaluateImbalances()
        {
            // Simplified logic: scan _barImbalances for 3 consecutive levels 
            // In a real footprint you sort by price and check contiguous levels.
            // Placeholder logic to demonstrate flag setting.
            HasBullishStackedImbalance = false; 
            HasBearishStackedImbalance = false;
            // Requires sorted price array logic here...
        }
    }
}
