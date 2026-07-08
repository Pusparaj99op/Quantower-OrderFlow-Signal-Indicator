using System.Text.Json.Serialization;

namespace LucidGoldFlowScalper.Models
{
    /// <summary>
    /// Represents the strict rules and parameters for a Lucid Trading evaluation account.
    /// Loaded dynamically from JSON at startup.
    /// </summary>
    public class LucidRuleSet
    {
        [JsonPropertyName("account_size")]
        public double AccountSize { get; set; }

        [JsonPropertyName("profit_target")]
        public double ProfitTarget { get; set; }

        [JsonPropertyName("trailing_drawdown_limit")]
        public double TrailingDrawdownLimit { get; set; }

        [JsonPropertyName("daily_loss_limit")]
        public double DailyLossLimit { get; set; }

        [JsonPropertyName("max_contracts_mgc")]
        public int MaxContracts_MGC { get; set; }

        [JsonPropertyName("max_contracts_gc")]
        public int MaxContracts_GC { get; set; }

        [JsonPropertyName("consistency_max_day_pct")]
        public double ConsistencyMaxDayPct { get; set; }

        [JsonPropertyName("news_flatten_minutes_before")]
        public int NewsFlattenMinutesBefore { get; set; }

        [JsonPropertyName("news_resume_minutes_after")]
        public int NewsResumeMinutesAfter { get; set; }

        [JsonPropertyName("weekend_positions_allowed")]
        public bool WeekendPositionsAllowed { get; set; }

        [JsonPropertyName("emergency_buffer_dollars")]
        public double EmergencyBufferDollars { get; set; }

        [JsonPropertyName("critical_buffer_dollars")]
        public double CriticalBufferDollars { get; set; }
    }
}
