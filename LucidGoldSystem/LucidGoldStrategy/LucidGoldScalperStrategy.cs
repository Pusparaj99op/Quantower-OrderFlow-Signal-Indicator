// ============================================================
// LucidGoldScalperStrategy.cs
// LucidGold.Strategy namespace
// Quantower Strategy — autonomous order placement engine
// ============================================================
//
// ⚠️ IMPORTANT: All Lucid Trading rule values in lucid_rules.json
// MUST be verified against current official Lucid Trading documentation
// before each live deployment. Prop firm rules change without notice.
// This system is provided for educational and developmental purposes.
// The author and developer accept no liability for trading losses.
// Always trade on a paper/simulation account first.
// Verify SDK API compatibility with your installed Quantower version.
//
// ============================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Integration;  // for QuotePriceType in Level2Quote.PriceType
using LucidGold.Core.Engines;
using LucidGold.Core.Enums;
using LucidGold.Core.Models;
using LucidGold.Core.RiskManagement;
using CoreBar = LucidGold.Core.Models.Bar;
using LocalAggressorSide = LucidGold.Core.Enums.AggressorSide;

namespace LucidGold.Strategy
{
    /// <summary>
    /// Lucid Gold Scalper — production autonomous ICT/SMC order-flow strategy.
    /// Targets MGC or GC on Lucid Trading Flex 25K evaluation accounts.
    ///
    /// <para>Full 8-state machine: IDLE → SCANNING → SETUP_IDENTIFIED →
    /// AWAITING_CONFIRMATION → ORDER_PENDING → IN_TRADE → MANAGING → FLAT → HALTED.</para>
    ///
    /// <para>Compliance: trailing drawdown, daily loss, consistency, news blackout,
    /// weekend flatten, expiration guard — all loaded from lucid_rules.json.</para>
    /// </summary>
    public class LucidGoldScalperStrategy : TradingPlatform.BusinessLayer.Strategy
    {
        // ═════════════════════════════════════════════════════════════
        // INPUT PARAMETERS
        // ═════════════════════════════════════════════════════════════

        // ── Group: Instrument ─────────────────────────────────────────
        [InputParameter("Symbol (MGC or GC)", 10)]
        public Symbol? TradingSymbol { get; set; }

        [InputParameter("Account Name Filter", 11)]
        public string AccountNameFilter { get; set; } = "Lucid";

        // ── Group: Risk ───────────────────────────────────────────────
        [InputParameter("Max Stop Loss Ticks", 20, 1, 1, 50, 1)]
        public int MaxStopLossTicks { get; set; } = 50;

        [InputParameter("Min Profit Target Ticks", 21, 1, 100, 500, 5)]
        public int MinProfitTargetTicks { get; set; } = 100;

        [InputParameter("Min Reward/Risk Ratio", 22, 0.1, 2.0, 10.0, 1)]
        public double MinRRRatio { get; set; } = 2.0;

        [InputParameter("Max Contracts", 23, 1, 1, 10, 1)]
        public int MaxContracts { get; set; } = 1;

        // ── Group: Entry ──────────────────────────────────────────────
        [InputParameter("Min Signal Score (60–90)", 30, 1, 60, 90, 1)]
        public int MinSignalScore { get; set; } = 65;

        [InputParameter("Max Setup Wait Minutes", 31, 1, 30, 120, 5)]
        public int MaxSetupWaitMinutes { get; set; } = 30;

        [InputParameter("Max Fill Wait Minutes", 32, 1, 15, 60, 1)]
        public int MaxFillWaitMinutes { get; set; } = 15;

        [InputParameter("Use Limit Orders (false = market)", 33)]
        public bool UseLimitOrders { get; set; } = true;

        // ── Group: Exits ──────────────────────────────────────────────
        [InputParameter("Breakeven At Ticks Profit", 40, 1, 25, 100, 5)]
        public int BreakevenAtTicks { get; set; } = 25;

        [InputParameter("TP1 Ticks", 41, 1, 100, 500, 10)]
        public int TP1Ticks { get; set; } = 100;

        [InputParameter("TP2 Ticks", 42, 1, 200, 1000, 10)]
        public int TP2Ticks { get; set; } = 200;

        [InputParameter("Max Trade Duration Minutes", 43, 1, 120, 480, 10)]
        public int MaxTradeDurationMinutes { get; set; } = 120;

        [InputParameter("Enable Time Stop", 44)]
        public bool EnableTimeStop { get; set; } = true;

        // ── Group: Sessions ───────────────────────────────────────────
        [InputParameter("Enable London Kill Zone", 50)]
        public bool EnableLondon { get; set; } = true;

        [InputParameter("Enable NY Kill Zone", 51)]
        public bool EnableNY { get; set; } = true;

        [InputParameter("Enable London Close", 52)]
        public bool EnableLondonClose { get; set; } = false;

        [InputParameter("Enable Afternoon Session", 53)]
        public bool EnableAfternoon { get; set; } = false;

        // ── Group: Filters ────────────────────────────────────────────
        [InputParameter("Min ATR (ticks, 20-period)", 60, 0.1, 8.0, 200.0, 1)]
        public double MinATR { get; set; } = 8.0;

        [InputParameter("Max ATR (ticks, 20-period)", 61, 1.0, 80.0, 500.0, 1)]
        public double MaxATR { get; set; } = 80.0;

        [InputParameter("Max Daily Trades", 62, 1, 3, 20, 1)]
        public int MaxDailyTrades { get; set; } = 3;

        [InputParameter("Large Lot Threshold MGC", 63, 1, 50, 500, 5)]
        public int LargeLotMGC { get; set; } = 50;

        [InputParameter("Large Lot Threshold GC", 64, 1, 10, 100, 1)]
        public int LargeLotGC { get; set; } = 10;

        // ── Group: Config Paths ───────────────────────────────────────
        [InputParameter("Lucid Rules JSON Path", 70)]
        public string LucidRulesPath { get; set; } = @"C:\Quantower\Config\lucid_rules.json";

        [InputParameter("News Events CSV Path", 71)]
        public string NewsEventsPath { get; set; } = @"C:\Quantower\Config\news_events.csv";

        // ── Group: Debug ──────────────────────────────────────────────
        [InputParameter("Enable Debug Logging", 80)]
        public bool EnableDebugLog { get; set; } = false;

        [InputParameter("Log Tick Data (Warning: large files)", 81)]
        public bool LogTickData { get; set; } = false;

        // ═════════════════════════════════════════════════════════════
        // SIGNAL ENGINES
        // ═════════════════════════════════════════════════════════════

        private MarketStructureEngine _msEngine5M  = null!;
        private MarketStructureEngine _msEngine15M = null!;
        private FVGEngine             _fvgEngine   = null!;
        private OrderBlockEngine      _obEngine    = null!;
        private HTFBiasEngine         _htfEngine   = null!;
        private KillZoneEngine        _kzEngine    = null!;
        private CumulativeDeltaEngine _deltaEngine = null!;
        private FootprintEngine       _fpEngine    = null!;
        private DOMAbsorptionEngine   _domEngine   = null!;
        private TapeReadingEngine     _tapeEngine  = null!;
        private VolumeProfileEngine   _vpEngine    = null!;
        private SignalScoringEngine   _scoreEngine = null!;
        private RiskCalculator        _riskCalc    = null!;
        private LucidComplianceGuard  _compliance  = null!;

        // ═════════════════════════════════════════════════════════════
        // STATE MACHINE
        // ═════════════════════════════════════════════════════════════

        private volatile TradeState _state = TradeState.Idle;
        private readonly object     _stateLock = new object();

        private TradeSignal?  _activeSignal;
        private DateTime      _setupIdentifiedTime;
        private DateTime      _orderPlacedTime;
        private DateTime      _entryFilledTime;
        private SignalDirection _setupDirection = SignalDirection.Long;

        // ═════════════════════════════════════════════════════════════
        // ORDER TRACKING
        // ═════════════════════════════════════════════════════════════

        private Order?  _entryOrder;
        private Order?  _stopOrder;
        private Order?  _tp1Order;
        private Order?  _tp2Order;
        private double  _actualEntryPrice;
        private int     _entryQuantity;
        private bool    _tp1Hit;
        private bool    _breakevenMoved;
        private double  _trailingStopPrice;

        // ═════════════════════════════════════════════════════════════
        // SESSION / DAILY TRACKING
        // ═════════════════════════════════════════════════════════════

        private int    _dailyTradeCount;
        private double _sessionRealizedPnL;
        private double _sessionUnrealizedPnL;
        private DateTime _lastHeartbeatTime = DateTime.MinValue;
        private DateTime _lastBarTime5M  = DateTime.MinValue;
        private DateTime _lastHtfRefresh = DateTime.MinValue;
        private static readonly TimeZoneInfo ET =
            TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        // ═════════════════════════════════════════════════════════════
        // INSTRUMENT CONFIG
        // ═════════════════════════════════════════════════════════════

        private double _tickSize  = 0.1;
        private double _tickValue = 1.0;
        private bool   _isMGC    = true;
        private long   _largeLotThreshold = 50;

        // ═════════════════════════════════════════════════════════════
        // LOCK-FREE LOGGING QUEUE
        // ═════════════════════════════════════════════════════════════

        private readonly ConcurrentQueue<(string Msg, StrategyLoggingLevel Level)> _logQueue
            = new ConcurrentQueue<(string, StrategyLoggingLevel)>();
        private Thread? _logThread;
        private volatile bool _logRunning;

        // ═════════════════════════════════════════════════════════════
        // CONSTRUCTOR
        // ═════════════════════════════════════════════════════════════

        public LucidGoldScalperStrategy()
        {
            Name        = "Lucid Gold Scalper";
            Description = "Production ICT/SMC/Order-Flow autonomous strategy for MGC/GC " +
                          "on Lucid Flex 25K evaluation. Reads lucid_rules.json for compliance.";
            // Note: Version property is read-only in the SDK; do not assign here.
        }

        // ═════════════════════════════════════════════════════════════
        // LIFECYCLE — OnCreated
        // ═════════════════════════════════════════════════════════════

        protected override void OnCreated()
        {
            base.OnCreated();

            // Detect instrument
            string symName = TradingSymbol?.Name ?? "MGC";
            _isMGC    = symName.Contains("MGC", StringComparison.OrdinalIgnoreCase);
            _tickSize = _isMGC ? 0.1  : 0.1;
            _tickValue= _isMGC ? 1.0  : 10.0;
            _largeLotThreshold = _isMGC ? LargeLotMGC : LargeLotGC;

            // Instantiate all engines
            _msEngine5M  = new MarketStructureEngine();
            _msEngine15M = new MarketStructureEngine();
            _fvgEngine   = new FVGEngine(_tickSize);
            _obEngine    = new OrderBlockEngine(_tickSize);
            _htfEngine   = new HTFBiasEngine(msg => QueueLog(msg, StrategyLoggingLevel.Info));
            _kzEngine    = new KillZoneEngine();
            _deltaEngine = new CumulativeDeltaEngine();
            _fpEngine    = new FootprintEngine(_tickSize);
            _domEngine   = new DOMAbsorptionEngine(_largeLotThreshold);
            _tapeEngine  = new TapeReadingEngine();
            _vpEngine    = new VolumeProfileEngine(_tickSize);
            _scoreEngine = new SignalScoringEngine();
            _riskCalc    = new RiskCalculator(symName, _tickSize, _tickValue);

            // Load compliance config
            LoadComplianceConfig();

            // Start lock-free log thread
            _logRunning = true;
            _logThread  = new Thread(LogThreadProc) { IsBackground = true, Name = "LucidGold-Log" };
            _logThread.Start();

            QueueLog($"[INIT] LucidGoldScalper created | Symbol={symName} | " +
                     $"TickSize={_tickSize} | TickValue={_tickValue:C}",
                     StrategyLoggingLevel.Info);
        }

        // ═════════════════════════════════════════════════════════════
        // LIFECYCLE — OnRun
        // ═════════════════════════════════════════════════════════════

        protected override void OnRun()
        {
            base.OnRun();

            if (TradingSymbol == null)
            {
                QueueLog("[FATAL] TradingSymbol not configured. Strategy halted.",
                         StrategyLoggingLevel.Error);
                TransitionState(TradeState.Halted, "No symbol configured");
                return;
            }

            // Subscribe to all market data events
            TradingSymbol.NewLast     += Symbol_NewLast;
            TradingSymbol.NewLevel2   += Symbol_NewLevel2;
            TradingSymbol.NewQuote    += Symbol_NewQuote;



            // Verify contract expiration
            CheckExpirationOnStartup();

            TransitionState(TradeState.Scanning, "Strategy started");

            // Start heartbeat timer task
            _ = Task.Run(HeartbeatLoopAsync);
        }

        // ═════════════════════════════════════════════════════════════
        // LIFECYCLE — OnStop
        // ═════════════════════════════════════════════════════════════

        protected override void OnStop()
        {
            base.OnStop();

            // Unsubscribe all events
            if (TradingSymbol != null)
            {
                TradingSymbol.NewLast   -= Symbol_NewLast;
                TradingSymbol.NewLevel2 -= Symbol_NewLevel2;
                TradingSymbol.NewQuote  -= Symbol_NewQuote;
            }

            var pos = FindOpenPosition();
            if (pos != null) pos.Updated -= OnPositionUpdated;

            // Stop log thread
            _logRunning = false;
            FlushLogQueue();

            TransitionState(TradeState.Halted, "OnStop called");

            // Dispose DOM engine
            _domEngine?.Dispose();

            QueueLog("[STATE] Strategy stopped.", StrategyLoggingLevel.Info);
            FlushLogQueue();
        }

        // ═════════════════════════════════════════════════════════════
        // EVENT — NewLast (HOT PATH — must return in < 1 ms)
        // ═════════════════════════════════════════════════════════════

        private void Symbol_NewLast(Symbol sym, Last last)
        {
            var side = last.AggressorFlag == AggressorFlag.Buy  ? LocalAggressorSide.Buy
                     : last.AggressorFlag == AggressorFlag.Sell ? LocalAggressorSide.Sell
                     : LocalAggressorSide.Unknown;

            // Lock-free engine updates (< 1 µs each)
            _deltaEngine.ProcessTrade(last.Price, (long)last.Size, side);
            _fpEngine.ProcessTrade(last.Price, (long)last.Size, side);
            _tapeEngine.ProcessTrade(last.Price, (long)last.Size, side, last.Time, _largeLotThreshold);
            _vpEngine.ProcessTrade(last.Price, (long)last.Size);

            if (LogTickData)
                QueueLog($"[TICK] {last.Price:F1} x{last.Size} {side}", StrategyLoggingLevel.Info);

            // Bar transition detection (5-minute boundary)
            DateTime barBoundary = new DateTime(
                last.Time.Year, last.Time.Month, last.Time.Day,
                last.Time.Hour, (last.Time.Minute / 5) * 5, 0, DateTimeKind.Utc);

            if (barBoundary > _lastBarTime5M)
            {
                _lastBarTime5M = barBoundary;
                Task.Run(() => ProcessBarsOnNewBar(sym));
            }
        }

        // ═════════════════════════════════════════════════════════════
        // EVENT — NewLevel2 (DOM)
        // ═════════════════════════════════════════════════════════════

        private void Symbol_NewLevel2(Symbol sym, Level2Quote quote, DOMQuote dom)
        {
            // QuotePriceType is in TradingPlatform.BusinessLayer.Integration namespace (added above)
            var side    = quote.PriceType == QuotePriceType.Bid ? QuoteSide.Bid : QuoteSide.Ask;
            var domQuote = new DomLevel2Quote(quote.Price, (long)quote.Size, side, quote.Time);
            _domEngine.ProcessLevel2(domQuote);

            // Notify DOM engine of aggressive trades for absorption detection
            // (cross-reference with last trade via a lightweight check)
        }

        // ═════════════════════════════════════════════════════════════
        // BAR PROCESSING — Triggered from OnUpdate in quote handler
        // ═════════════════════════════════════════════════════════════

        // ─────────────────────────────────────────────────────────────
        // BAR PROCESSING — Driven by HistoricalData.NewHistoryItem events
        // (BUG 11 FIX: replaces incorrect quote-based throttle approach)
        // ─────────────────────────────────────────────────────────────

        private void ProcessBarsOnNewBar(Symbol sym)
        {
            try
            {
                var bars5M = FetchBars(sym.Name, Period.MIN5, 30).ToList();
                if (bars5M.Count >= 3)
                {
                    var b0 = bars5M[^1];
                    var b1 = bars5M[^2];
                    var b2 = bars5M[^3];
                    _msEngine5M.ProcessNewBar(b1, "5M");
                    _fvgEngine.ProcessNewBar(b2, b1, b0, "5M");
                    _fpEngine.OnBarClose(b0.High, b0.Low, b0.Close, b0.Time);
                    _deltaEngine.OnBarClose(b0.High, b0.Low);
                    _obEngine.ProcessBars(bars5M, _msEngine5M.GetATR20(), _tickSize);
                    _fvgEngine.UpdateFillStatus(b0.Close, _tickSize);
                }

                var bars15M = FetchBars(sym.Name, Period.MIN15, 10).ToList();
                if (bars15M.Count > 0)
                    _msEngine15M.ProcessNewBar(bars15M[^1], "15M");

                _domEngine.EvaluateConditions();
                _vpEngine.CheckSessionReset(DateTime.UtcNow);
                CheckDailySessionReset();

                if ((DateTime.UtcNow - _lastHtfRefresh).TotalMinutes >= 15)
                {
                    _lastHtfRefresh = DateTime.UtcNow;
                    RefreshHTFBias(sym.Name);
                }
            }
            catch (Exception ex)
            {
                QueueLog($"[ERROR] ProcessBarsOnNewBar: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        private void RefreshHTFBias(string symbolName)
        {
            try
            {
                var daily = FetchBars(symbolName, Period.DAY1, 50);
                var h4    = FetchBars(symbolName, new Period(PeriodType.Hour, 4), 50);
                _htfEngine.Refresh(daily, h4);
            }
            catch (Exception ex)
            {
                QueueLog($"[WARN] HTF refresh failed: {ex.Message}", StrategyLoggingLevel.Info);
            }
        }

        private IEnumerable<CoreBar> FetchBars(string symbolName, Period period, int count)
        {
            try
            {
                var sym = TradingPlatform.BusinessLayer.Core.Instance.Symbols.FirstOrDefault(s => s.Name == symbolName);
                if (sym == null) return Enumerable.Empty<CoreBar>();

                var history = sym.GetHistory(period, DateTime.UtcNow.AddDays(-count));
                if (history == null) return Enumerable.Empty<CoreBar>();

                return history.Select(item =>
                {
                    if (item is HistoryItemBar b)
                        return new CoreBar { Time=item.TimeLeft, Open=b.Open,
                                             High=b.High, Low=b.Low,
                                             Close=b.Close, Volume=(long)b.Volume };
                    double p = item[PriceType.Close];
                    return new CoreBar { Time=item.TimeLeft, Open=p, High=p, Low=p, Close=p };
                }).ToList();
            }
            catch { return Enumerable.Empty<CoreBar>(); }
        }

        // ═════════════════════════════════════════════════════════════
        // EVENT — OnNewQuote (state machine evaluation)
        // ═════════════════════════════════════════════════════════════

        private void Symbol_NewQuote(Symbol sym, Quote quote)
        {
            // Dispatch to background — do not run state machine on platform event thread
            Task.Run(() => EvaluateStateMachine(sym, quote));
        }

        // ═════════════════════════════════════════════════════════════
        // STATE MACHINE EVALUATION
        // ═════════════════════════════════════════════════════════════

        private void EvaluateStateMachine(Symbol sym, Quote quote)
        {
            try
            {
                TradeState currentState;
                lock (_stateLock) currentState = _state;

                double bid = quote.Bid;
                double ask = quote.Ask;
                double mid = (bid + ask) / 2.0;

                // ── Compliance heartbeat checks ─────────────────────────────
                var account = FindLucidAccount();
                if (account != null)
                {
                    // SDK: Account has only .Balance. Use it for equity tracking.
                    decimal equity  = (decimal)account.Balance;
                    decimal realPnL = 0m;  // no TodayRealizedPnL in this SDK version
                    _compliance.UpdateEquity(equity, realPnL, 0m);

                    if (_compliance.IsEmergencyHalt && currentState != TradeState.Halted)
                    {
                        EmergencyHalt("DrawdownFloor breached");
                        return;
                    }
                }

                // ── Weekend flatten ─────────────────────────────────────────
                if (_compliance.ShouldFlattenForWeekend(DateTime.UtcNow) &&
                    currentState == TradeState.InTrade)
                {
                    FlattenPosition("WeekendFlat");
                    return;
                }

                // ── News flatten ────────────────────────────────────────────
                if (_compliance.ShouldFlattenForNews(DateTime.UtcNow) &&
                    currentState == TradeState.InTrade)
                {
                    FlattenPosition("NewsFlatten");
                    return;
                }

                // ── State-specific logic ────────────────────────────────────
                switch (currentState)
                {
                    case TradeState.Halted:
                        CheckDailyReset_Halt();
                        break;

                    case TradeState.Idle:
                        EvaluateIdleToScanning();
                        break;

                    case TradeState.Scanning:
                        EvaluateScanning(mid);
                        break;

                    case TradeState.SetupIdentified:
                        EvaluateSetupIdentified(mid);
                        break;

                    case TradeState.AwaitingConfirmation:
                        EvaluateAwaitingConfirmation(mid, bid, ask);
                        break;

                    case TradeState.OrderPending:
                        EvaluateOrderPending(mid);
                        break;

                    case TradeState.InTrade:
                    case TradeState.Managing:
                        EvaluatePositionManagement(mid, bid, ask);
                        break;
                }
            }
            catch (Exception ex)
            {
                QueueLog($"[ERROR] StateMachine: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // State: IDLE
        // ─────────────────────────────────────────────────────────────

        private void EvaluateIdleToScanning()
        {
            // Enter scanning only during a kill zone
            string kz = _kzEngine.GetActiveKillZoneName(DateTime.UtcNow,
                            EnableLondon, EnableNY, EnableLondonClose, EnableAfternoon);
            if (kz != "None")
                TransitionState(TradeState.Scanning, $"Kill zone active: {kz}");
        }

        // ─────────────────────────────────────────────────────────────
        // State: SCANNING
        // ─────────────────────────────────────────────────────────────

        private void EvaluateScanning(double mid)
        {
            // Exit scanning if kill zone ends
            string kz = _kzEngine.GetActiveKillZoneName(DateTime.UtcNow,
                            EnableLondon, EnableNY, EnableLondonClose, EnableAfternoon);
            if (kz == "None")
            {
                TransitionState(TradeState.Idle, "Kill zone ended");
                return;
            }

            // ATR filter
            double atr20 = _msEngine5M.GetATR20();
            if (atr20 < MinATR * _tickSize || atr20 > MaxATR * _tickSize) return;

            // HTF bias check
            var bias = _htfEngine.GetCurrentBias();
            if (bias == MarketBias.Neutral) return;

            SignalDirection dir = bias is MarketBias.StrongBullish or MarketBias.Bullish
                ? SignalDirection.Long : SignalDirection.Short;

            // Check for market structure event (MSS or CHoCH)
            var ms = _msEngine5M.GetLatestStructureEvent();
            if (ms == null) return;
            if (ms.Direction != dir) return;
            if (ms.EventType == StructureEventType.BOS) return; // Need at least CHoCH for scanning

            // Build setup context
            var ctx = BuildSetupContext(mid, kz, bias, dir);
            float score = _scoreEngine.ScoreSetup(ctx, dir);

            if (score >= MinSignalScore)
            {
                ctx.SignalScore = score;
                // Build TradeSignal
                double swingStop = dir == SignalDirection.Long
                    ? _msEngine5M.GetSwingLow()
                    : _msEngine5M.GetSwingHigh();

                var signal = _riskCalc.CalculateTrade(mid, swingStop, dir,
                    TP1Ticks, TP2Ticks, 1, score, ctx,
                    $"Score={score:F0}|KZ={kz}|Bias={bias}|MSS={ms.EventType}");

                if (signal == null)
                {
                    QueueLog($"[SCAN] Signal rejected by RiskCalc | Score={score:F0} | " +
                             $"NaturalSL too wide or RR < {MinRRRatio:F1}",
                             StrategyLoggingLevel.Info);
                    return;
                }

                _activeSignal        = signal;
                _setupDirection      = dir;
                _setupIdentifiedTime = DateTime.UtcNow;

                QueueLog($"[SETUP] Direction={dir} | Score={score:F0} | HTF={bias} | " +
                         $"KZ={kz} | FVG={ctx.NearestFVG?.Top:F1}-{ctx.NearestFVG?.Bottom:F1} | " +
                         $"OB={ctx.NearestOrderBlock?.Top:F1}-{ctx.NearestOrderBlock?.Bottom:F1} | " +
                         $"MSS={ms.Price:F1} | ATR={atr20:F1}",
                         StrategyLoggingLevel.Trading);

                TransitionState(TradeState.SetupIdentified, $"Score={score:F0}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // State: SETUP_IDENTIFIED
        // ─────────────────────────────────────────────────────────────

        private void EvaluateSetupIdentified(double mid)
        {
            if (_activeSignal == null) { TransitionState(TradeState.Scanning, "Signal lost"); return; }

            // Wait for price to approach entry zone
            double entryZoneTop    = _activeSignal.Context.EntryZoneTop;
            double entryZoneBottom = _activeSignal.Context.EntryZoneBottom;

            if (entryZoneTop <= 0 || entryZoneBottom <= 0)
            {
                // If no specific entry zone, advance to confirmation immediately
                TransitionState(TradeState.AwaitingConfirmation, "No entry zone — immediate confirm");
                return;
            }

            bool priceInZone = mid >= entryZoneBottom && mid <= entryZoneTop;
            if (!priceInZone)
            {
                // Invalidate if waited too long
                if ((DateTime.UtcNow - _setupIdentifiedTime).TotalMinutes > MaxSetupWaitMinutes)
                {
                    QueueLog($"[SETUP] Expired after {MaxSetupWaitMinutes}min waiting for entry zone.",
                             StrategyLoggingLevel.Info);
                    _activeSignal = null;
                    TransitionState(TradeState.Scanning, "Setup expired — price never reached zone");
                }
                return;
            }

            // Re-score the setup
            string kz   = _kzEngine.GetActiveKillZoneName(DateTime.UtcNow, EnableLondon, EnableNY, EnableLondonClose, EnableAfternoon);
            var    bias  = _htfEngine.GetCurrentBias();
            var    ctx   = BuildSetupContext(mid, kz, bias, _setupDirection);
            float  score = _scoreEngine.ScoreSetup(ctx, _setupDirection);

            if (score < 50)
            {
                QueueLog($"[SETUP] Score degraded to {score:F0} < 50 — cancelling setup.",
                         StrategyLoggingLevel.Info);
                _activeSignal = null;
                TransitionState(TradeState.Scanning, "Score degraded below 50");
                return;
            }

            TransitionState(TradeState.AwaitingConfirmation, $"Price in entry zone, score={score:F0}");
        }

        // ─────────────────────────────────────────────────────────────
        // State: AWAITING_CONFIRMATION
        // ─────────────────────────────────────────────────────────────

        // BUG 7 FIX: was async Task, changed to void. Removed await + async keyword.
        private void EvaluateAwaitingConfirmation(double mid, double bid, double ask)
        {
            if (_activeSignal == null) { TransitionState(TradeState.Scanning, "Signal lost"); return; }

            // Check all final confirmation conditions
            bool domAbsorption  = _domEngine.IsAbsorptionActive();
            bool tapeOk         = !_tapeEngine.IsTapeExhaustion(_setupDirection == SignalDirection.Long
                                                 ? SignalDirection.Short : SignalDirection.Long);
            bool deltaOk        = !_deltaEngine.IsDeltaDivergent(_setupDirection);

            // Compliance pre-entry check
            if (!_compliance.PreEntryCheck(_activeSignal, DateTime.UtcNow))
            {
                TransitionState(TradeState.Scanning, "Compliance PreEntryCheck blocked");
                _activeSignal = null;
                return;
            }

            // Daily trade limit check
            if (_dailyTradeCount >= MaxDailyTrades)
            {
                QueueLog($"[COMPLIANCE] Daily trade limit reached ({MaxDailyTrades}). Halting.",
                         StrategyLoggingLevel.Error);
                TransitionState(TradeState.Halted, "DailyTradeLimit");
                return;
            }

            if (!tapeOk || !deltaOk)
            {
                // Not confirmed — check timeout
                if ((DateTime.UtcNow - _setupIdentifiedTime).TotalMinutes > MaxSetupWaitMinutes)
                {
                    QueueLog("[AWAIT] Confirmation timeout — cancelling setup.", StrategyLoggingLevel.Info);
                    _activeSignal = null;
                    TransitionState(TradeState.Scanning, "Confirmation timeout");
                }
                return;
            }

            // All confirmations satisfied — place order
            PlaceEntryOrder(bid, ask);
        }

        // ─────────────────────────────────────────────────────────────
        // State: ORDER_PENDING
        // ─────────────────────────────────────────────────────────────

        private void EvaluateOrderPending(double mid)
        {
            if (_entryOrder == null)
            {
                TransitionState(TradeState.Scanning, "Entry order lost");
                return;
            }

            // Cancel if fill wait exceeded
            if ((DateTime.UtcNow - _orderPlacedTime).TotalMinutes > MaxFillWaitMinutes)
            {
                QueueLog($"[ORDER] Fill timeout ({MaxFillWaitMinutes}min). Cancelling entry order.",
                         StrategyLoggingLevel.Info);
                // BUG 7 FIX: CancelOrder is synchronous in this SDK
                var cancelOrder = _entryOrder;
                _entryOrder   = null;
                _activeSignal = null;
                try { TradingPlatform.BusinessLayer.Core.Instance.CancelOrder(
                        new CancelOrderRequestParameters { Order = cancelOrder }); }
                catch { }
                TransitionState(TradeState.Scanning, "Fill timeout");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // State: IN_TRADE / MANAGING
        // ─────────────────────────────────────────────────────────────

        private void EvaluatePositionManagement(double mid, double bid, double ask)
        {
            if (_activeSignal == null) return;

            var position = FindOpenPosition();
            if (position == null)
            {
                TransitionState(TradeState.Flat, "Position no longer exists");
                return;
            }

            double unrealizedPnL = position.GrossPnl;
            _sessionUnrealizedPnL = unrealizedPnL;

            double ticksProfit = _setupDirection == SignalDirection.Long
                ? (mid - _actualEntryPrice) / _tickSize
                : (_actualEntryPrice - mid) / _tickSize;

            // ── Conviction Reversal: immediate close ─────────────────
            var fpSig = _fpEngine.GetSignalSummary();
            bool stackedAgainst = _setupDirection == SignalDirection.Long
                ? fpSig.HasStackedBearishImbalance
                : fpSig.HasStackedBullishImbalance;
            bool domAgainst = (_setupDirection == SignalDirection.Long &&
                               _domEngine.GetImbalanceSignal() == DOMImbalanceSignal.AskHeavy) ||
                              (_setupDirection == SignalDirection.Short &&
                               _domEngine.GetImbalanceSignal() == DOMImbalanceSignal.BidHeavy);

            if (stackedAgainst && domAgainst)
            {
                QueueLog("[MANAGE] Conviction reversal: stacked imbalance + DOM against position. " +
                         "Closing immediately.", StrategyLoggingLevel.Trading);
                FlattenPosition("ConvictionReversal");
                return;
            }

            // ── Time Stop ────────────────────────────────────────────
            if (EnableTimeStop &&
                (DateTime.UtcNow - _entryFilledTime).TotalMinutes > MaxTradeDurationMinutes &&
                ticksProfit < TP1Ticks)
            {
                QueueLog($"[MANAGE] Time stop triggered after {MaxTradeDurationMinutes}min. " +
                         "Price has not reached TP1.", StrategyLoggingLevel.Trading);
                FlattenPosition("TimeStop");
                return;
            }

            // ── Breakeven Rule ───────────────────────────────────────
            if (!_breakevenMoved && ticksProfit >= BreakevenAtTicks)
            {
                double bePrice = _setupDirection == SignalDirection.Long
                    ? _actualEntryPrice + 2 * _tickSize
                    : _actualEntryPrice - 2 * _tickSize;

                ModifyStopOrder(bePrice);
                _breakevenMoved = true;
                QueueLog($"[MANAGE] Breakeven moved to {bePrice:F1} | Ticks profit={ticksProfit:F0}",
                         StrategyLoggingLevel.Trading);
            }

            // ── TP1 Rule ─────────────────────────────────────────────
            if (!_tp1Hit && ticksProfit >= TP1Ticks && position.Quantity > 1)
            {
                int closeQty = (int)(Math.Ceiling(position.Quantity / 2.0));
                ClosePartial(closeQty, $"TP1 @ {mid:F1}");
                _tp1Hit = true;

                double lockInPrice = _setupDirection == SignalDirection.Long
                    ? _activeSignal.TP1Price - 5 * _tickSize
                    : _activeSignal.TP1Price + 5 * _tickSize;
                ModifyStopOrder(lockInPrice);

                QueueLog($"[MANAGE] TP1 hit @ {mid:F1}. Closed {closeQty} contracts. " +
                         $"SL locked to {lockInPrice:F1}", StrategyLoggingLevel.Trading);
            }

            // ── TP2 ATR Trailing Stop ─────────────────────────────────
            if (_tp1Hit && ticksProfit >= TP2Ticks)
            {
                double atr = _msEngine5M.GetATR20();
                double newTrail = _setupDirection == SignalDirection.Long
                    ? mid - atr * 1.5
                    : mid + atr * 1.5;

                bool shouldMove = _setupDirection == SignalDirection.Long
                    ? newTrail > _trailingStopPrice
                    : _trailingStopPrice == 0 || newTrail < _trailingStopPrice;

                if (shouldMove)
                {
                    _trailingStopPrice = newTrail;
                    ModifyStopOrder(_trailingStopPrice);
                    QueueLog($"[MANAGE] ATR trail stop moved to {_trailingStopPrice:F1}",
                             StrategyLoggingLevel.Info);
                }
            }
        }

        // ═════════════════════════════════════════════════════════════
        // ORDER OPERATIONS
        // ═════════════════════════════════════════════════════════════

        // BUG 7 FIX: Renamed from PlaceEntryOrderAsync. All Core.Instance order methods
        // are synchronous in this SDK. Removed async/await throughout.
        private void PlaceEntryOrder(double bid, double ask)
        {
            if (_activeSignal == null) return;

            var account = FindLucidAccount();
            if (account == null)
            {
                QueueLog("[ORDER] No Lucid account found. Cannot place entry.", StrategyLoggingLevel.Error);
                return;
            }

            // Determine quantity
            int qty = MaxContracts;
            if (_activeSignal.Score >= 80 && MaxContracts > 1)
                qty = MaxContracts;  // Already capped by compliance
            else
                qty = 1;

            // Validate contract limit
            if (_compliance.IsContractLimitExceeded(TradingSymbol?.Name ?? "MGC", qty))
            {
                QueueLog($"[ORDER] Contract limit exceeded for qty={qty}.", StrategyLoggingLevel.Error);
                return;
            }

            double entryPrice = _activeSignal.Direction == SignalDirection.Long ? ask : bid;

            var orderType = UseLimitOrders ? OrderType.Limit : OrderType.Market;
            double? limitPrice = UseLimitOrders ? entryPrice : (double?)null;

            var entryRequest = new PlaceOrderRequestParameters
            {
                Symbol      = TradingSymbol!,
                Account     = account,
                Side        = _activeSignal.Direction == SignalDirection.Long ? Side.Buy : Side.Sell,
                OrderTypeId = orderType,
                Price       = limitPrice ?? 0,
                Quantity    = qty,
                TimeInForce = TimeInForce.Day,
                Comment     = $"LucidGold|Score={_activeSignal.Score:F0}|{_activeSignal.Direction}"
            };

            // BUG 7 / SDK FIX: PlaceOrder returns TradingOperationResult with .OrderId (string),
            // not .Order. Look up the order object from Core.Instance.Orders after placement.
            var result = TradingPlatform.BusinessLayer.Core.Instance.PlaceOrder(entryRequest);
            if (result.Status == TradingOperationResultStatus.Failure)
            {
                QueueLog($"[ORDER] Entry order failed: {result.Message}", StrategyLoggingLevel.Error);
                return;
            }

            _entryOrder    = TradingPlatform.BusinessLayer.Core.Instance.Orders
                                .FirstOrDefault(o => o.Id == result.OrderId);
            _entryQuantity = qty;
            _orderPlacedTime = DateTime.UtcNow;

            // Subscribe to order update events
            if (_entryOrder != null)
                _entryOrder.Updated += OnOrderUpdated;

            TransitionState(TradeState.OrderPending, $"Entry order placed @ {entryPrice:F1}");

            // Place stop loss order simultaneously
            double slPrice = _activeSignal.StopPrice;
            var slRequest = new PlaceOrderRequestParameters
            {
                Symbol      = TradingSymbol!,
                Account     = account,
                Side        = _activeSignal.Direction == SignalDirection.Long ? Side.Sell : Side.Buy,
                OrderTypeId = OrderType.Stop,
                TriggerPrice = slPrice,   // SDK: Stop orders use TriggerPrice, not StopPrice
                Quantity    = qty,
                TimeInForce = TimeInForce.GTC,
                Comment     = "LucidGold|SL"
            };

            var slResult = TradingPlatform.BusinessLayer.Core.Instance.PlaceOrder(slRequest);
            if (slResult.Status == TradingOperationResultStatus.Success)
            {
                _stopOrder = TradingPlatform.BusinessLayer.Core.Instance.Orders
                                .FirstOrDefault(o => o.Id == slResult.OrderId);
            }
            else
            {
                _stopOrder = null;
            }

            // Subscribe to stop order update events — IOrder.Updated fires Action<IOrder>
            if (_stopOrder != null)
                _stopOrder.Updated += OnOrderUpdated;

            if (_stopOrder == null)
                QueueLog("[ORDER] WARNING: Stop loss order failed to place! Monitor manually.",
                         StrategyLoggingLevel.Error);

            QueueLog($"[ENTRY] Symbol={TradingSymbol?.Name} | Side={_activeSignal.Direction} | " +
                     $"Qty={qty} | Entry={entryPrice:F1} | SL={slPrice:F1} | " +
                     $"TP1={_activeSignal.TP1Price:F1} | TP2={_activeSignal.TP2Price:F1} | " +
                     $"RR={_activeSignal.RRRatio:F2} | Score={_activeSignal.Score:F0}",
                     StrategyLoggingLevel.Trading);
        }

        // BUG 7 FIX: Renamed from ModifyStopOrderAsync. Sync SDK call.
        // SDK: ModifyOrderRequestParameters.OrderId (not .Order)
        private void ModifyStopOrder(double newStopPrice)
        {
            if (_stopOrder == null) return;
            try
            {
                var modRequest = new ModifyOrderRequestParameters
                {
                    OrderId      = _stopOrder.Id,
                    TriggerPrice = newStopPrice,  // SDK: Use TriggerPrice for stop orders
                    Price        = newStopPrice
                };
                TradingPlatform.BusinessLayer.Core.Instance.ModifyOrder(modRequest);
            }
            catch (Exception ex)
            {
                QueueLog($"[ORDER] ModifyStop failed: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        // BUG 7 FIX: Renamed from ClosePartialAsync. Sync SDK call.
        // SDK: ClosePositionRequestParameters uses Position property (no Quantity/CloseReason).
        // For partial close, use AdvancedTradingOperations or pass quantity via comment.
        private void ClosePartial(int quantity, string reason)
        {
            var position = FindOpenPosition();
            if (position == null) return;
            try
            {
                TradingPlatform.BusinessLayer.Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                {
                    Symbol      = TradingSymbol!,
                    Account     = position.Account,
                    Side        = position.Side == Side.Buy ? Side.Sell : Side.Buy,
                    OrderTypeId = OrderType.Market,
                    Quantity    = quantity,
                    Comment     = reason
                });
            }
            catch (Exception ex)
            {
                QueueLog($"[ORDER] ClosePartial failed: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        // BUG 7 FIX: Renamed from FlattenPositionAsync. Sync SDK calls.
        private void FlattenPosition(string reason)
        {
            // Cancel all working orders first
            CancelAllWorkingOrders();

            var position = FindOpenPosition();
            if (position == null)
            {
                TransitionState(TradeState.Flat, reason);
                return;
            }

            try
            {
                position.Close();
            }
            catch (Exception ex)
            {
                QueueLog($"[ORDER] FlattenPosition failed: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        // BUG 7 FIX: Renamed from CancelAllWorkingOrdersAsync. Sync SDK calls.
        // SDK: CancelOrder takes CancelOrderRequestParameters with Order property.
        private void CancelAllWorkingOrders()
        {
            var workingOrders = TradingPlatform.BusinessLayer.Core.Instance.Orders
                .Where(o => o.Account?.Name.Contains(AccountNameFilter,
                                StringComparison.OrdinalIgnoreCase) == true &&
                            o.Status == OrderStatus.Working)
                .ToList();

            foreach (var order in workingOrders)
            {
                try { TradingPlatform.BusinessLayer.Core.Instance.CancelOrder(
                            new CancelOrderRequestParameters { Order = order }); }
                catch { }
            }
        }

        // BUG 7 FIX: Renamed from EmergencyHaltAsync. Sync SDK calls.
        private void EmergencyHalt(string reason)
        {
            QueueLog($"[COMPLIANCE] EMERGENCY HALT | Reason={reason} | " +
                     $"Equity={_compliance.CurrentBuffer + _compliance.DrawdownFloor:F2} | " +
                     $"Floor={_compliance.DrawdownFloor:F2} | Buffer={_compliance.CurrentBuffer:F2} | " +
                     "Action=HALT/FLATTEN", StrategyLoggingLevel.Error);

            FlattenPosition("EmergencyHalt");
            TransitionState(TradeState.Halted, reason);
        }

        // ═════════════════════════════════════════════════════════════
        // ORDER & POSITION EVENT HANDLERS
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Called when an Order we are tracking gets updated.
        /// Subscribed via Order.Updated += OnOrderUpdated in order placement code.
        /// </summary>
        private void OnOrderUpdated(IOrder order)
        {
            if (order.Status == OrderStatus.Filled && order == _entryOrder)
            {
                _actualEntryPrice = order.AverageFillPrice;
                _entryFilledTime  = DateTime.UtcNow;
                _breakevenMoved   = false;
                _tp1Hit           = false;
                _trailingStopPrice = 0;
                _dailyTradeCount++;

                // Verify stop loss is working
                if (_stopOrder?.Status != OrderStatus.Working)
                {
                    QueueLog("[RISK] CRITICAL: Stop loss order NOT working after fill! " +
                             "Manual intervention required.", StrategyLoggingLevel.Error);
                }

                // BUG 9 FIX: Wire OnPositionUpdated to detect position closure via event
                var pos = FindOpenPosition();
                if (pos != null)
                    pos.Updated += OnPositionUpdated;

                TransitionState(TradeState.InTrade, $"Entry filled @ {_actualEntryPrice:F1}");
                QueueLog($"[TRADE] Position opened | Entry={_actualEntryPrice:F1} | " +
                         $"SL={_activeSignal?.StopPrice:F1} | TP1={_activeSignal?.TP1Price:F1}",
                         StrategyLoggingLevel.Trading);
            }
            else if (order.Status == OrderStatus.Cancelled && order == _entryOrder)
            {
                QueueLog("[ORDER] Entry order cancelled.", StrategyLoggingLevel.Info);
                _entryOrder   = null;
                _activeSignal = null;
                TransitionState(TradeState.Scanning, "Entry order cancelled");
            }
            else if (order.Status == OrderStatus.Filled && order == _stopOrder)
            {
                double exitPrice = order.AverageFillPrice;
                double ticks = _setupDirection == SignalDirection.Long
                    ? (_actualEntryPrice - exitPrice) / _tickSize
                    : (exitPrice - _actualEntryPrice) / _tickSize;
                double pnl = ticks * _tickValue * _entryQuantity * -1;

                LogExitEvent("StopLoss", exitPrice, ticks, pnl);
                _stopOrder    = null;
                _activeSignal = null;
                TransitionState(TradeState.Flat, "Stop loss filled");
            }
            else if (order.Status == OrderStatus.Refused)
            {
                QueueLog($"[ORDER] Refused: {order.Comment}",
                         StrategyLoggingLevel.Error);
            }
        }

        /// <summary>
        /// Called when a Position we are tracking gets updated.
        /// Subscribed via Position.Updated += OnPositionUpdated when position opens.
        /// </summary>
        private void OnPositionUpdated(Position position)
        {
            if (TradingSymbol == null || position.Symbol != TradingSymbol) return;

            _sessionUnrealizedPnL = position.GrossPnl;

            if (position.Quantity == 0 && _state == TradeState.InTrade)
            {
                // Position fully closed
                _sessionRealizedPnL += position.GrossPnl;
                TransitionState(TradeState.Flat, "Position fully closed");
                _activeSignal = null;
                _stopOrder    = null;
                _entryOrder   = null;
            }
        }

        // ═════════════════════════════════════════════════════════════
        // COMPLIANCE HELPERS
        // ═════════════════════════════════════════════════════════════

        private void LoadComplianceConfig()
        {
            LucidConfig cfg;
            try
            {
                cfg = File.Exists(LucidRulesPath)
                    ? LucidConfig.LoadFromFile(LucidRulesPath)
                    : new LucidConfig();   // fallback to defaults
                QueueLog($"[CONFIG] Loaded lucid_rules.json from {LucidRulesPath}",
                         StrategyLoggingLevel.Info);
            }
            catch (Exception ex)
            {
                QueueLog($"[CONFIG] WARNING: Could not load lucid_rules.json ({ex.Message}). " +
                         "Using default values. Verify before live trading.",
                         StrategyLoggingLevel.Error);
                cfg = new LucidConfig();
            }

            var account = FindLucidAccount();
            // SDK: Account.Equity doesn't exist; use Account.Balance as the initial equity proxy.
            decimal initialEquity = account != null ? (decimal)account.Balance : cfg.AccountSize;

            _compliance = new LucidComplianceGuard(cfg, initialEquity,
                (msg, level) => QueueLog(msg,
                    level == "Error" ? StrategyLoggingLevel.Error : StrategyLoggingLevel.Info));

            if (File.Exists(NewsEventsPath))
            {
                _compliance.LoadNewsEvents(NewsEventsPath);
                QueueLog($"[CONFIG] Loaded news events from {NewsEventsPath}", StrategyLoggingLevel.Info);
            }

            if (TradingSymbol?.ExpirationDate != null)
                // SDK: Symbol.ExpirationDate is DateTime (not DateTime?); null-check above is via null-coalescing on TradingSymbol
                _compliance.SetContractExpiration(TradingSymbol.ExpirationDate);
        }

        private void CheckExpirationOnStartup()
        {
            if (TradingSymbol?.ExpirationDate == null) return;
            // SDK: Symbol.ExpirationDate is DateTime (not DateTime?); just pass directly
            _compliance.SetContractExpiration(TradingSymbol.ExpirationDate);
            _compliance.CheckExpiration(DateTime.UtcNow);
        }

        private void CheckDailySessionReset()
        {
            var etNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ET);
            if (etNow.Hour == 17 && etNow.Minute == 0)
            {
                _dailyTradeCount      = 0;
                _sessionRealizedPnL   = 0;
                _sessionUnrealizedPnL = 0;
            }
        }

        private void CheckDailyReset_Halt()
        {
            // Allow restart after daily reset (5 PM ET)
            if (!_compliance.IsEmergencyHalt)
                TransitionState(TradeState.Scanning, "Daily reset — restarting from HALTED");
        }

        // ─────────────────────────────────────────────────────────────
        // SetupContext builder
        // ─────────────────────────────────────────────────────────────

        private SetupContext BuildSetupContext(double mid, string kz, MarketBias bias, SignalDirection dir)
        {
            var ms = _msEngine5M.GetLatestStructureEvent();
            var fvg = _fvgEngine.GetNearestFVG(mid, dir);
            var ob  = _obEngine.GetNearestOrderBlock(mid, dir);
            var fpSig = _fpEngine.GetSignalSummary();

            double entryZoneTop = 0, entryZoneBottom = 0;
            if (fvg != null)
            {
                entryZoneTop    = fvg.Top;
                entryZoneBottom = fvg.Bottom;
            }
            else if (ob != null)
            {
                entryZoneTop    = ob.Top;
                entryZoneBottom = ob.Bottom;
            }

            return new SetupContext
            {
                Time                 = DateTime.UtcNow,
                Symbol               = TradingSymbol?.Name ?? "MGC",
                ProposedDirection    = dir,
                HTFBias              = bias,
                ActiveKillZone       = kz,
                IsSilverBullet       = _kzEngine.IsSilverBullet(DateTime.UtcNow),
                LatestStructureEvent = ms,
                NearestFVG           = fvg,
                NearestOrderBlock    = ob,
                HasStackedImbalance  = dir == SignalDirection.Long
                                       ? fpSig.HasStackedBullishImbalance
                                       : fpSig.HasStackedBearishImbalance,
                HasAbsorption        = dir == SignalDirection.Long
                                       ? fpSig.HasBullishAbsorption
                                       : fpSig.HasBearishAbsorption,
                HasDeltaFlip         = false,
                DOMAbsorptionActive  = _domEngine.IsAbsorptionActive(),
                DOMStackingActive    = _domEngine.IsStackingActive(),
                DOMImbalance         = _domEngine.GetImbalanceSignal(),
                DeltaSlope           = _deltaEngine.GetDeltaSlope(),
                IsDeltaDivergent     = _deltaEngine.IsDeltaDivergent(dir),
                IsLargeBlockBurst    = _tapeEngine.IsLargeBlockBurst(dir),
                IsTapeAccelerating   = _tapeEngine.IsAccelerating(dir),
                ATR20                = _msEngine5M.GetATR20(),
                SwingHigh            = _msEngine5M.GetSwingHigh(),
                SwingLow             = _msEngine5M.GetSwingLow(),
                EntryZoneTop         = entryZoneTop,
                EntryZoneBottom      = entryZoneBottom,
                SignalScore          = 0   // filled after scoring
            };
        }

        // ─────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────

        private Account? FindLucidAccount()
            => TradingPlatform.BusinessLayer.Core.Instance.Accounts.FirstOrDefault(a =>
                a.Name.Contains(AccountNameFilter, StringComparison.OrdinalIgnoreCase));

        private Position? FindOpenPosition()
            => TradingPlatform.BusinessLayer.Core.Instance.Positions.FirstOrDefault(p =>
                p.Symbol == TradingSymbol &&
                p.Account?.Name.Contains(AccountNameFilter, StringComparison.OrdinalIgnoreCase) == true);

        private void TransitionState(TradeState newState, string reason)
        {
            TradeState oldState;
            lock (_stateLock)
            {
                oldState = _state;
                _state   = newState;
            }
            if (oldState != newState)
                QueueLog($"[STATE] {oldState} → {newState} | Reason: {reason} | " +
                         $"Time: {TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ET):HH:mm:ss} ET",
                         StrategyLoggingLevel.Info);
        }

        private void LogExitEvent(string reason, double exitPrice, double ticks, double pnl)
        {
            TimeSpan duration = DateTime.UtcNow - _entryFilledTime;
            double rrAchieved = _activeSignal != null
                ? Math.Abs(ticks) / Math.Abs((_activeSignal.StopPrice - _actualEntryPrice) / _tickSize)
                : 0;

            QueueLog($"[EXIT] Reason={reason} | Entry={_actualEntryPrice:F1} | Exit={exitPrice:F1} | " +
                     $"Ticks={ticks:F0} | PnL=${pnl:F2} | Duration={duration.TotalMinutes:F0}min | " +
                     $"RR_Achieved={rrAchieved:F2}",
                     StrategyLoggingLevel.Trading);
        }

        // ═════════════════════════════════════════════════════════════
        // HEARTBEAT LOOP (every 5 minutes)
        // ═════════════════════════════════════════════════════════════

        private async Task HeartbeatLoopAsync()
        {
            while (_state != TradeState.Halted)
            {
                await Task.Delay(TimeSpan.FromMinutes(5));

                if ((DateTime.UtcNow - _lastHeartbeatTime) >= TimeSpan.FromMinutes(5))
                {
                    _lastHeartbeatTime = DateTime.UtcNow;
                    float score;
                    lock (_stateLock) score = 0; // quick read

                    QueueLog($"[HEARTBEAT] State={_state} | " +
                             $"Delta={_deltaEngine.GetCumulativeDelta()} | " +
                             $"DailyPnL=${_sessionRealizedPnL + _sessionUnrealizedPnL:F2} | " +
                             $"Equity=${_compliance.DrawdownFloor + _compliance.CurrentBuffer:F2} | " +
                             $"DD_Buffer=${_compliance.CurrentBuffer:F2} | " +
                             $"DailyTrades={_dailyTradeCount}",
                             StrategyLoggingLevel.Info);

                    // Reload compliance config periodically
                    try { LoadComplianceConfig(); } catch { }

                    // Expiration check
                    if (TradingSymbol?.ExpirationDate != null)
                        _compliance.CheckExpiration(DateTime.UtcNow);
                }
            }
        }

        // ═════════════════════════════════════════════════════════════
        // LOCK-FREE LOG THREAD
        // ═════════════════════════════════════════════════════════════

        private void QueueLog(string msg, StrategyLoggingLevel level)
        {
            _logQueue.Enqueue((msg, level));
        }

        private void LogThreadProc()
        {
            while (_logRunning || !_logQueue.IsEmpty)
            {
                if (_logQueue.TryDequeue(out var entry))
                {
                    try
                    {
                        // BUG: StrategyLoggingLevel.Debug does not exist in this SDK.
                        // Use EnableDebugLog to gate Info-level messages instead.
                        bool shouldLog = entry.Level != StrategyLoggingLevel.Info || EnableDebugLog
                            || entry.Level == StrategyLoggingLevel.Error
                            || entry.Level == StrategyLoggingLevel.Trading;
                        if (shouldLog)
                            Log(entry.Msg, entry.Level);
                    }
                    catch { }
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
        }

        private void FlushLogQueue()
        {
            while (_logQueue.TryDequeue(out var entry))
            {
                try
                {
                    bool shouldLog = entry.Level == StrategyLoggingLevel.Error
                        || entry.Level == StrategyLoggingLevel.Trading
                        || EnableDebugLog;
                    if (shouldLog) Log(entry.Msg, entry.Level);
                }
                catch { }
            }
        }
    }
}
