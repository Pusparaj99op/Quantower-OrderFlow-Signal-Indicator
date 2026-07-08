using System;

namespace LucidGoldFlowScalper.Models
{
    /// <summary>
    /// Represents the comprehensive context at the moment a signal is generated.
    /// Passed to the SignalScorer to determine setup validity.
    /// </summary>
    public class SignalContext
    {
        public bool IsLong { get; set; }
        public MarketBiasEnum ConsolidatedBias { get; set; }
        
        // Kill Zone Info
        public bool IsKillZoneActive { get; set; }
        public string CurrentKillZone { get; set; } = string.Empty;
        public float KillZoneWeight { get; set; }
        public bool IsNoTradeZone { get; set; }
        
        // Liquidity Sweep
        public bool HasLiquiditySweep { get; set; }
        public double SweepDepthTicks { get; set; }
        
        // Structure & Zones
        public int FvgAgeBars { get; set; } // 0 means no FVG
        public int OrderBlockAgeBars { get; set; } // 0 means no OB
        
        // Order Flow & Tape
        public bool HasDeltaFlip { get; set; }
        public bool HasStackedImbalance { get; set; }
        public bool HasTapeAlignment { get; set; }
        public bool HasDomAbsorption { get; set; }
        
        // Volume Profile
        public bool IsAtValueAreaEdge { get; set; }
        public bool IsCrossingLvn { get; set; }
        public bool IsAtPoc { get; set; }
        
        // Proposed Trade Parameters
        public double EntryPrice { get; set; }
        public double StopLossPrice { get; set; }
        public double TakeProfitPrice { get; set; }
        public double StopLossTicks { get; set; }
        public double TakeProfitTicks { get; set; }
    }
}
