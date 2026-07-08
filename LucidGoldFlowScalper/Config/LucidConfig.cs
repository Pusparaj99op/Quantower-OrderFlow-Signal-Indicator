using System;
using System.IO;
using System.Text.Json;
using TradingPlatform.BusinessLayer;

namespace LucidGoldFlowScalper.Config
{
    /// <summary>
    /// Handles loading and parsing the Lucid Flex rules from JSON.
    /// </summary>
    public static class LucidConfig
    {
        /// <summary>
        /// Loads the LucidRuleSet from the specified path.
        /// </summary>
        /// <param name="filePath">Absolute path to lucid_rules.json</param>
        /// <returns>Deserialized LucidRuleSet or a default fallback if missing/invalid.</returns>
        public static Models.LucidRuleSet Load(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Core.Instance.Loggers.Log($"[RISK] Lucid config not found at {filePath}, using defaults.", LoggingLevel.Error);
                    return GetDefaultRules();
                }

                string json = File.ReadAllText(filePath);
                var rules = JsonSerializer.Deserialize<Models.LucidRuleSet>(json);

                if (rules == null)
                    throw new Exception("Deserialized rules resulted in null.");

                Core.Instance.Loggers.Log($"[RISK] LucidConfig loaded: ProfitTarget={rules.ProfitTarget}, TrailingDD={rules.TrailingDrawdownLimit}, MaxMGC={rules.MaxContracts_MGC}", LoggingLevel.System);
                return rules;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[RISK] Failed to load Lucid config from {filePath}. {ex.Message}", LoggingLevel.Error);
                return GetDefaultRules();
            }
        }

        private static Models.LucidRuleSet GetDefaultRules()
        {
            return new Models.LucidRuleSet
            {
                AccountSize = 25000,
                ProfitTarget = 1500,
                TrailingDrawdownLimit = 1500,
                DailyLossLimit = 0, // 0 means no hard daily limit in some flex rules
                MaxContracts_MGC = 10,
                MaxContracts_GC = 1,
                ConsistencyMaxDayPct = 0.30,
                NewsFlattenMinutesBefore = 2,
                NewsResumeMinutesAfter = 2,
                WeekendPositionsAllowed = false,
                EmergencyBufferDollars = 250,
                CriticalBufferDollars = 500
            };
        }
    }
}
