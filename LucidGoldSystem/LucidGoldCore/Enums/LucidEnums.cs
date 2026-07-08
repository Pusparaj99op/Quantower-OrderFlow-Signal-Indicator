// ============================================================
// LucidGoldCore — Shared Enums
// Namespace : LucidGold.Core.Enums
// ============================================================
#nullable enable

namespace LucidGold.Core.Enums
{
    /// <summary>Higher-timeframe directional bias used by the scoring engine.</summary>
    public enum MarketBias
    {
        StrongBearish = -2,
        Bearish       = -1,
        Neutral       =  0,
        Bullish       =  1,
        StrongBullish =  2
    }

    /// <summary>Direction of a pending or confirmed trade signal.</summary>
    public enum SignalDirection { Long, Short, Flat }

    /// <summary>Strategy state machine states.</summary>
    public enum TradeState
    {
        Idle,
        Scanning,
        SetupIdentified,
        AwaitingConfirmation,
        OrderPending,
        InTrade,
        Managing,
        Flat,
        Halted
    }

    /// <summary>Slope direction of the cumulative-delta series over recent bars.</summary>
    public enum DeltaTrend { Rising, Flat, Falling }

    /// <summary>DOM order book imbalance signal.</summary>
    public enum DOMImbalanceSignal { BidHeavy, AskHeavy, Balanced }

    /// <summary>Tape reading aggregated condition.</summary>
    public enum TapeCondition { Bullish, Bearish, Exhaustion, Neutral }

    /// <summary>Market-structure event type.</summary>
    public enum StructureEventType { BOS, CHoCH, MSS }

    /// <summary>
    /// Aggressor side for trade prints (who initiated the trade).
    /// Mirrors TradingPlatform.BusinessLayer.AggressorFlag but removes SDK dependency from Core.
    /// </summary>
    public enum AggressorSide
    {
        /// <summary>Buyer-initiated trade (hit the ask).</summary>
        Buy  = 0,
        /// <summary>Seller-initiated trade (hit the bid).</summary>
        Sell = 1,
        /// <summary>Aggressor is unknown or cross trade.</summary>
        Unknown = 2
    }

    /// <summary>
    /// Level 2 quote type (bid or ask side of the order book).
    /// Mirrors TradingPlatform.BusinessLayer.Level2Type.
    /// </summary>
    public enum QuoteSide { Bid, Ask }

    /// <summary>Price extreme of a bar for unfinished auction detection.</summary>
    public enum BarExtreme { High, Low }
}
