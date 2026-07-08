// RiskCalculator.cs  |  LucidGold.Core.RiskManagement
using System;
using LucidGold.Core.Enums;
using LucidGold.Core.Models;

namespace LucidGold.Core.RiskManagement
{
    /// <summary>Calculates trade specs (entry, stop, TP1, TP2, RR) for a proposed trade.</summary>
    public sealed class RiskCalculator
    {
        public const int    MaxStopLossTicks      = 50;
        public const int    MinProfitTargetTicks   = 100;
        public const double MinRRRatio             = 2.0;

        private readonly double _tickSize;
        private readonly double _tickValue;
        private readonly string _symbol;

        public RiskCalculator(string symbol, double tickSize, double tickValue)
        {
            _symbol    = symbol;
            _tickSize  = tickSize  > 0 ? tickSize  : 0.1;
            _tickValue = tickValue > 0 ? tickValue : 1.0;
        }

        /// <summary>
        /// Returns a fully-specified TradeSignal, or null if any constraint is violated.
        /// Adds a 2-tick buffer beyond the natural stop level.
        /// </summary>
        public TradeSignal? CalculateTrade(
            double         entryPrice,
            double         naturalStopLevel,
            SignalDirection direction,
            int            tp1Ticks,
            int            tp2Ticks,
            int            quantity,
            float          score,
            SetupContext   context,
            string         reason)
        {
            if (direction == SignalDirection.Flat) return null;

            double sign          = direction == SignalDirection.Long ? 1.0 : -1.0;
            double stopBuffered  = naturalStopLevel - (sign * 2 * _tickSize);
            double stopDist      = Math.Abs(entryPrice - stopBuffered);
            int    stopTicks     = (int)Math.Round(stopDist / _tickSize);

            if (stopTicks > MaxStopLossTicks) return null;
            if (stopTicks <= 0)               return null;

            int    actualTP1Ticks = Math.Max(tp1Ticks, MinProfitTargetTicks);
            int    actualTP2Ticks = Math.Max(tp2Ticks, actualTP1Ticks + 50);
            double rrRatio        = (double)actualTP1Ticks / stopTicks;
            if (rrRatio < MinRRRatio) return null;

            double stopPrice = direction == SignalDirection.Long
                ? entryPrice - (stopTicks * _tickSize)
                : entryPrice + (stopTicks * _tickSize);

            double tp1Price = direction == SignalDirection.Long
                ? entryPrice + (actualTP1Ticks * _tickSize)
                : entryPrice - (actualTP1Ticks * _tickSize);

            double tp2Price = direction == SignalDirection.Long
                ? entryPrice + (actualTP2Ticks * _tickSize)
                : entryPrice - (actualTP2Ticks * _tickSize);

            return new TradeSignal
            {
                Symbol          = _symbol,
                Direction       = direction,
                EntryPrice      = entryPrice,
                StopPrice       = stopPrice,
                TP1Price        = tp1Price,
                TP2Price        = tp2Price,
                Quantity        = quantity,
                Score           = score,
                RRRatio         = Math.Round(rrRatio, 2),
                RiskPerContract = stopTicks * _tickValue,
                SignalTime      = DateTime.UtcNow,
                Reason          = reason,
                Context         = context
            };
        }

        public double GetMaxRiskDollars() => MaxStopLossTicks * _tickValue;

        public bool IsStopValid(double entry, double stop)
            => Math.Abs(entry - stop) / _tickSize <= MaxStopLossTicks;

        public bool IsRRValid(double entry, double stop, double tp1)
        {
            double risk   = Math.Abs(entry - stop);
            double reward = Math.Abs(tp1   - entry);
            return risk > 0 && (reward / risk) >= MinRRRatio;
        }

        public int    ToTicks(double dollars) => (int)Math.Round(dollars / _tickValue);
        public double ToDollars(int ticks)    => ticks * _tickValue;
    }
}
