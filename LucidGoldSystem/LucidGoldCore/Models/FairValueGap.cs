// FairValueGap.cs  |  LucidGold.Core.Models
using System;
using LucidGold.Core.Enums;

namespace LucidGold.Core.Models
{
    /// <summary>Represents a Fair Value Gap (FVG) identified on a price chart.</summary>
    public sealed class FairValueGap
    {
        public double          Top             { get; init; }
        public double          Bottom          { get; init; }
        public SignalDirection  Direction       { get; init; }
        public DateTime        FormedTime      { get; init; }
        public bool            Filled          { get; set; }
        public bool            PartiallyFilled { get; set; }
        public bool            Void            { get; set; }
        public double          Strength        { get; init; }
        public string          Timeframe       { get; init; } = string.Empty;
        public double          PriorityScore   { get; set; }

        public bool   IsActive  => !Filled && !Void;
        public double Midpoint  => (Top + Bottom) / 2.0;
        public double GapSize   => Top - Bottom;
    }
}
