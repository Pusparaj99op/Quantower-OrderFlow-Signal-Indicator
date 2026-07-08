// LucidComplianceGuard.cs  |  LucidGold.Core.RiskManagement
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LucidGold.Core.Models;

namespace LucidGold.Core.RiskManagement
{
    /// <summary>Single economic news event loaded from CSV.</summary>
    public sealed record NewsEvent(DateTime UtcTime, string Impact, string Description);

    /// <summary>
    /// Enforces all Lucid Trading prop firm compliance rules.
    /// All thresholds are loaded from LucidConfig (JSON — no hardcoding).
    /// </summary>
    public sealed class LucidComplianceGuard
    {
        private static readonly TimeZoneInfo ET =
            TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        private readonly LucidConfig _config;
        private readonly Action<string, string> _log;  // (message, "Info"|"Error")

        // ── Trailing drawdown ─────────────────────────────────────────
        private decimal _highWaterMark;
        private decimal _currentEquity;
        private bool    _emergencyHalt;

        // ── Daily loss ────────────────────────────────────────────────
        private decimal  _todayPnL;
        private decimal  _totalCumPnL;
        private bool     _dailyHalt;
        private DateTime _lastSessionReset;

        // ── News ──────────────────────────────────────────────────────
        private List<NewsEvent> _news = new List<NewsEvent>();

        // ── Expiration ────────────────────────────────────────────────
        private DateTime _contractExpiry;
        private bool     _expirationHalt;

        // ── Public state ──────────────────────────────────────────────
        public bool    IsEmergencyHalt => _emergencyHalt;
        public bool    IsDailyHalt     => _dailyHalt;
        public bool    IsExpirationHalt=> _expirationHalt;
        public decimal DrawdownFloor   => _highWaterMark - _config.TrailingDrawdownLimit;
        public decimal CurrentBuffer   => _currentEquity - DrawdownFloor;
        public decimal BufferPct       => _config.TrailingDrawdownLimit > 0
                                         ? CurrentBuffer / _config.TrailingDrawdownLimit : 1m;

        public LucidComplianceGuard(LucidConfig config, decimal initialEquity,
                                    Action<string, string> logAction)
        {
            _config           = config;
            _highWaterMark    = initialEquity;
            _currentEquity    = initialEquity;
            _log              = logAction;
            _lastSessionReset = DateTime.UtcNow;
        }

        // ── Equity update ─────────────────────────────────────────────

        public void UpdateEquity(decimal equity, decimal realizedPnL, decimal unrealizedPnL)
        {
            _currentEquity = equity;
            if (equity > _highWaterMark) _highWaterMark = equity;

            decimal buffer       = CurrentBuffer;
            decimal safetyBuffer = _config.TrailingDrawdownLimit * 0.20m;

            if (equity <= DrawdownFloor)
            {
                _emergencyHalt = true;
                _log($"[COMPLIANCE] Guard=TrailingDrawdown | Equity={equity:F2} | Floor={DrawdownFloor:F2} | Action=EMERGENCY_HALT", "Error");
            }
            else if (buffer <= safetyBuffer)
            {
                _log($"[COMPLIANCE] Guard=DrawdownBuffer20pct | Buffer={buffer:F2} | Action=WARN", "Error");
            }

            CheckDailyReset();
            _todayPnL = realizedPnL + unrealizedPnL;

            if (_config.DailyLossLimit > 0 && _todayPnL <= -_config.DailyLossLimit && !_dailyHalt)
            {
                _dailyHalt = true;
                _log($"[COMPLIANCE] Guard=DailyLoss | PnL={_todayPnL:F2} | Limit=-{_config.DailyLossLimit:F2} | Action=HALT", "Error");
            }
        }

        private void CheckDailyReset()
        {
            var nowEt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ET);
            var lastEt = TimeZoneInfo.ConvertTimeFromUtc(_lastSessionReset, ET);
            if (nowEt.Date > lastEt.Date || (nowEt.Hour >= 17 && lastEt.Hour < 17))
            {
                _dailyHalt         = false;
                _totalCumPnL      += _todayPnL;
                _todayPnL          = 0m;
                _lastSessionReset  = DateTime.UtcNow;
            }
        }

        // ── Contract limit ────────────────────────────────────────────

        public bool IsContractLimitExceeded(string symbol, int quantity)
        {
            bool isMGC = symbol.Contains("MGC", StringComparison.OrdinalIgnoreCase);
            int  limit = isMGC ? _config.MaxContractsMGC : _config.MaxContractsGC;
            if (quantity > limit)
            {
                _log($"[COMPLIANCE] Guard=ContractLimit | Symbol={symbol} | Qty={quantity} | Limit={limit} | Action=BLOCK", "Error");
                return true;
            }
            return false;
        }

        // ── Consistency ───────────────────────────────────────────────

        public bool IsConsistencyCapReached()
        {
            decimal cap = _config.ProfitTarget * (decimal)(_config.ConsistencyRulePct / 100.0);
            if (_todayPnL >= cap)
            {
                _log($"[COMPLIANCE] Guard=Consistency | TodayPnL={_todayPnL:F2} | Cap={cap:F2} | Action=HALT", "Error");
                return true;
            }
            return false;
        }

        // ── News ──────────────────────────────────────────────────────

        public void LoadNewsEvents(string csvPath)
        {
            _news.Clear();
            if (!File.Exists(csvPath)) return;
            foreach (string line in File.ReadAllLines(csvPath).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                if (DateTime.TryParse(parts[0].Trim(), out DateTime dt))
                    _news.Add(new NewsEvent(dt.ToUniversalTime(), parts[1].Trim(), parts[2].Trim()));
            }
        }

        public bool IsNewsBlackout(DateTime utcNow)
        {
            var blackout = TimeSpan.FromMinutes(_config.NewsBlackoutMinutes);
            foreach (var ev in _news)
            {
                if (!ev.Impact.Equals("High", StringComparison.OrdinalIgnoreCase)) continue;
                var diff = utcNow - ev.UtcTime;
                if (diff >= -blackout && diff <= blackout)
                {
                    _log($"[COMPLIANCE] Guard=News | Event='{ev.Description}' | Action=BLOCK", "Info");
                    return true;
                }
            }
            return false;
        }

        public bool ShouldFlattenForNews(DateTime utcNow)
        {
            var flatWindow = TimeSpan.FromMinutes(2);
            foreach (var ev in _news)
            {
                if (!ev.Impact.Equals("High", StringComparison.OrdinalIgnoreCase)) continue;
                var diff = ev.UtcTime - utcNow;
                if (diff >= TimeSpan.Zero && diff <= flatWindow)
                {
                    _log($"[COMPLIANCE] Guard=NewsFlatten | Event='{ev.Description}' | In={diff.TotalMinutes:F1}min | Action=FLATTEN", "Error");
                    return true;
                }
            }
            return false;
        }

        // ── Weekend flat ──────────────────────────────────────────────

        public bool ShouldFlattenForWeekend(DateTime utcNow)
        {
            if (!_config.WeekendFlatRequired) return false;
            var etNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, ET);
            if (etNow.DayOfWeek == DayOfWeek.Friday && etNow.Hour >= 16 && etNow.Minute >= 45)
            {
                _log($"[COMPLIANCE] Guard=WeekendFlat | Time={etNow:HH:mm} ET | Action=FLATTEN", "Info");
                return true;
            }
            return false;
        }

        // ── Expiration ────────────────────────────────────────────────

        public void SetContractExpiration(DateTime expirationDate)
        {
            _contractExpiry = expirationDate;
            _expirationHalt = false;
        }

        public bool CheckExpiration(DateTime utcNow)
        {
            if (_contractExpiry == default) return false;
            double days = (_contractExpiry.Date - utcNow.Date).TotalDays;
            if (days <= 2) { _expirationHalt = true; _log($"[COMPLIANCE] Guard=Expiration | Days={days:F0} | Action=HALT", "Error"); return true; }
            if (days <= 5) { _log($"[COMPLIANCE] Guard=Expiration | Days={days:F0} | Action=WARN", "Info"); }
            return false;
        }

        // ── Master pre-entry check ────────────────────────────────────

        /// <summary>Runs all guards. Returns true if entry is allowed.</summary>
        public bool PreEntryCheck(TradeSignal trade, DateTime utcNow)
        {
            if (_emergencyHalt)         { _log("[COMPLIANCE] BLOCKED | Reason=EmergencyHalt", "Error");         return false; }
            if (_dailyHalt)             { _log("[COMPLIANCE] BLOCKED | Reason=DailyLossHalt", "Error");         return false; }
            if (_expirationHalt)        { _log("[COMPLIANCE] BLOCKED | Reason=ExpirationHalt", "Error");        return false; }
            if (CurrentBuffer <= _config.TrailingDrawdownLimit * 0.20m)
                                        { _log($"[COMPLIANCE] BLOCKED | Reason=LowDDBuffer | Buffer={CurrentBuffer:F2}", "Error"); return false; }
            if (IsContractLimitExceeded(trade.Symbol, trade.Quantity)) return false;
            if (IsConsistencyCapReached())                             return false;
            if (IsNewsBlackout(utcNow))                                return false;
            if (ShouldFlattenForWeekend(utcNow))                       return false;
            return true;
        }

        /// <summary>Status string for HUD display.</summary>
        public string GetStatusSummary()
            => $"Equity={_currentEquity:F0} | Floor={DrawdownFloor:F0} | Buffer={CurrentBuffer:F0} | " +
               $"TodayPnL={_todayPnL:F0} | Halt={_emergencyHalt || _dailyHalt}";
    }
}
