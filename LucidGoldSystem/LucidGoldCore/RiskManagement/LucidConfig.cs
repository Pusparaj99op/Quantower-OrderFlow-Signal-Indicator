// LucidConfig.cs  |  LucidGold.Core.RiskManagement
using System;
using System.IO;
using Newtonsoft.Json;

namespace LucidGold.Core.RiskManagement
{
    /// <summary>
    /// Lucid prop firm rule configuration loaded from lucid_rules.json.
    /// ⚠️ VERIFY ALL VALUES against current Lucid Trading documentation before deployment.
    /// </summary>
    public sealed class LucidConfig
    {
        [JsonProperty("account_size")]
        public decimal AccountSize { get; set; } = 25000m;

        [JsonProperty("profit_target")]
        public decimal ProfitTarget { get; set; } = 1500m;

        [JsonProperty("trailing_drawdown_limit")]
        public decimal TrailingDrawdownLimit { get; set; } = 1500m;

        [JsonProperty("daily_loss_limit")]
        public decimal DailyLossLimit { get; set; } = 0m;

        [JsonProperty("max_contracts_mgc")]
        public int MaxContractsMGC { get; set; } = 5;

        [JsonProperty("max_contracts_gc")]
        public int MaxContractsGC { get; set; } = 1;

        [JsonProperty("consistency_rule_pct")]
        public double ConsistencyRulePct { get; set; } = 30.0;

        [JsonProperty("news_blackout_minutes")]
        public int NewsBlackoutMinutes { get; set; } = 10;

        [JsonProperty("max_position_hold_minutes")]
        public int MaxPositionHoldMinutes { get; set; } = 240;

        [JsonProperty("weekend_flat_required")]
        public bool WeekendFlatRequired { get; set; } = true;

        [JsonProperty("evaluation_mode")]
        public bool EvaluationMode { get; set; } = true;

        /// <summary>Loads config from a JSON file. Throws on failure (never silent).</summary>
        public static LucidConfig LoadFromFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Lucid rules config not found: {path}");

            string json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<LucidConfig>(json)
                   ?? throw new InvalidOperationException("Failed to deserialize lucid_rules.json");
        }

        /// <summary>Hot-reloads config from the same path.</summary>
        public static LucidConfig Reload(string path) => LoadFromFile(path);
    }
}
