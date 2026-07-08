using System;
using System.Drawing;
using LucidGoldFlowScalper.Engines;
using LucidGoldFlowScalper.Models;
using TradingPlatform.BusinessLayer;

namespace LucidGoldFlowScalper.Indicator
{
    /// <summary>
    /// Layer 1: Visualizes order flow, market structure, and kill zones on the chart.
    /// Operates purely as an indicator, does not place trades.
    /// </summary>
    public class LucidGoldFlowIndicator : TradingPlatform.BusinessLayer.Indicator
    {
        [InputParameter("Instrument Mode (MGC/GC)")]
        public string InstrumentMode = "MGC";

        [InputParameter("Show FVG Zones")]
        public bool ShowFVGZones = true;

        [InputParameter("Show Order Blocks")]
        public bool ShowOrderBlocks = true;

        [InputParameter("Show Volume Profile")]
        public bool ShowVolumeProfile = true;

        [InputParameter("Delta Window Ticks")]
        public int DeltaWindowTicks = 500;
        private OrderFlowEngine _orderFlowEngine;
        private VolumeProfileEngine _volumeProfileEngine;
        private MarketStructureEngine _marketStructureEngine;
        private KillZoneEngine _killZoneEngine;

        public LucidGoldFlowIndicator()
        {
            Name = "Lucid Gold Flow";
            Description = "Order Flow and Market Structure indicator for Gold Scalping";
            AddLineSeries("Cumulative Delta", Color.Cyan, 2, LineStyle.Histogramm);
            SeparateWindow = true; // Delta goes in sub-window
        }

        protected override void OnInit()
        {
            _orderFlowEngine = new OrderFlowEngine(500, 50, 20, 10.0);
            _volumeProfileEngine = new VolumeProfileEngine(0.1);
            _marketStructureEngine = new MarketStructureEngine(3);
            _killZoneEngine = new KillZoneEngine(true, true, false);
            
            if (this.Symbol != null)
            {
                this.Symbol.NewLast += Symbol_NewLast;
            }
        }

        protected override void OnClear()
        {
            if (this.Symbol != null)
            {
                this.Symbol.NewLast -= Symbol_NewLast;
            }
            base.OnClear();
        }

        private void Symbol_NewLast(Symbol symbol, Last last)
        {
            bool isBuy = last.AggressorFlag == AggressorFlag.Buy;
            _orderFlowEngine.OnNewTrade((decimal)last.Price, (long)last.Size, isBuy);
            _volumeProfileEngine.AddTrade(last.Price, (long)last.Size, isBuy);
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            // Indicator logic updates engines for visualization
            if (args.Reason == UpdateReason.NewTick || args.Reason == UpdateReason.NewBar)
            {
                SetValue(_orderFlowEngine.SessionDelta, 0, 0); // Update LineSeries (Histogram)
            }
            else if (args.Reason == UpdateReason.NewBar)
            {
                _orderFlowEngine.OnNewBar();
                _volumeProfileEngine.RecalculateProfile();
                
                // Fetch recent bars for market structure
                var count = Math.Min(10, HistoricalData.Count);
                var bars = new IIHistoryItem[count];
                for(int i=0; i<count; i++) bars[i] = HistoricalData[i];
                
                _marketStructureEngine.OnNewBar(bars);
            }
        }
        
        // Custom rendering for FVGs and OBs would go here using Graphics APIs if supported by SDK.
        // For standard Quantower Indicator, we override OnPaintChart.
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);
            // Draw HUD and shapes using args.Graphics
            var g = args.Graphics;
            
            // HUD
            string hudText = $"Bias: {_marketStructureEngine.ConsolidatedBias}\n" +
                             $"CumDelta: {_orderFlowEngine.SessionDelta}\n" +
                             $"POC: {_volumeProfileEngine.POC:F1} | VAH: {_volumeProfileEngine.VAH:F1} | VAL: {_volumeProfileEngine.VAL:F1}";
                             
            g.DrawString(hudText, new Font("Arial", 10), Brushes.White, new PointF(10, 10));
        }
    }
}
