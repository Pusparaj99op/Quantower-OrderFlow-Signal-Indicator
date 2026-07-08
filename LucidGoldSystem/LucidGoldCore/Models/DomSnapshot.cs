// DomSnapshot.cs  |  LucidGold.Core.Models
using System;
using System.Collections.Generic;

namespace LucidGold.Core.Models
{
    public enum QuoteType { Bid, Ask }

    public readonly struct DomQuote
    {
        public double Price { get; }
        public long Size { get; }
        public QuoteType Type { get; }

        public DomQuote(double price, long size, QuoteType type)
        {
            Price = price;
            Size = size;
            Type = type;
        }
    }

    /// <summary>Snapshot of DOM state at a point in time.</summary>
    public sealed class DomSnapshot
    {
        public DateTime                         Time           { get; init; }
        public IReadOnlyDictionary<double, long> Bids          { get; init; } = new Dictionary<double, long>();
        public IReadOnlyDictionary<double, long> Asks          { get; init; } = new Dictionary<double, long>();
        public double                           BestBid        { get; init; }
        public double                           BestAsk        { get; init; }
        public long                             TotalBidDepth  { get; init; }
        public long                             TotalAskDepth  { get; init; }

        public double ImbalanceRatio =>
            TotalAskDepth > 0 ? (double)TotalBidDepth / TotalAskDepth : 0;
    }
}
