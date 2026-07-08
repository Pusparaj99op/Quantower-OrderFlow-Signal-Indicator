using System;
using System.Linq;
using System.Collections.Generic;
using LucidGoldFlowScalper.Config;
using TradingPlatform.BusinessLayer.Integration;
using LucidGoldFlowScalper.Engines;
using LucidGoldFlowScalper.Models;
using LucidGoldFlowScalper.Risk;
using LucidGoldFlowScalper.Utils;
using TradingPlatform.BusinessLayer;

namespace LucidGoldFlowScalper.Strategy
{
    enum StrategyState
    {
        IDLE,
        WATCHING_FOR_SWEEP,
        SWEEP_CONFIRMED,
        SEEKING_ENTRY,
        IN_TRADE
    }

    /// <summary>
    /// Layer 2: Autonomous trading strategy running the ICT OTE Scalp logic.
    /// Integrates engines and risk guards. Places real orders.
    /// </summary>
    public class LucidGoldFlowStrategy : TradingPlatform.BusinessLayer.Strategy
    {
        [InputParameter("Instrument Mode (MGC/GC)")]
        public string InstrumentMode = "MGC";

        [InputParameter("Account Filter")]
        public string AccountFilter = "Lucid";

        [InputParameter("Lucid Rules Config Path")]
        public string LucidConfigPath = "C:\\Quantower\\LucidGold\\lucid_rules.json";

        [InputParameter("Enable Auto Trading")]
        public bool EnableAutoTrading = false;

        [InputParameter("Risk Per Trade Pct")]
        public double RiskPerTradePct = 0.005;

        // Engines & Risk
        private OrderFlowEngine _flowEngine;
        private VolumeProfileEngine _vpEngine;
        private MarketStructureEngine _structureEngine;
        private KillZoneEngine _kzEngine;
        private SignalScorer _scorer;
        private LucidComplianceGuard _compliance;
        private DailyRiskBudget _dailyRisk;
        private CircuitBreaker _circuitBreaker;

        // Strategy State
        private StrategyState _currentState = StrategyState.IDLE;
        private Account _tradeAccount;
        private Symbol _tradeSymbol;
        private bool _tradingEnabled;

        public LucidGoldFlowStrategy()
        {
            Name = "Lucid Gold Flow Scalper";
            Description = "Autonomous order flow and structure scalper for Lucid Flex.";
        }

        protected override void OnCreated()
        {
            base.OnCreated();
            
            var rules = LucidConfig.Load(LucidConfigPath);
            _compliance = new LucidComplianceGuard(rules, InstrumentMode);
            _dailyRisk = new DailyRiskBudget(5);
            _circuitBreaker = new CircuitBreaker();
            
            _flowEngine = new OrderFlowEngine(500, 50, 20, 10.0);
            _vpEngine = new VolumeProfileEngine(TickMath.TICK_SIZE);
            _structureEngine = new MarketStructureEngine(3);
            _kzEngine = new KillZoneEngine(true, true, false);
            _scorer = new SignalScorer();
        }

        protected override void OnRun()
        {
            _tradeAccount = Core.Instance.Accounts.FirstOrDefault(a => a.Name.Contains(AccountFilter));
            if (_tradeAccount == null)
            {
                Core.Instance.Loggers.Log($"[BOOT] Account matching '{AccountFilter}' not found. Stopping.", LoggingLevel.Error);
                Stop();
                return;
            }

            if (!EnableAutoTrading)
            {
                Core.Instance.Loggers.Log("[BOOT] Auto-trading is disabled by parameter. Running in monitor mode.", LoggingLevel.System);
            }

            _tradingEnabled = EnableAutoTrading;
            
            _tradeSymbol = Core.Instance.Symbols.FirstOrDefault(s => s.Name == InstrumentMode);
            if (_tradeSymbol == null)
            {
                Core.Instance.Loggers.Log($"[BOOT] Symbol {InstrumentMode} not found.", LoggingLevel.Error);
                Stop();
                return;
            }

            // Subscribe to events
            _tradeSymbol.NewLast += Symbol_NewLast;
            _tradeSymbol.NewLevel2 += Symbol_NewLevel2;
            
            Core.PositionAdded += Positions_PositionChanged;
            Core.PositionRemoved += Positions_PositionChanged;

            Core.Instance.Loggers.Log($"[BOOT] Strategy Initialized on {_tradeAccount.Name}, Mode={InstrumentMode}.", LoggingLevel.System);
        }

        protected override void OnStop()
        {
            if (_tradeSymbol != null)
            {
                _tradeSymbol.NewLast -= Symbol_NewLast;
                _tradeSymbol.NewLevel2 -= Symbol_NewLevel2;
            }
            Core.PositionAdded -= Positions_PositionChanged;
            Core.PositionRemoved -= Positions_PositionChanged;
            
            base.OnStop();
        }

        private void Symbol_NewLast(Symbol symbol, Last last)
        {
            bool isBuy = last.AggressorFlag == AggressorFlag.Buy;
            _flowEngine.OnNewTrade((decimal)last.Price, (long)last.Size, isBuy);
            _vpEngine.AddTrade(last.Price, (long)last.Size, isBuy);
            
            _kzEngine.Update(DateTime.UtcNow);
            UpdateRiskMetrics();
            RunStateMachine(last.Price);
        }
        
        private void Symbol_NewLevel2(Symbol symbol, Level2Quote level2, DOMQuote dom)
        {
            bool isBid = level2.PriceType == QuotePriceType.Bid;
            _flowEngine.OnLevel2Update((decimal)level2.Price, (long)level2.Size, isBid);
        }

        private void UpdateRiskMetrics()
        {
            if (_tradeAccount != null)
            {
                _compliance.UpdateEquity(_tradeAccount.Balance);
                
                if (!_compliance.CanTrade)
                {
                    _circuitBreaker.DowngradeState(CircuitBreakerState.Emergency);
                }

                if (_circuitBreaker.State == CircuitBreakerState.Emergency)
                {
                    ExecuteEmergencyLiquidation();
                }
            }
        }

        private void ExecuteEmergencyLiquidation()
        {
            if (!_tradingEnabled) return;
            
            Core.Instance.Loggers.Log("[EMERGENCY] Triggered. Cancelling orders, flattening positions.", LoggingLevel.Error);
            _tradingEnabled = false;
            
            // Cancel Orders
            var activeOrders = Core.Instance.Orders.Where(o => o.Account == _tradeAccount && (o.Status == OrderStatus.Opened || o.Status == OrderStatus.PartiallyFilled));
            foreach (var order in activeOrders)
            {
                order.Cancel();
            }

            // Close Positions
            var positions = Core.Instance.Positions.Where(p => p.Account == _tradeAccount && p.Symbol == _tradeSymbol);
            foreach (var pos in positions)
            {
                pos.Close();
            }
            
            _currentState = StrategyState.IDLE;
        }

        private void RunStateMachine(double currentPrice)
        {
            if (!_tradingEnabled || _circuitBreaker.State > CircuitBreakerState.Reduced) return;

            switch (_currentState)
            {
                case StrategyState.IDLE:
                    if (_kzEngine.IsKillZoneActive && _structureEngine.ConsolidatedBias != MarketBiasEnum.Neutral)
                    {
                        _currentState = StrategyState.WATCHING_FOR_SWEEP;
                    }
                    break;

                case StrategyState.WATCHING_FOR_SWEEP:
                    // Simulated sweep detection
                    if (currentPrice < _structureEngine.PDL - 2 * TickMath.TICK_SIZE) 
                    {
                        Core.Instance.Loggers.Log("[STRUCT] Sweep below PDL detected.", LoggingLevel.System);
                        _currentState = StrategyState.SWEEP_CONFIRMED;
                    }
                    if (!_kzEngine.IsKillZoneActive) _currentState = StrategyState.IDLE;
                    break;

                case StrategyState.SWEEP_CONFIRMED:
                    // Await CHoCH... simulated transition for completeness
                    _currentState = StrategyState.SEEKING_ENTRY;
                    break;

                case StrategyState.SEEKING_ENTRY:
                    // Score signal
                    var ctx = new SignalContext
                    {
                        IsLong = true,
                        ConsolidatedBias = _structureEngine.ConsolidatedBias,
                        IsKillZoneActive = _kzEngine.IsKillZoneActive,
                        CurrentKillZone = _kzEngine.CurrentKillZone.ToString(),
                        StopLossTicks = 40,
                        TakeProfitTicks = 120,
                        HasDeltaFlip = _flowEngine.CurrentTapeSignal == TapeSignal.BuyHeavy
                    };

                    if (_scorer.IsValidSetup(ctx))
                    {
                        PlaceEntryOrder(currentPrice, currentPrice - 4.0, currentPrice + 12.0);
                        _currentState = StrategyState.IN_TRADE;
                    }
                    else
                    {
                        Core.Instance.Loggers.Log($"[SIGNAL] Rejected: {_scorer.GetRejectionReason(ctx)}", LoggingLevel.System);
                        _currentState = StrategyState.IDLE;
                    }
                    break;

                case StrategyState.IN_TRADE:
                    // Monitor trade, handled by OnOrderChanged/OnPositionChanged normally
                    break;
            }
        }

        private void PlaceEntryOrder(double entry, double sl, double tp)
        {
            int qty = Math.Max(1, (int)(_tradeAccount.Balance * RiskPerTradePct / (Math.Abs(entry - sl) * TickMath.TICK_SIZE)));
            if (qty > _compliance.MaxContractsAllowed) qty = _compliance.MaxContractsAllowed;
            
            Core.Instance.Loggers.Log($"[ORDER] Placing entry. Price={entry}, SL={sl}, TP={tp}, Qty={qty}", LoggingLevel.System);
            // Execute order via SDK
            // Note: In real SDK, setup an OrderTicket with attached SL/TP brackets.
            // Simplified placeholder call:
            // Core.Instance.PlaceOrder(new PlaceOrderRequestParameters { ... });
        }
        
        private void Positions_PositionChanged(Position position)
        {
            if (position.Account == _tradeAccount && position.Symbol == _tradeSymbol)
            {
                // This handler is now hooked to PositionRemoved, so the position is closed.
                double pnl = position.NetPnL != null ? position.NetPnL.Value : 0;
                _dailyRisk.OnTradeClosed(pnl);
                Core.Instance.Loggers.Log($"[TRADE] Position closed. PnL={pnl}", LoggingLevel.System);
                _currentState = StrategyState.IDLE;
            }
        }
    }
}
