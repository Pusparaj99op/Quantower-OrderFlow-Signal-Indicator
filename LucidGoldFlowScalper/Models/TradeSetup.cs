namespace LucidGoldFlowScalper.Models
{
    /// <summary>
    /// Represents a validated, fully formed trade setup that has passed scoring and risk filters.
    /// </summary>
    public class TradeSetup
    {
        public string SetupId { get; set; } = string.Empty;
        public bool IsLong { get; set; }
        public double EntryPrice { get; set; }
        public double StopLossPrice { get; set; }
        public double TakeProfit1Price { get; set; }
        public double TakeProfit2Price { get; set; }
        public double StopTicks { get; set; }
        public double Tp1Ticks { get; set; }
        public double Tp2Ticks { get; set; }
        public float Score { get; set; }
        public string SourceZoneType { get; set; } = string.Empty; // e.g. "FVG", "OB"
    }
}
