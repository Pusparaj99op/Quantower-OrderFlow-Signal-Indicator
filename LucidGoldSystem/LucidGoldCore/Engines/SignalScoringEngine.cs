// ============================================================
// SignalScoringEngine.cs  |  LucidGold.Core.Engines
// ============================================================
using System;
using LucidGold.Core.Enums;
using LucidGold.Core.Models;

namespace LucidGold.Core.Engines
{
    /// <summary>
    /// Aggregates all signal engine outputs into a 0–100 signal score.
    /// A score ≥ 65 qualifies for trade entry consideration.
    /// A score ≥ 80 may justify scaling to max contracts (subject to compliance).
    ///
    /// <para>Weight Distribution (sums to exactly 100):</para>
    /// <list type="table">
    ///   <item><term>HTF Bias</term><description>25 pts (strongest confirming factor)</description></item>
    ///   <item><term>Kill Zone</term><description>15 pts (NY SB = 15, NY = 10, London = 7)</description></item>
    ///   <item><term>Market Structure (MSS/BOS)</term><description>15 pts</description></item>
    ///   <item><term>FVG quality</term><description>10 pts</description></item>
    ///   <item><term>Order Block quality</term><description>10 pts</description></item>
    ///   <item><term>Footprint confirmation</term><description>10 pts</description></item>
    ///   <item><term>DOM confirmation</term><description>8 pts</description></item>
    ///   <item><term>Cumulative delta alignment</term><description>5 pts</description></item>
    ///   <item><term>Tape reading</term><description>2 pts</description></item>
    /// </list>
    ///
    /// <para>Auto-disqualifiers (return -1): HTF bias directly conflicts with proposed direction.</para>
    /// </summary>
    public sealed class SignalScoringEngine
    {
        // ── HTF Bias (25 pts) ─────────────────────────────────────────
        private const float ScoreHTFStrong   = 25f;   // StrongBull for long, StrongBear for short
        private const float ScoreHTFPartial  = 15f;   // Bullish for long, Bearish for short
        // 0 for Neutral; -1 auto-disqualify for opposite bias

        // ── Kill Zone (15 pts) ────────────────────────────────────────
        private const float ScoreKZNYSilver  = 15f;   // NY Kill Zone + Silver Bullet sub-window
        private const float ScoreKZNY        = 10f;   // NY Kill Zone regular
        private const float ScoreKZLondon    = 7f;    // London Kill Zone
        private const float ScoreKZOther     = 3f;    // London Close / Afternoon
        // 0 for no kill zone

        // ── Market Structure (15 pts) ─────────────────────────────────
        private const float ScoreMSS         = 15f;
        private const float ScoreBOS         = 8f;
        private const float ScoreCHoCH       = 4f;

        // ── FVG (10 pts) ──────────────────────────────────────────────
        private const float ScoreFVGLarge    = 10f;   // large + fresh + HTF-aligned
        private const float ScoreFVGMedium   = 6f;    // medium
        private const float ScoreFVGSmall    = 3f;    // small or old

        // ── Order Block (10 pts) ──────────────────────────────────────
        private const float ScoreOBFresh     = 10f;   // fresh OB (formed < 5 bars ago, untested)
        private const float ScoreOBTested    = 7f;    // tested once and held
        private const float ScoreOBOld       = 3f;    // > 20 bars old

        // ── Footprint (10 pts) ────────────────────────────────────────
        private const float ScoreFpStackAbsorb = 10f; // stacked imbalance + absorption
        private const float ScoreFpStack       = 6f;  // stacked imbalance only
        private const float ScoreFpAbsorb      = 5f;  // absorption only
        private const float ScoreFpDeltaFlip   = 3f;  // delta flip only

        // ── DOM (8 pts) ───────────────────────────────────────────────
        private const float ScoreDOMBoth      = 8f;   // absorption + stacking
        private const float ScoreDOMAbsorb    = 5f;   // absorption only
        private const float ScoreDOMImbalance = 3f;   // imbalance only

        // ── Delta (5 pts) ─────────────────────────────────────────────
        private const float ScoreDeltaAligned = 5f;   // slope aligned, no divergence
        private const float ScoreDeltaPartial = 2f;   // aligned but no slope trend

        // ── Tape (2 pts) ──────────────────────────────────────────────
        private const float ScoreTapeBurst    = 2f;
        private const float ScoreTapeAccel    = 1f;

        // ─────────────────────────────────────────────────────────────
        // Scoring
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the final signal score (0–100) from a fully populated <see cref="SetupContext"/>.
        /// Returns -1 if the setup is auto-disqualified (HTF bias conflict).
        /// </summary>
        public float ScoreSetup(SetupContext ctx, SignalDirection direction)
        {
            if (direction == SignalDirection.Flat) return 0f;

            float score = 0f;

            // ── 1. HTF Bias (25 pts) ─────────────────────────────────
            float htfScore = ScoreHTFBias(ctx.HTFBias, direction);
            if (htfScore < 0) return -1f;  // auto-disqualify
            score += htfScore;

            // ── 2. Kill Zone (15 pts) ────────────────────────────────
            score += ScoreKillZone(ctx.ActiveKillZone, ctx.IsSilverBullet);

            // ── 3. Market Structure (15 pts) ─────────────────────────
            score += ScoreStructure(ctx.LatestStructureEvent, direction);

            // ── 4. FVG quality (10 pts) ──────────────────────────────
            score += ScoreFVG(ctx.NearestFVG, ctx.ATR20);

            // ── 5. Order Block quality (10 pts) ──────────────────────
            score += ScoreOrderBlock(ctx.NearestOrderBlock);

            // ── 6. Footprint confirmation (10 pts) ───────────────────
            score += ScoreFootprint(ctx.HasStackedImbalance, ctx.HasAbsorption, ctx.HasDeltaFlip);

            // ── 7. DOM confirmation (8 pts) ──────────────────────────
            score += ScoreDOM(ctx.DOMAbsorptionActive, ctx.DOMStackingActive, ctx.DOMImbalance);

            // ── 8. Delta alignment (5 pts) ───────────────────────────
            score += ScoreDelta(ctx.DeltaSlope, ctx.IsDeltaDivergent, direction);

            // ── 9. Tape (2 pts) ──────────────────────────────────────
            if (ctx.IsLargeBlockBurst)  score += ScoreTapeBurst;
            else if (ctx.IsTapeAccelerating) score += ScoreTapeAccel;

            // Clamp to [0, 100]
            score = Math.Max(0f, Math.Min(100f, score));
            return (float)Math.Round(score, 1);
        }

        // ─────────────────────────────────────────────────────────────
        // Component scorers
        // ─────────────────────────────────────────────────────────────

        /// <summary>Returns HTF bias score, or -1 if bias directly conflicts with direction.</summary>
        private static float ScoreHTFBias(MarketBias bias, SignalDirection direction)
        {
            bool isLong = direction == SignalDirection.Long;
            return bias switch
            {
                MarketBias.StrongBullish => isLong  ? ScoreHTFStrong  : -1f,
                MarketBias.Bullish       => isLong  ? ScoreHTFPartial :  0f,
                MarketBias.StrongBearish => !isLong ? ScoreHTFStrong  : -1f,
                MarketBias.Bearish       => !isLong ? ScoreHTFPartial :  0f,
                _                        => 0f   // Neutral
            };
        }

        private static float ScoreKillZone(string kzName, bool isSilverBullet)
        {
            if (kzName == "None" || string.IsNullOrEmpty(kzName)) return 0f;

            bool isNY     = kzName.Contains("NY Kill Zone");
            bool isSB     = isSilverBullet || kzName.StartsWith("Silver Bullet");
            bool isLondon = kzName.Contains("London Kill Zone");

            if (isNY && isSB)  return ScoreKZNYSilver;
            if (isNY)          return ScoreKZNY;
            if (isSB)          return ScoreKZNY;           // Silver Bullet in London = NY-equivalent
            if (isLondon)      return ScoreKZLondon;
            return ScoreKZOther;
        }

        private static float ScoreStructure(StructurePoint? ms, SignalDirection direction)
        {
            if (ms == null) return 0f;
            if (ms.Direction != direction) return 0f;

            return ms.EventType switch
            {
                StructureEventType.MSS   => ScoreMSS,
                StructureEventType.BOS   => ScoreBOS,
                StructureEventType.CHoCH => ScoreCHoCH,
                _                        => 0f
            };
        }

        private static float ScoreFVG(FairValueGap? fvg, double atr20)
        {
            if (fvg == null || !fvg.IsActive) return 0f;

            double gapTicks = (fvg.Top - fvg.Bottom) / 0.1;  // normalized, actual tickSize from caller context
            double large = atr20 > 0 ? atr20 * 2 : 10;

            if (gapTicks >= large)  return ScoreFVGLarge;
            if (gapTicks >= large / 2) return ScoreFVGMedium;
            return ScoreFVGSmall;
        }

        private static float ScoreOrderBlock(OrderBlockZone? ob)
        {
            if (ob == null || !ob.Valid) return 0f;
            if (ob.BarsAgo <= 5 && !ob.Tested) return ScoreOBFresh;
            if (ob.Tested && ob.TestCount == 1) return ScoreOBTested;
            if (ob.BarsAgo > 20) return ScoreOBOld;
            return ScoreOBTested;  // tested and valid, mid-age
        }

        private static float ScoreFootprint(bool stackedImbalance, bool absorption, bool deltaFlip)
        {
            if (stackedImbalance && absorption) return ScoreFpStackAbsorb;
            if (stackedImbalance)               return ScoreFpStack;
            if (absorption)                     return ScoreFpAbsorb;
            if (deltaFlip)                      return ScoreFpDeltaFlip;
            return 0f;
        }

        private static float ScoreDOM(bool domAbsorption, bool domStacking,
                                      DOMImbalanceSignal imbalance)
        {
            if (domAbsorption && domStacking)                    return ScoreDOMBoth;
            if (domAbsorption)                                   return ScoreDOMAbsorb;
            if (imbalance != DOMImbalanceSignal.Balanced)        return ScoreDOMImbalance;
            return 0f;
        }

        private static float ScoreDelta(DeltaTrend slope, bool isDivergent, SignalDirection direction)
        {
            if (isDivergent) return 0f;  // delta divergence = no contribution

            bool aligned = direction == SignalDirection.Long
                ? slope == DeltaTrend.Rising
                : slope == DeltaTrend.Falling;

            if (aligned)             return ScoreDeltaAligned;
            if (slope == DeltaTrend.Flat) return ScoreDeltaPartial;
            return 0f;
        }
    }
}
