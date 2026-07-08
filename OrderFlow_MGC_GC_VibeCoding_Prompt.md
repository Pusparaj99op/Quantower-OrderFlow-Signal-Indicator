# 🔱 VIBE-CODING PROMPT
## Quantower Order Flow Indicator — MGC / GC | Lucid Flex 25K
**Platform:** Quantower (C# .NET, Quantower Algo SDK)
**Data Feed:** Rithmic (CME — COMEX Gold Futures)
**Account:** Lucid Trading Flex 25K Evaluation
**Version:** Production-Grade v1.0

---

## 🎯 OBJECTIVE

Build a **production-grade Quantower Indicator** (registered as `IIndicator`) for **MGC (Micro Gold Futures)** and **GC (Full Gold Futures)** that performs intelligent, real-time **Order Flow Analysis** directly on the price chart. It must visually plot order flow signals on the chart panel AND autonomously submit, manage, and close trades through the Quantower Algo Strategy interface — operating fully within Lucid Trading Flex 25K evaluation rules.

**Hard Constraints:**
- Maximum Risk per Trade: **50 ticks** (MGC = $50 | GC = $500)
- Minimum Profit Target: **100 ticks** (MGC = $100 | GC = $1,000) — minimum 1:2 R:R
- The indicator is the brain; it draws signals, scores confluence, and fires the strategy engine
- Must never violate Lucid Flex trailing drawdown or daily loss limits
- Must run live on Rithmic feed — no simulated or synthetic data

---

## 📁 PROJECT STRUCTURE

```
/LucidGoldOrderFlow/
├── Indicators/
│   ├── OrderFlowIndicator.cs          ← Main indicator (chart overlay)
│   ├── DeltaProfileEngine.cs          ← Footprint delta calculations
│   ├── VolumeProfileEngine.cs         ← Session/range volume profile
│   ├── DomImbalanceEngine.cs          ← Level 2 DOM analysis
│   ├── CvdEngine.cs                   ← Cumulative Volume Delta tracker
│   └── SignalScorer.cs                ← Multi-factor confluence scoring
├── Strategy/
│   ├── OrderFlowStrategy.cs           ← Execution strategy (IStrategy)
│   ├── RiskEngine.cs                  ← Position sizing + SL/TP enforcement
│   ├── LucidComplianceGuard.cs        ← Prop firm rule enforcement
│   └── TradeStateMachine.cs           ← Trade lifecycle FSM
├── Models/
│   ├── OrderFlowSignal.cs             ← Signal DTO
│   ├── TradeSetup.cs                  ← Full trade blueprint
│   ├── FootprintBar.cs                ← Per-bar footprint data
│   └── DomSnapshot.cs                 ← DOM state capture
├── Config/
│   └── StrategyParameters.cs          ← All user-configurable inputs
└── Utils/
    ├── SessionTimeHelper.cs           ← Kill zone & session time logic
    ├── NewsBlackoutManager.cs         ← Pre/post news pause logic
    └── Logger.cs                      ← Structured file + console logging
```

---

## 🧠 FULL SPECIFICATION

### SECTION 1 — INDICATOR ARCHITECTURE (`OrderFlowIndicator.cs`)

Implement as a Quantower `Indicator` class targeting the **Chart panel**. This is the primary file the AI must generate first. It:

1. Subscribes to `History` for bar-by-bar footprint reconstruction
2. Subscribes to `Symbol.NewTrade` for tick-level trade aggression tracking
3. Subscribes to `Symbol.Level2Updated` for real-time DOM depth snapshots
4. Subscribes to `Symbol.NewQuote` for best bid/ask spread monitoring
5. Computes all order flow signals per bar and in real-time
6. Renders visual overlays directly on the chart canvas
7. Exposes a `public OrderFlowSignal LatestSignal { get; private set; }` property consumed by `OrderFlowStrategy`

**Chart Visuals to render:**

```
A) Delta Bars (sub-chart pane below price):
   - Positive delta = upward bar, color: #00BFFF (electric blue)
   - Negative delta = downward bar, color: #FF4444 (red)
   - Cumulative delta line overlay on price pane in #FFD700 (gold)

B) DOM Imbalance Arrows (on price pane):
   - Strong bid stacking detected → green triangle UP at bar low
   - Strong ask stacking detected → red triangle DOWN at bar high
   - Size = imbalance ratio (larger arrow = stronger imbalance)

C) Absorption Zones (horizontal shaded bands):
   - Detected absorption at swing high → red semi-transparent band
   - Detected absorption at swing low → green semi-transparent band

D) Signal Labels (on candle):
   - LONG signal → "▲ OFL" label below bar in gold (#FFD700)
   - SHORT signal → "▼ OFS" label above bar in red (#FF4444)
   - Score shown as sub-label: e.g., "Score: 82"

E) Volume Profile (right side of visible chart window):
   - Session VPOC line in dashed white
   - VAH / VAL lines in dashed grey
   - POC label in text

F) CVD Line (separate sub-pane):
   - Cumulative Volume Delta painted as a line
   - Color changes based on slope: rising = #00FF87, falling = #FF4444
   - Zero line in grey
   - Divergence markers: if CVD declining while price rising → orange dot
```

---

### SECTION 2 — ORDER FLOW SIGNAL ENGINE

#### 2A. Delta Analysis (`DeltaProfileEngine.cs`)

Reconstruct footprint data per bar from `NewTrade` tick events.

For each tick:
```csharp
if (trade.AggressorFlag == AggressorFlag.Buy)
    buyVolumeAtPrice[trade.Price] += trade.Volume;
else
    sellVolumeAtPrice[trade.Price] += trade.Volume;
```

Compute per bar:
- `BarDelta = TotalBuyVolume - TotalSellVolume`
- `MaxDeltaPrice`: price level with the highest absolute delta in the bar
- `DeltaExhaustion`: if last 3 bars all show declining delta in trend direction → exhaustion flag
- `StackedImbalance`: if 3+ consecutive price levels in a bar have delta imbalance > 3:1 same direction

Stacked imbalance detection logic:
```
For each pair of adjacent price levels in footprint:
   ratio = biggerSide / smallerSide
   if ratio >= 3.0 AND smallerSide > minVolumeThreshold:
       imbalanceCount++
       direction = dominant side
if imbalanceCount >= 3 consecutively:
    → StackedImbalanceSignal(direction, startPrice, endPrice)
```

#### 2B. DOM Imbalance Engine (`DomImbalanceEngine.cs`)

On each `Level2Updated` event, snapshot the full DOM and compute:

**DOM Ratio at Best Bid/Ask:**
```
bidWallVolume = sum of top 3 bid levels
askWallVolume = sum of top 3 ask levels
domRatio = bidWallVolume / askWallVolume
```
- If `domRatio > 3.0` → BID_WALL signal (bullish)
- If `domRatio < 0.33` → ASK_WALL signal (bearish)

**Absorption Detection:**
```
If price is at a price level with a large limit order AND:
   - Aggressive orders are hitting that level
   - Price is NOT moving through it after N prints
   → AbsorptionSignal(side=passive, price=level, volume=passiveSize)
```

**Pulling Detection:**
```
If a large limit order (>= pullThreshold contracts) disappears from DOM
without trading prints at that level:
   → PullingSignal(side, price, volumeRemoved)
```

Track DOM history with a `CircularBuffer<DomSnapshot>(capacity: 100)` — never use unbounded List.

#### 2C. CVD Engine (`CvdEngine.cs`)

Maintain rolling CVD:
```csharp
cvd += (trade.AggressorFlag == AggressorFlag.Buy) ? trade.Volume : -trade.Volume;
```

Compute:
- `CvdSlope`: linear regression slope over last 20 ticks
- `CvdDivergence`: if price making higher highs but CVD making lower highs (or vice versa)
- `CvdMomentum`: rate of change of CVD over last 10 ticks vs previous 10 ticks

#### 2D. Volume Profile Engine (`VolumeProfileEngine.cs`)

Build a session-rolling volume profile:
- Reset at 6:00 PM ET (CME gold session open)
- Compute VPOC, VAH, VAL in real-time
- Identify HVN clusters (price levels with volume > 1.5× average)
- Identify LVN gaps (price ranges with volume < 0.5× average)
- Store as `SortedDictionary<decimal, long> sessionProfile`

---

### SECTION 3 — SIGNAL SCORER (`SignalScorer.cs`)

Implements a **multi-factor confluence scoring system** (0–100 points). A signal fires only if score ≥ **65 points**.

```
LONG SIGNAL FACTORS:
┌─────────────────────────────────────────────────────────────┐
│ Factor                              │ Max Points │ Required │
├─────────────────────────────────────┼────────────┼──────────┤
│ Positive bar delta (last bar)       │    10      │   No     │
│ CVD rising (positive slope)         │    10      │   No     │
│ CVD-price bullish divergence        │    15      │   No     │
│ DOM bid wall present                │    15      │   No     │
│ Absorption at swing low             │    15      │   No     │
│ Stacked imbalance bullish           │    15      │   No     │
│ Price above session VPOC            │     5      │   No     │
│ Price at/near VAL (support)         │    10      │   No     │
│ Price inside LVN (low resistance)   │     5      │   No     │
│ Kill zone active (London/NY open)   │    10      │   No     │
│ HTF trend bullish (H1 / H4 EMA)     │    10      │   No     │  ← bonus
│ News blackout NOT active            │    Req.    │   YES    │
│ Score threshold (fire trade)        │    ≥65     │   YES    │
└─────────────────────────────────────┴────────────┴──────────┘

SHORT SIGNAL FACTORS: (mirror of above, inverted)
```

Return a `OrderFlowSignal` object:
```csharp
public class OrderFlowSignal
{
    public SignalDirection Direction;     // Long | Short | None
    public int Score;                     // 0–100
    public decimal EntryPrice;            // Suggested limit entry
    public decimal StopLoss;              // 50 ticks from entry
    public decimal TakeProfit1;           // 100 ticks (1:2 R:R minimum)
    public decimal TakeProfit2;           // 150 ticks (optional scale-out)
    public decimal TakeProfit3;           // 200 ticks (runner)
    public DateTime SignalTime;
    public List<string> ConfluenceFactors; // Human-readable reason list
    public bool IsHighConfidence;          // Score >= 80
}
```

---

### SECTION 4 — EXECUTION STRATEGY (`OrderFlowStrategy.cs`)

Implements `IStrategy` in Quantower. Receives `OrderFlowSignal` from the indicator (shared via static event or dependency injection). Handles all order submission and management.

**Entry Logic:**
```
1. Check LucidComplianceGuard.CanTrade() → if false, skip
2. Read LatestSignal from OrderFlowIndicator
3. If signal.Score >= 65 AND no open position AND in kill zone:
   a. Calculate position size from RiskEngine
   b. Submit LIMIT entry order at signal.EntryPrice
   c. On fill → submit STOP LOSS order at signal.StopLoss (ALWAYS first)
   d. Submit LIMIT take profit at signal.TakeProfit1 (TP1)
   e. Log entry with full signal details
4. If signal.Score >= 80 (high confidence):
   a. Enter with 2 contracts (subject to Lucid limits)
   b. Scale out 1 at TP1, trail runner to TP2/TP3
```

**Trade Management (TradeStateMachine.cs):**

States: `IDLE → PENDING_ENTRY → IN_TRADE → SCALING_OUT → TRAILING → CLOSED`

```
STATE: IN_TRADE
   - Monitor CVD for reversal signal
   - If CVD reverses sharply (slope flips) AND price moves against: move SL to BE
   - At TP1 hit: close 50% of position, move SL to breakeven
   - At TP2 hit: close additional 25%, trail remaining to TP3
   - If new opposing signal fires (score >= 70): close entire position

STATE: TRAILING
   - Trail stop every 10 ticks in profit direction
   - Never let trail go below breakeven once TP1 is achieved
```

---

### SECTION 5 — RISK ENGINE (`RiskEngine.cs`)

```csharp
public class RiskEngine
{
    // Contract specs
    const decimal MGC_TICK_VALUE = 1.00m;   // $1 per 0.1 pt tick
    const decimal GC_TICK_VALUE  = 10.00m;  // $10 per 0.1 pt tick
    const int MAX_RISK_TICKS = 50;
    const int MIN_REWARD_TICKS = 100;

    public int CalculateContracts(decimal accountEquity, decimal maxRiskDollars)
    {
        // Risk per contract = 50 ticks × tick value
        // For MGC: 50 × $1 = $50/contract
        // For GC:  50 × $10 = $500/contract
        decimal riskPerContract = MAX_RISK_TICKS * TickValue(instrument);
        int contracts = (int)Math.Floor(maxRiskDollars / riskPerContract);
        return Math.Max(1, Math.Min(contracts, LucidMaxContracts));
    }

    public decimal CalculateStopLoss(decimal entryPrice, SignalDirection dir, string symbol)
    {
        decimal tickSize = GetTickSize(symbol); // 0.1 for MGC and GC
        return dir == Long
            ? entryPrice - (MAX_RISK_TICKS * tickSize)
            : entryPrice + (MAX_RISK_TICKS * tickSize);
    }

    public decimal CalculateTP1(decimal entryPrice, SignalDirection dir, string symbol)
    {
        decimal tickSize = GetTickSize(symbol);
        return dir == Long
            ? entryPrice + (MIN_REWARD_TICKS * tickSize)    // +10.0 points
            : entryPrice - (MIN_REWARD_TICKS * tickSize);
    }
}
```

**Position Sizing Rule:**
- Default: risk 1% of current account balance per trade
- High-confidence signal (score ≥ 80): up to 2% risk
- Never exceed Lucid's max contract limit for the account tier
- If in drawdown > 40% of daily limit: drop to 0.5% risk

---

### SECTION 6 — LUCID COMPLIANCE GUARD (`LucidComplianceGuard.cs`)

```csharp
public class LucidComplianceGuard
{
    // ⚠️ THESE VALUES MUST BE VERIFIED FROM OFFICIAL LUCID DOCS
    // before each deployment — they change. Do not hardcode in prod.
    private decimal _trailingDrawdownLimit;   // load from config
    private decimal _dailyLossLimit;          // load from config
    private int _maxContractsMGC;             // load from config
    private int _maxContractsGC;              // load from config

    public bool CanTrade(IAccount account, string symbol)
    {
        if (IsNewsBlackout()) return false;
        if (IsWeekend()) return false;
        if (!IsKillZoneActive()) return false;
        if (DailyPnL() <= -_dailyLossLimit) return false;
        if (CurrentEquity() <= TrailingDDFloor()) return false;
        if (CurrentPositions() >= MaxContracts(symbol)) return false;
        if (IsRolloverWeek(symbol)) return false;
        return true;
    }

    private bool IsNewsBlackout()
    {
        // Check economic calendar — pause 3 min before, 2 min after
        // High-impact events for gold: NFP, CPI, FOMC, Fed Chair, PPI
        return NewsBlackoutManager.IsBlackedOut(DateTime.UtcNow);
    }

    private bool IsKillZoneActive()
    {
        // London Open: 3:00 AM – 5:00 AM ET
        // New York Open: 9:30 AM – 11:00 AM ET
        // London Close: 11:00 AM – 12:00 PM ET
        var et = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ETZone);
        var t = et.TimeOfDay;
        return (t >= new TimeSpan(3,0,0) && t <= new TimeSpan(5,0,0))
            || (t >= new TimeSpan(9,30,0) && t <= new TimeSpan(12,0,0));
    }

    public bool IsApproachingDDLimit(IAccount account)
    {
        // Warning threshold: 70% of drawdown consumed
        decimal consumed = StartingEquity - CurrentEquity();
        return consumed >= (_trailingDrawdownLimit * 0.70m);
    }

    private decimal TrailingDDFloor()
    {
        // Trailing DD tracks the highest intraday equity
        // Floor = HighWaterMark - TrailingDrawdownLimit
        return _highWaterMark - _trailingDrawdownLimit;
    }
}
```

---

### SECTION 7 — STRATEGY PARAMETERS (`StrategyParameters.cs`)

Expose all these as Quantower Strategy Parameters (visible in the strategy panel UI):

```csharp
[InputParameter("Instrument", 0)]
public string InstrumentSymbol = "MGC";   // or "GC"

[InputParameter("Signal Score Threshold", 1)]
public int SignalScoreThreshold = 65;      // Min: 60, Max: 90

[InputParameter("Max Risk Ticks", 2)]
public int MaxRiskTicks = 50;             // Fixed per spec — don't go above

[InputParameter("TP1 Ticks", 3)]
public int TP1Ticks = 100;               // Minimum 100

[InputParameter("TP2 Ticks", 4)]
public int TP2Ticks = 150;

[InputParameter("TP3 Ticks (Runner)", 5)]
public int TP3Ticks = 200;

[InputParameter("Account Risk % Per Trade", 6)]
public double RiskPercent = 1.0;          // 1% per trade

[InputParameter("DOM Imbalance Ratio Threshold", 7)]
public double DomImbalanceRatio = 3.0;    // 3:1 DOM imbalance to fire

[InputParameter("Min Absorption Prints", 8)]
public int MinAbsorptionPrints = 25;      // Prints at level before absorption confirmed

[InputParameter("CVD Slope Lookback (ticks)", 9)]
public int CvdSlopeLookback = 20;

[InputParameter("Stacked Imbalance Min Levels", 10)]
public int StackedImbalanceMinLevels = 3;

[InputParameter("Stacked Imbalance Ratio", 11)]
public double StackedImbalanceRatio = 3.0;

[InputParameter("Enable Scale-Out at TP1", 12)]
public bool EnableScaleOut = true;

[InputParameter("Scale-Out TP1 Percent", 13)]
public int ScaleOutTP1Percent = 50;       // Close 50% at TP1

[InputParameter("Kill Zone Only", 14)]
public bool KillZoneOnly = true;          // Only trade during London/NY opens

[InputParameter("News Blackout Minutes Before", 15)]
public int NewsBlackoutBefore = 3;

[InputParameter("News Blackout Minutes After", 16)]
public int NewsBlackoutAfter = 2;

[InputParameter("Enable Trailing Stop", 17)]
public bool EnableTrailingStop = true;

[InputParameter("Trailing Stop Step Ticks", 18)]
public int TrailingStepTicks = 10;

[InputParameter("HTF Bias Timeframe (minutes)", 19)]
public int HtfBiasTimeframe = 60;         // H1 for HTF filter

[InputParameter("HTF EMA Period", 20)]
public int HtfEmaPeriod = 21;

[InputParameter("Enable Auto-Trading", 21)]
public bool EnableAutoTrading = false;    // Manual override — OFF by default
```

---

### SECTION 8 — VISUAL RENDERING SPEC

Implement `OnPaintChart(PaintChartEventArgs args)` in the indicator.

**Render Order (back to front):**
1. Volume Profile histogram (right-anchored, 15% chart width, semi-transparent)
2. VPOC horizontal line (full width, dashed white, 1px)
3. VAH / VAL lines (dashed grey, 1px)
4. Absorption zones (semi-transparent filled rectangles spanning N bars)
5. CVD line (sub-pane below main chart)
6. Delta histogram (separate sub-pane below CVD)
7. DOM imbalance arrows (overlaid on price bars)
8. Signal labels with score (above/below signal bar)
9. Active trade info box (top-right corner): Entry, SL, TP1, Current P&L, Ticks P&L

**Info Panel (top-right overlay box):**
```
┌────────────────────────────────────┐
│ 🔱 LUCID GOLD ORDER FLOW          │
│ Symbol:  MGC  |  Score: 78        │
│ Signal:  ▲ LONG                   │
│ Entry:   2348.5  SL: 2343.5       │
│ TP1:     2358.5  TP2: 2363.5      │
│ CVD:     ↑ +1,240  Slope: +12.4  │
│ DOM:     BID 4.2:1                │
│ Session: NY OPEN KILL ZONE ✅     │
│ Lucid:   DD Buffer: $1,847 ✅     │
└────────────────────────────────────┘
```
Color scheme: Dark background `#0D0D0D`, gold text `#FFD700`, status green `#00FF87`, warning red `#FF4444`.

---

### SECTION 9 — PERFORMANCE REQUIREMENTS

**All hot-path code must adhere to these rules:**

1. `OnNewTrade` handler: must complete in **< 1ms** — no LINQ, no allocation
2. `Level2Updated` handler: must complete in **< 2ms** — use pre-allocated arrays
3. Use `CircularBuffer<T>` for all tick and DOM history — never `List<T>` on the hot path
4. Pre-allocate all render brushes and pens in `Initialize()` — never create GDI objects in `OnPaintChart`
5. No `Thread.Sleep()` anywhere — use `System.Timers.Timer` or Quantower's scheduler for deferred work
6. Logging must be async — fire-and-forget to a `BlockingCollection<string>` consumed by a background thread
7. All shared state accessed from both the UI thread and event threads must use `Interlocked` or `lock` — never bare field access

---

### SECTION 10 — TESTING AND VALIDATION

Generate a `TestHarness.cs` stub with:

```csharp
// Unit test scenarios to implement:
// 1. Stacked imbalance detection: inject 5 consecutive tick events with 4:1 delta → expect StackedImbalanceSignal
// 2. Absorption detection: 50 tick prints at same price level with DOM large limit unchanged → expect AbsorptionSignal
// 3. DOM ratio: inject DOM with bidVol=1000, askVol=200 → expect BID_WALL signal
// 4. CVD divergence: inject rising price + falling CVD over 20 ticks → expect DivergenceSignal
// 5. LucidComplianceGuard: set equity to below DD floor → expect CanTrade() = false
// 6. Risk engine: 25K account, 1% risk, MGC → expect 5 contracts ($50 risk)
// 7. Kill zone: inject 4:00 AM ET → expect IsKillZoneActive() = true
// 8. Kill zone: inject 2:00 PM ET → expect IsKillZoneActive() = false
// 9. News blackout: inject event 2 min from now → expect IsNewsBlackout() = true
// 10. Score threshold: build signal with all factors → verify total ≤ 100
```

---

### SECTION 11 — INSTRUMENT SPECS (DO NOT DEVIATE)

```
MGC — Micro Gold Futures
  Exchange:      COMEX (CME Group)
  Tick Size:     0.10 points
  Tick Value:    $1.00 USD
  50-tick SL:    $50.00 (5.0 points against entry)
  100-tick TP:   $100.00 (10.0 points from entry)
  Contract Size: 10 troy ounces
  Symbol (Rithmic): "MGC" + front month code

GC — Full Gold Futures
  Exchange:      COMEX (CME Group)
  Tick Size:     0.10 points
  Tick Value:    $10.00 USD
  50-tick SL:    $500.00 (5.0 points against entry)
  100-tick TP:   $1,000.00 (10.0 points from entry)
  Contract Size: 100 troy ounces
  Symbol (Rithmic): "GC" + front month code
```

---

### SECTION 12 — LUCID FLEX 25K ACCOUNT CONSTRAINTS

⚠️ **ALWAYS verify these against the official Lucid Trading docs before deploying to live evaluation. Rules change without notice.**

Expected constraints (verify current values):
- **Trailing Drawdown**: ~$1,500 trailing (locks at funded threshold — verify)
- **Profit Target**: ~$1,500+ (verify exact current figure)
- **Daily Loss Limit**: verify current figure
- **Max Contracts MGC**: typically 50 micro = 5 full equivalents — verify
- **Consistency Rule**: No single trading day > 40–50% of total profit — verify
- **News Restriction**: Verify if Lucid prohibits holding through designated news events
- **Weekend Rule**: All positions must be flat before CME weekend close — verify

Load all these values from a JSON config file (`lucid_config.json`) that can be updated without recompiling:
```json
{
  "accountSize": 25000,
  "trailingDrawdown": 1500,
  "profitTarget": 1500,
  "dailyLossLimit": 500,
  "maxContractsMGC": 50,
  "maxContractsGC": 5,
  "consistencyRulePct": 0.40,
  "newsRestrictedEvents": ["NFP", "FOMC", "CPI", "PPI", "FedChair"],
  "newsBlackoutMinsBefore": 3,
  "newsBlackoutMinsAfter": 2
}
```

---

### SECTION 13 — LOGGING SPEC

All log entries must follow this structured format:
```
[2025-01-15 09:32:44.221 ET] [SIGNAL] Direction=LONG Score=78 Entry=2348.5 SL=2343.5 TP1=2358.5 Factors=[CVD+,DOM_BID_WALL,ABSORPTION,STACKED_IMB] KillZone=NY_OPEN
[2025-01-15 09:32:44.890 ET] [ORDER]  Type=LIMIT Side=BUY Qty=3 Price=2348.5 Status=SUBMITTED OrderId=abc123
[2025-01-15 09:32:45.102 ET] [FILL]   OrderId=abc123 FillPrice=2348.5 FillQty=3 Slippage=0 ticks
[2025-01-15 09:32:45.110 ET] [ORDER]  Type=STOP Side=SELL Qty=3 Price=2343.5 Status=WORKING (SL placed)
[2025-01-15 09:32:45.115 ET] [ORDER]  Type=LIMIT Side=SELL Qty=3 Price=2358.5 Status=WORKING (TP1 placed)
[2025-01-15 09:45:12.001 ET] [FILL]   TP1 hit. Closed 2/3 at 2358.5. PnL=+$200.00 (+100 ticks). Moving SL to BE.
[2025-01-15 09:45:12.005 ET] [STATE]  TradeState=TRAILING BreakevenSet=true RemainingQty=1
[2025-01-15 09:58:33.441 ET] [FILL]   TP2 hit. Closed 1/1 at 2363.5. PnL=+$150.00 (+150 ticks). Trade closed.
[2025-01-15 09:58:33.444 ET] [STATE]  TradeState=IDLE DailyPnL=+$350.00 DDBuffer=$2,197
```

Log to file: `C:\QuantowerData\Logs\LucidGoldOF_YYYYMMDD.log`

---

## 🚀 BUILD ORDER (Give the AI this sequence)

**Step 1:** Generate `Models/` directory — all DTOs and data structures first
**Step 2:** Generate `Utils/SessionTimeHelper.cs` and `Utils/Logger.cs`
**Step 3:** Generate `Utils/NewsBlackoutManager.cs` with sample economic event calendar logic
**Step 4:** Generate `Config/StrategyParameters.cs` with all Quantower InputParameter attributes
**Step 5:** Generate `Indicators/CvdEngine.cs`
**Step 6:** Generate `Indicators/DeltaProfileEngine.cs`
**Step 7:** Generate `Indicators/DomImbalanceEngine.cs`
**Step 8:** Generate `Indicators/VolumeProfileEngine.cs`
**Step 9:** Generate `Indicators/SignalScorer.cs`
**Step 10:** Generate `Strategy/RiskEngine.cs`
**Step 11:** Generate `Strategy/LucidComplianceGuard.cs` — load from `lucid_config.json`
**Step 12:** Generate `Strategy/TradeStateMachine.cs`
**Step 13:** Generate `Strategy/OrderFlowStrategy.cs`
**Step 14:** Generate `Indicators/OrderFlowIndicator.cs` (largest file — ties everything together)
**Step 15:** Generate `TestHarness.cs` — unit test stubs

After each file, ask the AI: **"Verify this file compiles against Quantower SDK. List any SDK methods that need verification."**

---

## ⚡ CURSOR / ANTIGRAVITY RULES

When running this prompt in Cursor or Antigravity, enforce these coding rules:

```
RULES:
- Language: C# 10+, .NET 6 targeting Windows
- Framework: Quantower Algo SDK (reference from installed Quantower directory)
- No NuGet packages beyond Quantower SDK — use only BCL + SDK
- All event handlers must be idempotent
- Never use async/await on SDK callbacks — Quantower events are synchronous by design
- Never call Thread.Sleep() or block any SDK callback
- All magic numbers go into StrategyParameters or config — zero magic numbers in logic code
- Every method over 20 lines gets an XML doc comment
- Every public property gets a brief comment
- Use expression-bodied members where clarity is not sacrificed
- Follow naming: private fields = _camelCase, public = PascalCase, constants = UPPER_SNAKE
- Use nullable reference types — #nullable enable at top of every file
- Every try/catch must log the exception with full stack trace via Logger.cs
```

---

## 📌 FINAL NOTES FOR THE AI

1. The indicator file (`OrderFlowIndicator.cs`) **must not submit any orders itself** — it only computes and renders. All order submission goes through `OrderFlowStrategy.cs`.

2. The indicator and strategy communicate via a **static shared event bus**:
   ```csharp
   public static event Action<OrderFlowSignal> OnSignalGenerated;
   ```
   Strategy subscribes to this event on Initialize() and unsubscribes on Dispose().

3. The `EnableAutoTrading` parameter defaults to **false**. When false, the indicator still renders all signals and scores on the chart for manual trading — the strategy engine is the optional execution layer.

4. For Lucid Flex accounts: **never place a trade that would consume the entire remaining DD buffer in a single loss**. If remaining DD buffer < (MaxRiskTicks × TickValue × Contracts × 2), reduce to 1 contract minimum.

5. All SL orders must be **bracket orders** (stop-limit or stop-market depending on Rithmic availability) and must be confirmed working in the order book within 500ms of entry fill. If not confirmed, execute emergency market close immediately.

6. This system is designed for **live evaluation accounts** — not backtesting. While a backtest method stub should exist, the primary value is live real-time execution fidelity.
```

---

*Generated for: Pranay Gajbhiye | BlackObsidian (AMC) / Zorvainstreet | Lucid Gold Order Flow System v1.0*
