using System;
using System.Collections.Generic;
using LucidGoldFlowScalper.Models;
using TradingPlatform.BusinessLayer; // Needed if taking real Bar objects

namespace LucidGoldFlowScalper.Engines
{
    public class FvgZone
    {
        public bool IsBullish { get; set; }
        public double TopPrice { get; set; }
        public double BottomPrice { get; set; }
        public double MidPrice => (TopPrice + BottomPrice) / 2.0;
        public int AgeBars { get; set; }
        public bool IsFilled { get; set; }
    }

    public class OrderBlock
    {
        public bool IsBullish { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public int AgeBars { get; set; }
        public bool IsMitigated { get; set; }
    }

    /// <summary>
    /// Analyzes swings, bias, CHoCH/MSS, FVGs, and OrderBlocks.
    /// Operates purely on Bar Close events.
    /// </summary>
    public class MarketStructureEngine
    {
        private int _swingStrength;

        public MarketBiasEnum ConsolidatedBias { get; private set; }
        
        public List<FvgZone> ActiveBullishFVGs { get; private set; } = new List<FvgZone>();
        public List<FvgZone> ActiveBearishFVGs { get; private set; } = new List<FvgZone>();
        
        public List<OrderBlock> ActiveBullishOBs { get; private set; } = new List<OrderBlock>();
        public List<OrderBlock> ActiveBearishOBs { get; private set; } = new List<OrderBlock>();

        // Liquidity
        public double PDH { get; private set; }
        public double PDL { get; private set; }

        public MarketStructureEngine(int swingStrength)
        {
            _swingStrength = swingStrength;
            ConsolidatedBias = MarketBiasEnum.Neutral;
        }

        public void OnNewBar(IIHistoryItem[] recentBars)
        {
            if (recentBars.Length < _swingStrength * 2 + 1) return;

            AgeZones();
            CheckFVGs(recentBars);
            CheckMitigations(recentBars[0][PriceType.Close]); // recentBars[0] is latest closed bar
            
            // Re-evaluate Bias (placeholder for full multi-timeframe swing logic)
            // Typically requires aggregating 5m, 15m, 1H swing highs/lows
            // For now, simple fallback
        }

        private void AgeZones()
        {
            foreach (var fvg in ActiveBullishFVGs) fvg.AgeBars++;
            foreach (var fvg in ActiveBearishFVGs) fvg.AgeBars++;
            foreach (var ob in ActiveBullishOBs) ob.AgeBars++;
            foreach (var ob in ActiveBearishOBs) ob.AgeBars++;

            ActiveBullishFVGs.RemoveAll(f => f.IsFilled || f.AgeBars > 50);
            ActiveBearishFVGs.RemoveAll(f => f.IsFilled || f.AgeBars > 50);
            ActiveBullishOBs.RemoveAll(ob => ob.IsMitigated || ob.AgeBars > 50);
            ActiveBearishOBs.RemoveAll(ob => ob.IsMitigated || ob.AgeBars > 50);
        }

        private void CheckFVGs(IIHistoryItem[] bars)
        {
            // Need at least 3 bars to form FVG: [i-2], [i-1], [i]
            // We look at the most recently completed 3-bar sequence
            if (bars.Length < 3) return;

            var c0 = bars[0]; // Current completed
            var c1 = bars[1]; // Middle
            var c2 = bars[2]; // Oldest
            
            // Bullish FVG: Low of c0 > High of c2
            if (c0[PriceType.Low] > c2[PriceType.High])
            {
                ActiveBullishFVGs.Add(new FvgZone 
                { 
                    IsBullish = true, 
                    TopPrice = c0[PriceType.Low], 
                    BottomPrice = c2[PriceType.High],
                    AgeBars = 0,
                    IsFilled = false
                });
            }

            // Bearish FVG: High of c0 < Low of c2
            if (c0[PriceType.High] < c2[PriceType.Low])
            {
                ActiveBearishFVGs.Add(new FvgZone
                {
                    IsBullish = false,
                    TopPrice = c2[PriceType.Low],
                    BottomPrice = c0[PriceType.High],
                    AgeBars = 0,
                    IsFilled = false
                });
            }
        }

        private void CheckMitigations(double currentClose)
        {
            foreach (var fvg in ActiveBullishFVGs)
                if (currentClose < fvg.MidPrice) fvg.IsFilled = true;

            foreach (var fvg in ActiveBearishFVGs)
                if (currentClose > fvg.MidPrice) fvg.IsFilled = true;
                
            foreach(var ob in ActiveBullishOBs)
                if (currentClose < ob.Low) ob.IsMitigated = true;
                
            foreach(var ob in ActiveBearishOBs)
                if (currentClose > ob.High) ob.IsMitigated = true;
        }

        public void UpdateDailyLevels(double priorHigh, double priorLow)
        {
            PDH = priorHigh;
            PDL = priorLow;
        }
    }
}
