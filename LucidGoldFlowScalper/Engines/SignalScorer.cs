using System;
using LucidGoldFlowScalper.Models;

namespace LucidGoldFlowScalper.Engines
{
    /// <summary>
    /// Weights and scores trading setups. Acts as the final gate before RME.
    /// Implements HARD GATES for max stop, min TP, RR, and HTF bias alignment.
    /// </summary>
    public class SignalScorer
    {
        private const double MIN_SCORE_THRESHOLD = 65.0;
        private const double MAX_STOP_TICKS = 50.0;
        private const double MIN_TARGET_TICKS = 100.0;
        private const double MIN_RR_RATIO = 2.0;

        /// <summary>
        /// Computes a weighted score for a given signal context.
        /// </summary>
        public float ComputeScore(SignalContext ctx)
        {
            float score = 0;

            // 1. HTF Market Bias (Weight 25)
            if (ctx.IsLong)
            {
                if (ctx.ConsolidatedBias == MarketBiasEnum.StrongBullish) score += 25;
                else if (ctx.ConsolidatedBias == MarketBiasEnum.Bullish) score += 15;
                else if (ctx.ConsolidatedBias == MarketBiasEnum.Bearish || ctx.ConsolidatedBias == MarketBiasEnum.StrongBearish) score -= 30;
            }
            else
            {
                if (ctx.ConsolidatedBias == MarketBiasEnum.StrongBearish) score += 25;
                else if (ctx.ConsolidatedBias == MarketBiasEnum.Bearish) score += 15;
                else if (ctx.ConsolidatedBias == MarketBiasEnum.Bullish || ctx.ConsolidatedBias == MarketBiasEnum.StrongBullish) score -= 30;
            }

            // 2. Kill Zone Active (Weight 10)
            if (ctx.IsKillZoneActive)
            {
                if (ctx.CurrentKillZone == "ComexOpen") score += 12;
                else if (ctx.CurrentKillZone == "NY") score += 10;
                else if (ctx.CurrentKillZone.Contains("SilverBullet")) score += 9;
                else if (ctx.CurrentKillZone == "London") score += 8;
                else if (ctx.CurrentKillZone == "Afternoon") score += 5;
            }

            // 3. Liquidity Sweep Quality (Weight 15)
            if (ctx.HasLiquiditySweep)
            {
                if (ctx.SweepDepthTicks > 10) score += 15;
                else if (ctx.SweepDepthTicks >= 5) score += 10;
                else score += 5;
            }

            // 4. FVG Quality (Weight 15)
            if (ctx.FvgAgeBars > 0)
            {
                if (ctx.FvgAgeBars <= 5) score += 15;
                else if (ctx.FvgAgeBars <= 15) score += 10;
                else score += 5;
            }

            // 5. Order Block Quality (Weight 10)
            if (ctx.OrderBlockAgeBars > 0)
            {
                if (ctx.OrderBlockAgeBars <= 3) score += 10;
                else if (ctx.OrderBlockAgeBars <= 10) score += 6;
                else score += 3;
            }

            // 6. Order Flow Confirmation (Weight 15)
            if (ctx.HasDeltaFlip && ctx.HasStackedImbalance && ctx.HasTapeAlignment) score += 15;
            else if (ctx.HasDeltaFlip && ctx.HasStackedImbalance) score += 11;
            else if (ctx.HasDeltaFlip) score += 7;
            else if (ctx.HasTapeAlignment) score += 5;

            // 7. DOM Absorption Confirmation (Weight 5)
            if (ctx.HasDomAbsorption) score += 5;

            // 8. Volume Profile Context (Weight 5)
            if (ctx.IsAtValueAreaEdge) score += 5;
            else if (ctx.IsCrossingLvn) score += 4;
            else if (ctx.IsAtPoc) score += 2;

            return Math.Max(0, score);
        }

        public string GetRejectionReason(SignalContext ctx)
        {
            if (ctx.IsNoTradeZone) return "In No-Trade Zone";
            
            if (ctx.StopLossTicks > MAX_STOP_TICKS) 
                return $"Stop Loss too wide ({ctx.StopLossTicks} ticks > {MAX_STOP_TICKS})";
                
            if (ctx.TakeProfitTicks < MIN_TARGET_TICKS) 
                return $"Target too small ({ctx.TakeProfitTicks} ticks < {MIN_TARGET_TICKS})";
                
            double rr = ctx.TakeProfitTicks / Math.Max(1, ctx.StopLossTicks);
            if (rr < MIN_RR_RATIO)
                return $"R:R ratio too low ({rr:F1} < {MIN_RR_RATIO})";

            if (ctx.IsLong && (ctx.ConsolidatedBias == MarketBiasEnum.StrongBearish || ctx.ConsolidatedBias == MarketBiasEnum.Bearish))
                return "Opposes bearish HTF bias";
                
            if (!ctx.IsLong && (ctx.ConsolidatedBias == MarketBiasEnum.StrongBullish || ctx.ConsolidatedBias == MarketBiasEnum.Bullish))
                return "Opposes bullish HTF bias";

            float score = ComputeScore(ctx);
            if (score < MIN_SCORE_THRESHOLD)
                return $"Score too low ({score:F1} < {MIN_SCORE_THRESHOLD})";

            return string.Empty; // No rejection
        }

        public bool IsValidSetup(SignalContext ctx)
        {
            return string.IsNullOrEmpty(GetRejectionReason(ctx));
        }
    }
}
