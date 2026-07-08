using System;

namespace LucidGoldFlowScalper.Risk
{
    /// <summary>
    /// Tracks daily trading metrics and limits.
    /// Resets at 18:00 ET.
    /// </summary>
    public class DailyRiskBudget
    {
        public double AccumulatedDailyPnL { get; private set; }
        public int ConsecutiveLossCount { get; private set; }
        public int TradeCountToday { get; private set; }
        
        private int _maxDailyTrades;

        public DailyRiskBudget(int maxDailyTrades)
        {
            _maxDailyTrades = maxDailyTrades;
            Reset();
        }

        public void Reset()
        {
            AccumulatedDailyPnL = 0;
            ConsecutiveLossCount = 0;
            TradeCountToday = 0;
        }

        public void OnTradeClosed(double pnl)
        {
            AccumulatedDailyPnL += pnl;
            TradeCountToday++;

            if (pnl < 0)
                ConsecutiveLossCount++;
            else
                ConsecutiveLossCount = 0; // Reset on win
        }

        public bool IsSuspended()
        {
            if (ConsecutiveLossCount >= 3) return true;
            if (TradeCountToday >= _maxDailyTrades) return true;
            return false;
        }
    }
}
