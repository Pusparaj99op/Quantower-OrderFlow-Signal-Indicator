// TradeSignal.cs  |  LucidGold.Core.Models
using System;
using LucidGold.Core.Enums;

namespace LucidGold.Core.Models
{
    /// <summary>A confirmed trade signal ready for order placement.</summary>
    public sealed class TradeSignal
    {
        public string          Symbol          { get; init; } = string.Empty;
        public SignalDirection  Direction       { get; init; }
        public double          EntryPrice      { get; init; }
        public double          StopPrice       { get; init; }
        public double          TP1Price        { get; init; }
        public double          TP2Price        { get; init; }
        public int             Quantity        { get; init; }
        public float           Score           { get; init; }
        public double          RRRatio         { get; init; }
        public double          RiskPerContract { get; init; }
        public DateTime        SignalTime      { get; init; }
        public string          Reason          { get; init; } = string.Empty;
        public SetupContext    Context         { get; init; } = null!;
    }
}
