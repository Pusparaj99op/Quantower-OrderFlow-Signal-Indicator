namespace LucidGoldFlowScalper.Risk
{
    public enum CircuitBreakerState
    {
        Normal,
        Reduced,       // Trade at 50% size
        Suspended,     // No new trades, but don't liquidate open yet
        FlatAndOff,    // Flat positions, strategy halted until reset
        Emergency      // Critical breach, liquidating at market now
    }

    /// <summary>
    /// Governs the overall operational state of the strategy based on risk events.
    /// Transitions are one-way intraday (can only downgrade until session reset).
    /// </summary>
    public class CircuitBreaker
    {
        public CircuitBreakerState State { get; private set; } = CircuitBreakerState.Normal;

        public void Reset()
        {
            State = CircuitBreakerState.Normal;
        }

        public void DowngradeState(CircuitBreakerState newState)
        {
            // Only allow downgrade to more severe states
            if (newState > State)
            {
                State = newState;
            }
        }
    }
}
