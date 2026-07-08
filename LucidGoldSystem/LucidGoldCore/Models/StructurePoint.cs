// StructurePoint.cs  |  LucidGold.Core.Models
using System;
using LucidGold.Core.Enums;

namespace LucidGold.Core.Models
{
    // StructureEventType is defined in LucidGold.Core.Enums.LucidEnums

    /// <summary>Represents a market structure event (BOS/CHoCH/MSS).</summary>
    public sealed class StructurePoint
    {
        public StructureEventType EventType               { get; init; }
        public SignalDirection     Direction              { get; init; }
        public double              Price                  { get; init; }
        public DateTime            Time                   { get; init; }
        /// <summary>Body of displacement candle as multiple of ATR20.</summary>
        public double              DisplacementATRMultiple{ get; init; }
        /// <summary>FVG price range left by displacement candle, if MSS.</summary>
        public (double Top, double Bottom)? DisplacementFVG { get; init; }
    }
}
