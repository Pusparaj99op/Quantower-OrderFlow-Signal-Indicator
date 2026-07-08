using System;

namespace LucidGoldFlowScalper.Utils
{
    /// <summary>
    /// Helper methods for converting between price values and tick counts.
    /// Assumes constant tick sizes for Gold futures (MGC/GC).
    /// </summary>
    public static class TickMath
    {
        // MGC & GC have 0.1 tick size
        public const double TICK_SIZE = 0.1;
        
        public static int PriceToTicks(double priceDifference)
        {
            return (int)Math.Round(Math.Abs(priceDifference) / TICK_SIZE);
        }

        public static double TicksToPrice(int ticks)
        {
            return ticks * TICK_SIZE;
        }

        public static double AddTicks(double basePrice, int ticks)
        {
            return basePrice + (ticks * TICK_SIZE);
        }
        
        public static double SubtractTicks(double basePrice, int ticks)
        {
            return basePrice - (ticks * TICK_SIZE);
        }

        public static int GetPriceIndex(double price, double basePrice)
        {
            return (int)Math.Round((price - basePrice) / TICK_SIZE);
        }
        
        public static double IndexToPrice(int index, double basePrice)
        {
            return basePrice + (index * TICK_SIZE);
        }
    }
}
