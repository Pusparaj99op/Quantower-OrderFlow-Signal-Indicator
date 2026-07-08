// OrderBlockZone.cs  |  LucidGold.Core.Models
using System;
using LucidGold.Core.Enums;

namespace LucidGold.Core.Models
{
    /// <summary>Represents an Order Block zone on the chart.</summary>
    public sealed class OrderBlockZone
    {
        public double          Top            { get; init; }
        public double          Bottom         { get; init; }
        public SignalDirection  Direction      { get; init; }
        public DateTime        FormedTime     { get; init; }
        public bool            Tested         { get; set; }
        public int             TestCount      { get; set; }
        public bool            Valid          { get; set; } = true;
        /// <summary>True if OB was invalidated and flipped polarity (breaker block).</summary>
        public bool            IsBreakerBlock { get; set; }
        public int             BarsAgo        { get; set; }

        public double Midpoint => (Top + Bottom) / 2.0;
    }
}
