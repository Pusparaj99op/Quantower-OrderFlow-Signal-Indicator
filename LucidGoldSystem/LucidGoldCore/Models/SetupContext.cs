// SetupContext.cs  |  LucidGold.Core.Models
using System;
using LucidGold.Core.Enums;

namespace LucidGold.Core.Models
{
    /// <summary>Aggregates all signal engine outputs for a potential trade setup.</summary>
    public sealed class SetupContext
    {
        public DateTime        Time                  { get; init; }
        public string          Symbol                { get; init; } = string.Empty;
        public SignalDirection  ProposedDirection     { get; set; }
        // HTF Bias
        public MarketBias      HTFBias               { get; set; }
        // Kill Zone
        public string          ActiveKillZone        { get; set; } = "None";
        public bool            IsSilverBullet        { get; set; }
        // Market Structure
        public StructurePoint? LatestStructureEvent  { get; set; }
        // FVG
        public FairValueGap?   NearestFVG            { get; set; }
        // Order Block
        public OrderBlockZone? NearestOrderBlock     { get; set; }
        // Footprint
        public bool            HasStackedImbalance   { get; set; }
        public bool            HasAbsorption         { get; set; }
        public bool            HasDeltaFlip          { get; set; }
        // DOM
        public bool              DOMAbsorptionActive { get; set; }
        public bool              DOMStackingActive   { get; set; }
        public DOMImbalanceSignal DOMImbalance       { get; set; }
        // Delta
        public DeltaTrend      DeltaSlope            { get; set; }
        public bool            IsDeltaDivergent      { get; set; }
        // Tape
        public bool            IsLargeBlockBurst     { get; set; }
        public bool            IsTapeAccelerating    { get; set; }
        // ATR
        public double          ATR20                 { get; set; }
        // Computed score
        public float           SignalScore           { get; set; }
        // Swing levels for SL placement
        public double          SwingHigh             { get; set; }
        public double          SwingLow              { get; set; }
        public double          EntryZoneTop          { get; set; }
        public double          EntryZoneBottom       { get; set; }
    }
}
