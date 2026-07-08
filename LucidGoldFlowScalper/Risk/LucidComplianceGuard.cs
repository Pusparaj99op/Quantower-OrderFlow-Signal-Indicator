using System;
using LucidGoldFlowScalper.Models;
using TradingPlatform.BusinessLayer;

namespace LucidGoldFlowScalper.Risk
{
    /// <summary>
    /// Monitors Lucid evaluation rules in real-time.
    /// Liquidates and suspends if limits are breached.
    /// </summary>
    public class LucidComplianceGuard
    {
        private readonly LucidRuleSet _rules;
        private readonly string _instrumentMode;
        
        public double PeakEquity { get; private set; }
        public double TrailingDDFloor { get; private set; }
        public double DailyPnL { get; private set; }
        private bool _isStoppedOut;
        
        public bool CanTrade { get; private set; } = true;
        public bool CanOpenNewPosition { get; private set; } = true;
        public int MaxContractsAllowed { get; private set; }

        public LucidComplianceGuard(LucidRuleSet rules, string instrumentMode)
        {
            _rules = rules;
            _instrumentMode = instrumentMode;
            MaxContractsAllowed = instrumentMode == "MGC" ? _rules.MaxContracts_MGC : _rules.MaxContracts_GC;
            
            // Assume initial peak equity is account size until updated
            PeakEquity = _rules.AccountSize;
            UpdateTrailingFloor();
        }

        public void UpdateEquity(double currentEquity)
        {
            if (currentEquity > PeakEquity)
            {
                PeakEquity = currentEquity;
                UpdateTrailingFloor();
            }

            double buffer = currentEquity - TrailingDDFloor;

            if (buffer <= 0)
            {
                CanTrade = false;
                CanOpenNewPosition = false;
                Core.Instance.Loggers.Log($"[COMPLIANCE] Trailing drawdown breached! Equity: {currentEquity}, Floor: {TrailingDDFloor}", LoggingLevel.Error);
                return;
            }

            if (buffer <= _rules.EmergencyBufferDollars)
            {
                if (_isStoppedOut)
                {
                    Core.Instance.Loggers.Log("[RISK] System is stopped out for the day.", LoggingLevel.System);
                }
                CanOpenNewPosition = false;
                Core.Instance.Loggers.Log($"[COMPLIANCE] Emergency buffer breached. No new trades allowed. Buffer: {buffer}", LoggingLevel.System);
            }
            else if (buffer <= _rules.CriticalBufferDollars)
            {
                CanOpenNewPosition = true;
                // Suggest 50% size reduction, but handled in Position Sizing
            }
            else
            {
                CanOpenNewPosition = true;
            }
        }

        public void UpdateDailyPnL(double pnl)
        {
            DailyPnL = pnl;

            // Check Hard Daily Loss Limit
            if (_rules.DailyLossLimit > 0 && DailyPnL <= -_rules.DailyLossLimit)
            {
                _isStoppedOut = true;
                CanTrade = false;
                CanOpenNewPosition = false;
                Core.Instance.Loggers.Log($"[COMPLIANCE] Daily loss limit breached! PnL: {DailyPnL}, Limit: -{_rules.DailyLossLimit}", LoggingLevel.Error);
            }

            // Check Consistency Profit Limit
            double consistencyCap = _rules.ProfitTarget * _rules.ConsistencyMaxDayPct;
            if (DailyPnL >= consistencyCap)
            {
                CanOpenNewPosition = false;
                Core.Instance.Loggers.Log($"[COMPLIANCE] Consistency cap reached. No new trades today. PnL: {DailyPnL}, Cap: {consistencyCap}", LoggingLevel.System);
            }
        }

        public bool CheckNewsWindow(DateTime currentUtc, string nextNewsEventUtc, int flattenMins, int resumeMins)
        {
            if (string.IsNullOrEmpty(nextNewsEventUtc)) return false;
            
            if (DateTime.TryParse(nextNewsEventUtc, out DateTime newsTime))
            {
                TimeSpan diff = newsTime - currentUtc;
                if (diff.TotalMinutes <= flattenMins && diff.TotalMinutes >= -resumeMins)
                {
                    CanOpenNewPosition = false;
                    return true;
                }
            }
            return false;
        }

        private void UpdateTrailingFloor()
        {
            // Prop firms trail intraday high water mark
            TrailingDDFloor = PeakEquity - _rules.TrailingDrawdownLimit;
            // Usually the trailing floor stops at initial balance, check specific prop firm rule
            if (TrailingDDFloor > _rules.AccountSize)
            {
                TrailingDDFloor = _rules.AccountSize;
            }
        }
    }
}
