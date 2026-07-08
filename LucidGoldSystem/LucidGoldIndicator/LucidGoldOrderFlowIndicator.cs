// ============================================================
// LucidGoldOrderFlowIndicator.cs
// LucidGold.Indicator namespace
// Quantower IIndicator — display-only, no order placement
// ============================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TradingPlatform.BusinessLayer;
using LucidGold.Core.Engines;
using LucidGold.Core.Enums;
using LucidGold.Core.Models;
using LucidGold.Core.RiskManagement;
using CoreBar = LucidGold.Core.Models.Bar;
using AggressorSide = LucidGold.Core.Enums.AggressorSide;

namespace LucidGold.Indicator
{
    /// <summary>
    /// Lucid Gold Order Flow Signal Indicator.
    /// Renders all ICT/SMC/order-flow overlays on a price chart.
    /// Operates in DISPLAY-ONLY mode — places no orders.
    ///
    /// <para>Rendered overlays:</para>
    /// <list type="bullet">
    ///   <item>Fair Value Gap (FVG) shaded boxes</item>
    ///   <item>Order Block (OB) outlined boxes with label</item>
    ///   <item>BOS / CHoCH / MSS horizontal lines and markers</item>
    ///   <item>Kill Zone background tinting</item>
    ///   <item>Previous session POC / VAH / VAL dashed lines</item>
    ///   <item>VWAP purple line (session rolling)</item>
    ///   <item>PDH / PDL dashed lines</item>
    ///   <item>Signal score badge (top-right)</item>
    ///   <item>Entry arrow with score label</item>
    ///   <item>Cumulative delta sub-pane (bar chart + line)</item>
    ///   <item>DOM imbalance corner indicator</item>
    /// </list>
    /// </summary>
    public class LucidGoldOrderFlowIndicator : TradingPlatform.BusinessLayer.Indicator
    {
        // ─────────────────────────────────────────────────────────────
        // Input Parameters
        // ─────────────────────────────────────────────────────────────

        [InputParameter("Large Lot Threshold (MGC contracts)", 0, 1, 1, 200, 1)]
        public int LargeLotThresholdMGC { get; set; } = 50;

        [InputParameter("Large Lot Threshold (GC contracts)", 1, 1, 1, 50, 1)]
        public int LargeLotThresholdGC { get; set; } = 10;

        [InputParameter("Min Signal Score for Arrow", 2, 50, 50, 100, 1)]
        public int MinScoreForArrow { get; set; } = 65;

        [InputParameter("Enable London Kill Zone Tint", 3)]
        public bool EnableLondonTint { get; set; } = true;

        [InputParameter("Enable NY Kill Zone Tint", 4)]
        public bool EnableNYTint { get; set; } = true;

        [InputParameter("Show Delta Sub-Pane", 5)]
        public bool ShowDeltaPane { get; set; } = true;

        [InputParameter("Show DOM Imbalance Indicator", 6)]
        public bool ShowDOMIndicator { get; set; } = true;

        [InputParameter("Show FVG Boxes", 7)]
        public bool ShowFVGBoxes { get; set; } = true;

        [InputParameter("Show Order Block Boxes", 8)]
        public bool ShowOBBoxes { get; set; } = true;

        [InputParameter("Show BOS/CHoCH Lines", 9)]
        public bool ShowStructureLines { get; set; } = true;

        [InputParameter("Show POC/VAH/VAL Lines", 10)]
        public bool ShowVolumeProfileLines { get; set; } = true;

        [InputParameter("Show VWAP", 11)]
        public bool ShowVWAP { get; set; } = true;

        [InputParameter("Show PDH/PDL Lines", 12)]
        public bool ShowPDHL { get; set; } = true;

        // ─────────────────────────────────────────────────────────────
        // Engines
        // ─────────────────────────────────────────────────────────────

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

        // ─────────────────────────────────────────────────────────────
        // State
        // ─────────────────────────────────────────────────────────────

        private double _tickSize = 0.1;
        private long   _largeLotThreshold = 50;

        // VWAP tracking
        private double _vwapNumerator   = 0;
        private long   _vwapDenominator = 0;
        private double _vwapValue       = 0;
        private DateTime _vwapSessionStart = DateTime.MinValue;

        // PDH / PDL tracking
        private double _pdHigh     = 0;
        private double _pdLow      = double.MaxValue;
        private DateTime _prevDate = DateTime.MinValue;

        // Signal score for badge
        private float  _currentScore     = 0;
        private string _currentDirection = "—";
        private readonly object _scoreLock = new object();

        // Signal arrows to draw
        private readonly List<(int BarIndex, double Price, SignalDirection Dir, float Score)> _arrows
            = new List<(int, double, SignalDirection, float)>();

        // Bar index counter
        private int _totalBarCount = 0;

        // Sub-pane line series indices
        private int _cumDeltaSeriesIdx = -1;
        private int _barDeltaSeriesIdx = -1;

        // ─────────────────────────────────────────────────────────────
        // Constructor
        // ─────────────────────────────────────────────────────────────

        public LucidGoldOrderFlowIndicator()
        {
            Name        = "Lucid Gold Order Flow";
            Description = "ICT/SMC order-flow signal indicator for MGC/GC. Display only.";
            SeparateWindow = false;

            // Delta sub-pane
            AddLineSeries("BarDelta",  Color.DarkGreen, 2, LineStyle.Histogramm);
            AddLineSeries("CumDelta",  Color.Purple,    1, LineStyle.Solid);
        }

        // ─────────────────────────────────────────────────────────────
        // OnInit — allocate all engines
        // ─────────────────────────────────────────────────────────────

        protected override void OnInit()
        {
            _tickSize = Symbol?.TickSize ?? 0.1;
            bool isMGC = Symbol?.Name?.Contains("MGC") ?? true;
            _largeLotThreshold = isMGC ? LargeLotThresholdMGC : LargeLotThresholdGC;

            _msEngine5M  = new MarketStructureEngine();
            _msEngine15M = new MarketStructureEngine();
            _fvgEngine   = new FVGEngine(_tickSize);
            _obEngine    = new OrderBlockEngine(_tickSize);
            _htfEngine   = new HTFBiasEngine(msg => Log(msg, StrategyLoggingLevel.Info));
            _kzEngine    = new KillZoneEngine();
            _deltaEngine = new CumulativeDeltaEngine();
            _fpEngine    = new FootprintEngine(_tickSize);
            _domEngine   = new DOMAbsorptionEngine(_largeLotThreshold);
            _tapeEngine  = new TapeReadingEngine();
            _vpEngine    = new VolumeProfileEngine(_tickSize);
            _scoreEngine = new SignalScoringEngine();

            _barDeltaSeriesIdx = 0;
            _cumDeltaSeriesIdx = 1;

            // Subscribe to real-time data events (Indicator has no OnNewTrade/OnNewLevel2 overrides)
            if (Symbol != null)
            {
                Symbol.NewLast   += HandleNewLast;
                Symbol.NewLevel2 += HandleNewLevel2;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // OnUpdate — dispatch to engines per update reason
        // ─────────────────────────────────────────────────────────────

        protected override void OnUpdate(UpdateArgs args)
        {
            if (HistoricalData == null || HistoricalData.Count < 5) return;

            if (args.Reason == UpdateReason.NewBar || args.Reason == UpdateReason.HistoricalBar)
                ProcessBarClose(args);
            else if (args.Reason == UpdateReason.NewTick)
                ProcessTick();

            // Update score badge
            UpdateScoreBadge();

            // Update VWAP line series (index 0 = price)
            if (ShowVWAP && _vwapValue > 0)
            {
                // VWAP rendered in OnPaintChart; no series needed for main pane
            }

            // Update delta sub-pane lines
            if (ShowDeltaPane && _barDeltaSeriesIdx >= 0)
            {
                double barDelta = _deltaEngine.GetBarDelta();
                double cumDelta = _deltaEngine.GetCumulativeDelta();
                SetValue(barDelta, _barDeltaSeriesIdx);
                SetValue(cumDelta, _cumDeltaSeriesIdx);
            }
        }

        private void ProcessBarClose(UpdateArgs args)
        {
            if (HistoricalData.Count < 3) return;

            var bar0 = ToBar(HistoricalData[0]);  // newest (just closed)
            var bar1 = ToBar(HistoricalData[1]);
            var bar2 = HistoricalData.Count > 2 ? ToBar(HistoricalData[2]) : bar1;

            string tf = HistoricalData.Aggregation?.ToString() ?? "1M";

            _msEngine5M.ProcessNewBar(bar1, tf);
            _fvgEngine.ProcessNewBar(bar2, bar1, bar0, tf);
            _fpEngine.OnBarClose(bar0.High, bar0.Low, bar0.Close, bar0.Time);
            _deltaEngine.OnBarClose(bar0.High, bar0.Low);

            // Build bar list for OB detection (last 30 bars)
            var barList = new List<CoreBar>();
            int count = Math.Min(30, HistoricalData.Count);
            for (int i = count - 1; i >= 0; i--)
                barList.Add(ToBar(HistoricalData[i]));
            _obEngine.ProcessBars(barList, _msEngine5M.GetATR20(), _tickSize);

            // FVG fill check on bar close
            _fvgEngine.UpdateFillStatus(bar0.Close, _tickSize);

            // PDH/PDL update
            UpdatePDHL(bar0);

            // VWAP session check
            CheckVwapSessionReset(bar0.Time);

            // HTF bias refresh (throttled internally to 15-minute intervals)
            // In indicator context we just call with empty lists if no data; real callers inject daily/4H bars
            _htfEngine.Refresh(Enumerable.Empty<CoreBar>(), Enumerable.Empty<CoreBar>());

            _totalBarCount++;
        }

        private void ProcessTick()
        {
            double price = HistoricalData[0][PriceType.Close];
            long   vol   = (long)(HistoricalData[0][PriceType.Volume]);

            _fvgEngine.UpdateFillStatus(price, _tickSize);
            _obEngine.UpdateValidity(price, _tickSize);

            // VWAP running sum (approximate using bar close prices)
            _vwapNumerator   += price * vol;
            _vwapDenominator += vol;
            _vwapValue        = _vwapDenominator > 0
                ? _vwapNumerator / _vwapDenominator : price;

            // Volume profile
            _vpEngine.ProcessTrade(price, Math.Max(1, vol));
            _vpEngine.CheckSessionReset(DateTime.UtcNow);
        }

        // ─────────────────────────────────────────────────────────────
        // NewLast / NewLevel2 — Subscribe via Symbol events in OnInit
        // (No overrides exist on Indicator base class in this SDK)
        // ─────────────────────────────────────────────────────────────

        private void HandleNewLast(Symbol symbol, Last last)
        {
            var side = last.AggressorFlag == AggressorFlag.Buy  ? AggressorSide.Buy
                     : last.AggressorFlag == AggressorFlag.Sell ? AggressorSide.Sell
                     : AggressorSide.Unknown;

            _deltaEngine.ProcessTrade(last.Price, (long)last.Size, side);
            _fpEngine.ProcessTrade(last.Price, (long)last.Size, side);
            _tapeEngine.ProcessTrade(last.Price, (long)last.Size, side, last.Time, _largeLotThreshold);
            _vpEngine.ProcessTrade(last.Price, (long)last.Size);
        }

        private void HandleNewLevel2(Symbol symbol, Level2Quote quote, DOMQuote dom)
        {
            var side = quote.PriceType == QuotePriceType.Bid ? QuoteSide.Bid : QuoteSide.Ask;
            var domQuote = new DomLevel2Quote(quote.Price, (long)quote.Size, side, quote.Time);
            _domEngine.ProcessLevel2(domQuote);

            // Lightweight eval after each quote (deferred to background in strategy)
            _domEngine.EvaluateConditions();
        }

        // ─────────────────────────────────────────────────────────────
        // Score Badge Update
        // ─────────────────────────────────────────────────────────────

        private void UpdateScoreBadge()
        {
            var ms   = _msEngine5M.GetLatestStructureEvent();
            var kz   = _kzEngine.GetActiveKillZoneName(DateTime.UtcNow,
                           EnableLondonTint, EnableNYTint, false, false);
            bool isSB = _kzEngine.IsSilverBullet(DateTime.UtcNow);

            var fvgL = _fvgEngine.GetNearestFVG(
                HistoricalData[0][PriceType.Close], SignalDirection.Long);
            var fvgS = _fvgEngine.GetNearestFVG(
                HistoricalData[0][PriceType.Close], SignalDirection.Short);
            var ob   = _obEngine.GetNearestOrderBlock(
                HistoricalData[0][PriceType.Close],
                ms?.Direction ?? SignalDirection.Long);

            var fpSig = _fpEngine.GetSignalSummary();
            var dom   = _domEngine.GetImbalanceSignal();
            var bias  = _htfEngine.GetCurrentBias();

            // Build context for the prevailing direction
            SignalDirection dir = bias switch
            {
                MarketBias.StrongBullish or MarketBias.Bullish => SignalDirection.Long,
                MarketBias.StrongBearish or MarketBias.Bearish => SignalDirection.Short,
                _ => ms?.Direction ?? SignalDirection.Long
            };

            var ctx = new SetupContext
            {
                Time                 = DateTime.UtcNow,
                Symbol               = Symbol?.Name ?? "—",
                HTFBias              = bias,
                ActiveKillZone       = kz,
                IsSilverBullet       = isSB,
                LatestStructureEvent = ms,
                NearestFVG           = dir == SignalDirection.Long ? fvgL : fvgS,
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
                DOMImbalance         = dom,
                DeltaSlope           = _deltaEngine.GetDeltaSlope(),
                IsDeltaDivergent     = _deltaEngine.IsDeltaDivergent(dir),
                IsLargeBlockBurst    = _tapeEngine.IsLargeBlockBurst(dir),
                IsTapeAccelerating   = _tapeEngine.IsAccelerating(dir),
                ATR20                = _msEngine5M.GetATR20(),
                SwingHigh            = _msEngine5M.GetSwingHigh(),
                SwingLow             = _msEngine5M.GetSwingLow()
            };

            float score = _scoreEngine.ScoreSetup(ctx, dir);
            if (score < 0) score = 0;

            lock (_scoreLock)
            {
                _currentScore     = score;
                _currentDirection = dir == SignalDirection.Long ? "LONG" : "SHORT";
            }

            // Draw entry arrow when score crosses threshold
            if (score >= MinScoreForArrow && HistoricalData.Count > 0)
            {
                int barIdx = HistoricalData.Count - 1;
                bool alreadyMarked = _arrows.Any(a => a.BarIndex == barIdx);
                if (!alreadyMarked)
                    _arrows.Add((barIdx, HistoricalData[0][PriceType.Close], dir, score));
            }
        }

        // ─────────────────────────────────────────────────────────────
        // OnPaintChart — all visual rendering
        // ─────────────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            var gr      = args.Graphics;
            var bounds  = args.Rectangle;

            // ── Kill Zone background tint ──────────────────────────────
            if (EnableLondonTint || EnableNYTint)
                PaintKillZoneTint(gr, bounds);

            // ── FVG boxes ──────────────────────────────────────────────
            if (ShowFVGBoxes)
                PaintFVGBoxes(gr, bounds);

            // ── Order Block boxes ──────────────────────────────────────
            if (ShowOBBoxes)
                PaintOrderBlockBoxes(gr, bounds);

            // ── Structure lines (BOS / CHoCH / MSS) ──────────────────
            if (ShowStructureLines)
                PaintStructureLines(gr, bounds);

            // ── Volume Profile lines (POC / VAH / VAL) ────────────────
            if (ShowVolumeProfileLines)
                PaintVolumeProfileLines(gr, bounds);

            // ── VWAP ──────────────────────────────────────────────────
            if (ShowVWAP)
                PaintVWAP(gr, bounds);

            // ── PDH / PDL ──────────────────────────────────────────────
            if (ShowPDHL)
                PaintPDHL(gr, bounds);

            // ── Entry arrows ───────────────────────────────────────────
            PaintEntryArrows(gr, bounds);

            // ── Score badge ────────────────────────────────────────────
            PaintScoreBadge(gr, bounds);

            // ── DOM imbalance corner indicator ─────────────────────────
            if (ShowDOMIndicator)
                PaintDOMIndicator(gr, bounds);
        }

        // ─────────────────────────────────────────────────────────────
        // Painter helpers
        // ─────────────────────────────────────────────────────────────

        private void PaintKillZoneTint(Graphics gr, RectangleF bounds)
        {
            // Iterate visible bars and shade kill zone periods
            int visibleBars = HistoricalData.Count;
            if (visibleBars == 0) return;

            for (int i = 0; i < Math.Min(visibleBars, 500); i++)
            {
                try
                {
                    var barTime = HistoricalData[i].TimeLeft;
                    var kz = _kzEngine.GetActiveKillZoneName(barTime.ToUniversalTime(),
                                 EnableLondonTint, EnableNYTint, false, false);
                    if (kz == "None") continue;

                    Color tint = kz.Contains("NY") || kz.Contains("Silver Bullet 2")
                        ? Color.FromArgb(20, 255, 215, 0)   // amber/gold for NY
                        : Color.FromArgb(15, 100, 149, 237); // cornflower blue for London

                    float x = GetXByBarNumber(i);
                    float w = GetBarWidth();
                    using var brush = new SolidBrush(tint);
                    gr.FillRectangle(brush, x, bounds.Top, w, bounds.Height);
                }
                catch { }
            }
        }

        private void PaintFVGBoxes(Graphics gr, RectangleF bounds)
        {
            var fvgs = _fvgEngine.GetActiveFVGs();
            foreach (var fvg in fvgs.Take(15))
            {
                try
                {
                    float yTop    = GetYByValue(fvg.Top);
                    float yBottom = GetYByValue(fvg.Bottom);
                    float x       = GetXByTime(fvg.FormedTime);
                    float w       = bounds.Right - x;

                    if (x < bounds.Left) x = bounds.Left;
                    if (w < 2) continue;

                    Color fill = fvg.Direction == SignalDirection.Long
                        ? Color.FromArgb(30, 0, 200, 100)    // semi-transparent green
                        : Color.FromArgb(30, 220, 50, 50);   // semi-transparent red

                    Color border = fvg.Direction == SignalDirection.Long
                        ? Color.FromArgb(120, 0, 200, 100)
                        : Color.FromArgb(120, 220, 50, 50);

                    float boxH = Math.Abs(yTop - yBottom);
                    if (boxH < 1) boxH = 1;
                    float boxY = Math.Min(yTop, yBottom);

                    using var fillBrush = new SolidBrush(fill);
                    using var pen       = new Pen(border, 1f);
                    gr.FillRectangle(fillBrush, x, boxY, w, boxH);
                    gr.DrawRectangle(pen, x, boxY, w, boxH);

                    // FVG label
                    string label = fvg.Direction == SignalDirection.Long ? "FVG↑" : "FVG↓";
                    using var font = new Font("Consolas", 7f);
                    gr.DrawString(label, font, new SolidBrush(border), x + 2, boxY + 1);
                }
                catch { }
            }
        }

        private void PaintOrderBlockBoxes(Graphics gr, RectangleF bounds)
        {
            var obs = _obEngine.GetActiveOrderBlocks();
            foreach (var ob in obs.Take(10))
            {
                try
                {
                    float yTop    = GetYByValue(ob.Top);
                    float yBottom = GetYByValue(ob.Bottom);
                    float x       = GetXByTime(ob.FormedTime);
                    float w       = bounds.Right - x;

                    if (x < bounds.Left) x = bounds.Left;
                    if (w < 2) continue;

                    Color borderColor = ob.IsBreakerBlock
                        ? Color.FromArgb(200, 255, 140, 0)   // orange for breaker
                        : ob.Direction == SignalDirection.Long
                            ? Color.FromArgb(200, 0, 180, 80)
                            : Color.FromArgb(200, 200, 40, 40);

                    float boxH = Math.Abs(yTop - yBottom);
                    if (boxH < 1) boxH = 1;
                    float boxY = Math.Min(yTop, yBottom);

                    using var pen = new Pen(borderColor, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Solid };
                    gr.DrawRectangle(pen, x, boxY, w, boxH);

                    string label = ob.IsBreakerBlock ? "BB" : "OB";
                    using var font = new Font("Consolas", 7.5f, FontStyle.Bold);
                    gr.DrawString(label, font, new SolidBrush(borderColor), x + 2, boxY + 1);
                }
                catch { }
            }
        }

        private void PaintStructureLines(Graphics gr, RectangleF bounds)
        {
            var ms = _msEngine5M.GetLatestStructureEvent();
            if (ms == null) return;

            try
            {
                float y  = GetYByValue(ms.Price);
                float x1 = GetXByTime(ms.Time);
                float x2 = bounds.Right;

                if (x1 < bounds.Left)  x1 = bounds.Left;
                if (x1 > x2)           return;

                string label;
                Color  color;
                switch (ms.EventType)
                {
                    case StructureEventType.BOS:
                        label = "BOS";
                        color = ms.Direction == SignalDirection.Long
                            ? Color.FromArgb(200, 0, 200, 255)
                            : Color.FromArgb(200, 255, 80, 80);
                        break;
                    case StructureEventType.CHoCH:
                        label = "CHoCH";
                        color = Color.FromArgb(200, 255, 165, 0);
                        break;
                    default:  // MSS
                        label = "MSS";
                        color = Color.FromArgb(220, 148, 0, 211);
                        break;
                }

                using var pen  = new Pen(color, 1.2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                using var font = new Font("Consolas", 7.5f, FontStyle.Bold);
                gr.DrawLine(pen, x1, y, x2, y);
                gr.DrawString(label, font, new SolidBrush(color), x2 - 40, y - 10);
            }
            catch { }
        }

        private void PaintVolumeProfileLines(Graphics gr, RectangleF bounds)
        {
            try
            {
                using var pocPen = new Pen(Color.FromArgb(180, 128, 128, 128), 1.2f)
                    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                using var vaPen  = new Pen(Color.FromArgb(120, 128, 128, 128), 0.8f)
                    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                using var font   = new Font("Consolas", 7f);
                var  labelBrush  = new SolidBrush(Color.FromArgb(180, 160, 160, 160));

                double poc = _vpEngine.GetPriorSessionPOC();
                double vah = _vpEngine.GetPriorSessionVAH();
                double val = _vpEngine.GetPriorSessionVAL();

                void DrawLevel(double price, Pen pen, string label)
                {
                    if (price <= 0) return;
                    float y = GetYByValue(price);
                    gr.DrawLine(pen, bounds.Left, y, bounds.Right, y);
                    gr.DrawString($"{label}: {price:F1}", font, labelBrush, bounds.Right - 70, y - 9);
                }

                DrawLevel(poc, pocPen, "POC");
                DrawLevel(vah, vaPen, "VAH");
                DrawLevel(val, vaPen, "VAL");

                // Current session
                double cPoc = _vpEngine.GetPOC();
                double cVah = _vpEngine.GetVAH();
                double cVal = _vpEngine.GetVAL();
                using var cPocPen = new Pen(Color.FromArgb(160, 200, 200, 100), 1f)
                    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                DrawLevel(cPoc, cPocPen, "sPOC");

                labelBrush.Dispose();
            }
            catch { }
        }

        private void PaintVWAP(Graphics gr, RectangleF bounds)
        {
            if (_vwapValue <= 0) return;
            try
            {
                float y = GetYByValue(_vwapValue);
                using var pen  = new Pen(Color.FromArgb(200, 150, 80, 220), 1.5f);
                using var font = new Font("Consolas", 7f);
                gr.DrawLine(pen, bounds.Left, y, bounds.Right, y);
                gr.DrawString($"VWAP: {_vwapValue:F1}", font,
                    new SolidBrush(Color.FromArgb(200, 150, 80, 220)),
                    bounds.Right - 90, y - 9);
            }
            catch { }
        }

        private void PaintPDHL(Graphics gr, RectangleF bounds)
        {
            try
            {
                using var pen  = new Pen(Color.FromArgb(150, 200, 130, 50), 1f)
                    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                using var font = new Font("Consolas", 7f);
                var brush = new SolidBrush(Color.FromArgb(150, 200, 130, 50));

                if (_pdHigh > 0)
                {
                    float y = GetYByValue(_pdHigh);
                    gr.DrawLine(pen, bounds.Left, y, bounds.Right, y);
                    gr.DrawString($"PDH: {_pdHigh:F1}", font, brush, bounds.Right - 80, y - 9);
                }
                if (_pdLow < double.MaxValue)
                {
                    float y = GetYByValue(_pdLow);
                    gr.DrawLine(pen, bounds.Left, y, bounds.Right, y);
                    gr.DrawString($"PDL: {_pdLow:F1}", font, brush, bounds.Right - 80, y + 2);
                }
                brush.Dispose();
            }
            catch { }
        }

        private void PaintEntryArrows(Graphics gr, RectangleF bounds)
        {
            foreach (var arrow in _arrows.TakeLast(10))
            {
                try
                {
                    float x = GetXByTime(HistoricalData[HistoricalData.Count - 1 - arrow.BarIndex].TimeLeft);
                    float y = GetYByValue(arrow.Price);

                    bool isLong = arrow.Dir == SignalDirection.Long;
                    Color arrowColor = isLong
                        ? Color.FromArgb(220, 0, 200, 80)
                        : Color.FromArgb(220, 220, 50, 50);

                    // Draw triangle arrow
                    PointF[] pts = isLong
                        ? new[] { new PointF(x, y - 20), new PointF(x - 6, y - 30), new PointF(x + 6, y - 30) }
                        : new[] { new PointF(x, y + 20), new PointF(x - 6, y + 30), new PointF(x + 6, y + 30) };

                    using var brush = new SolidBrush(arrowColor);
                    gr.FillPolygon(brush, pts);

                    // Score label
                    string lbl = $"{(isLong ? "↑" : "↓")} {arrow.Score:F0}";
                    using var font = new Font("Consolas", 8f, FontStyle.Bold);
                    float lblY = isLong ? y - 42 : y + 32;
                    gr.DrawString(lbl, font, brush, x - 12, lblY);
                }
                catch { }
            }
        }

        private void PaintScoreBadge(Graphics gr, RectangleF bounds)
        {
            try
            {
                float score;
                string direction;
                lock (_scoreLock)
                {
                    score     = _currentScore;
                    direction = _currentDirection;
                }

                Color scoreColor = score >= 65 ? Color.FromArgb(230, 0, 210, 100)
                                 : score >= 50 ? Color.FromArgb(230, 230, 185, 0)
                                 : Color.FromArgb(180, 130, 130, 130);

                string text = $"SCORE: {score:F0} | {direction}";
                using var font = new Font("Consolas", 11f, FontStyle.Bold);
                using var bg   = new SolidBrush(Color.FromArgb(160, 20, 20, 30));
                using var fg   = new SolidBrush(scoreColor);

                SizeF sz = gr.MeasureString(text, font);
                float bx = bounds.Right  - sz.Width  - 12;
                float by = bounds.Top    + 12;

                gr.FillRectangle(bg, bx - 4, by - 3, sz.Width + 8, sz.Height + 6);
                gr.DrawString(text, font, fg, bx, by);

                // HTF bias label below score
                var bias = _htfEngine.GetCurrentBias();
                string biasStr = bias.ToString().ToUpper();
                Color  biasColor = bias is MarketBias.StrongBullish or MarketBias.Bullish
                    ? Color.FromArgb(200, 0, 200, 80)
                    : bias is MarketBias.StrongBearish or MarketBias.Bearish
                        ? Color.FromArgb(200, 220, 50, 50)
                        : Color.FromArgb(160, 130, 130, 130);

                using var biasFont  = new Font("Consolas", 8f);
                using var biasBrush = new SolidBrush(biasColor);
                gr.DrawString($"HTF: {biasStr}", biasFont, biasBrush, bx, by + sz.Height + 2);
            }
            catch { }
        }

        private void PaintDOMIndicator(Graphics gr, RectangleF bounds)
        {
            try
            {
                var imbalance = _domEngine.GetImbalanceSignal();
                bool absorption = _domEngine.IsAbsorptionActive();

                Color domColor = imbalance == DOMImbalanceSignal.BidHeavy
                    ? Color.FromArgb(200, 0, 200, 80)
                    : imbalance == DOMImbalanceSignal.AskHeavy
                        ? Color.FromArgb(200, 220, 50, 50)
                        : Color.FromArgb(150, 128, 128, 128);

                string domText = absorption
                    ? $"DOM: {(imbalance == DOMImbalanceSignal.BidHeavy ? "BID↑" : imbalance == DOMImbalanceSignal.AskHeavy ? "ASK↓" : "BAL")} ⊗ABS"
                    : $"DOM: {(imbalance == DOMImbalanceSignal.BidHeavy ? "BID↑" : imbalance == DOMImbalanceSignal.AskHeavy ? "ASK↓" : "BAL")}";

                using var font  = new Font("Consolas", 8f, FontStyle.Bold);
                using var bg    = new SolidBrush(Color.FromArgb(140, 20, 20, 30));
                using var fg    = new SolidBrush(domColor);

                SizeF sz = gr.MeasureString(domText, font);
                float bx = bounds.Right - sz.Width - 12;
                float by = bounds.Top   + 52;

                gr.FillRectangle(bg, bx - 4, by - 3, sz.Width + 8, sz.Height + 4);
                gr.DrawString(domText, font, fg, bx, by);
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────
        // Helper methods
        // ─────────────────────────────────────────────────────────────

        private CoreBar ToBar(IHistoryItem item)
        {
            if (item is HistoryItemBar b)
                return new CoreBar
                {
                    Time   = item.TimeLeft,
                    Open   = b.Open,
                    High   = b.High,
                    Low    = b.Low,
                    Close  = b.Close,
                    Volume = b.Ticks
                };

            double price = item[PriceType.Close];
            return new CoreBar
            {
                Time   = item.TimeLeft,
                Open   = price, High = price, Low = price, Close = price,
                Volume = (long)item[PriceType.Volume]
            };
        }

        private void UpdatePDHL(CoreBar bar)
        {
            var etDate = TimeZoneInfo.ConvertTimeFromUtc(bar.Time, EasternTime).Date;
            if (_prevDate == DateTime.MinValue)
            {
                _prevDate = etDate;
                _pdHigh   = bar.High;
                _pdLow    = bar.Low;
            }
            else if (etDate > _prevDate)
            {
                _pdHigh   = bar.High;
                _pdLow    = bar.Low;
                _prevDate = etDate;
            }
            else
            {
                if (bar.High > _pdHigh) _pdHigh = bar.High;
                if (bar.Low  < _pdLow)  _pdLow  = bar.Low;
            }
        }

        private void CheckVwapSessionReset(DateTime barTimeUtc)
        {
            var etNow = TimeZoneInfo.ConvertTimeFromUtc(barTimeUtc, EasternTime);
            if (_vwapSessionStart == DateTime.MinValue ||
                (etNow.Hour >= 17 &&
                 TimeZoneInfo.ConvertTimeFromUtc(_vwapSessionStart, EasternTime).Hour < 17))
            {
                _vwapNumerator    = 0;
                _vwapDenominator  = 0;
                _vwapValue        = 0;
                _vwapSessionStart = barTimeUtc;
            }
        }

        private static readonly TimeZoneInfo EasternTime =
            TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        // Quantower coordinate helpers — these are implemented by the base class
        // via protected abstract/virtual methods. We use them via reflection pattern.
        // In real SDK: GetXByBarNumber, GetXByTime, GetYByValue are available.

        private float GetXByBarNumber(int barNumber)
        {
            try { return base.ChartWindow?.GetXByBarNumber(barNumber) ?? 0f; }
            catch { return 0f; }
        }

        private float GetXByTime(DateTime time)
        {
            try { return base.ChartWindow?.GetXByTime(time) ?? 0f; }
            catch { return 0f; }
        }

        private float GetYByValue(double price)
        {
            try { return base.ChartWindow?.GetYByValue(price) ?? 0f; }
            catch { return 0f; }
        }

        private float GetBarWidth()
        {
            try { return base.ChartWindow?.GetBarWidth() ?? 4f; }
            catch { return 4f; }
        }

        // ─────────────────────────────────────────────────────────────
        // Cleanup
        // ─────────────────────────────────────────────────────────────

        protected override void OnClear()
        {
            _domEngine?.Dispose();
        }
    }
}
