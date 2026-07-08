// ============================================================
// Bar.cs  |  LucidGold.Core.Models
// Local lightweight Bar struct — used by all Core engines.
// Mirrors the Quantower SDK Bar fields so engines are SDK-agnostic.
// ============================================================
using System;

namespace LucidGold.Core.Models
{
    /// <summary>
    /// Lightweight OHLCV bar struct used throughout LucidGoldCore.
    /// Mirrors the Quantower SDK IHistoryItem interface fields.
    /// Create instances from Indicator/Strategy using the factory below.
    /// </summary>
    public struct Bar
    {
        public DateTime Time   { get; set; }
        public double   Open   { get; set; }
        public double   High   { get; set; }
        public double   Low    { get; set; }
        public double   Close  { get; set; }
        public long     Volume { get; set; }

        public double Body    => Math.Abs(Close - Open);
        public bool   IsBull  => Close >= Open;
        public bool   IsBear  => Close < Open;
        public double Range   => High - Low;

        public override string ToString()
            => $"[{Time:HH:mm}] O={Open:F1} H={High:F1} L={Low:F1} C={Close:F1} V={Volume}";
    }
}
