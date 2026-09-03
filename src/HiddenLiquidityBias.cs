//
// Hidden Liquidity Bias
//
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.Gui.Tools;
using SharpDX;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class HiddenLiquidityBias : Indicator
    {
        private const int MaxLiqEvents    = 5000;
        private const int IcebergPoolSz   = 200;
        private const int IcebergMaxClips = 60;
        private const int RecentTradeSz   = 128;

        private struct LiqEvent { public long TimestampTicks; public double Volume; public int Side; public double ContextWeight; }

        private sealed class IcebergCandidate
        {
            public int PoolIndex; public double Price; public int Side;
            public readonly int[]  ClipSizes = new int[IcebergMaxClips];
            public readonly long[] ClipTicks = new long[IcebergMaxClips];
            public int ClipCount, CumulativeSize; public long FirstTick;
            public void Reset() { Price = 0; Side = 0; ClipCount = 0; FirstTick = 0; CumulativeSize = 0; }
        }

        private struct SavedRange { public int StartBar, EndBar; }

        private LiqEvent[] liqBuf; private int liqHead, liqCount;
        private double bullEvidenceSum, bearEvidenceSum, biasScore;
        private double priceAtLastRecalc, responseAccum;
        private double currentBid, currentAsk, lastTradedPrice, tickSize;
        private bool hasPendingBlock; private double pendingBlockPrice, pendingBlockVolume; private int pendingBlockSide; private long pendingBlockTick;
        private IcebergCandidate[] icebergPool; private int[] icebergFreeStack; private int icebergFreeTop;
        private Dictionary<double, IcebergCandidate> icebergActive; private double[] icebergExpireKeys; private int icebergExpireCnt;
        private double[] rtPrice; private long[] rtTick; private int rtHead, rtCount;
        private double rangeHigh, rangeLow; private bool inRange; private int rangeStartBar, breakoutBarsCnt, atrExpansionBarsCnt;
        private SavedRange[] savedRanges; private int savedRangeHead, savedRangeCount;
        private Dictionary<int, double> scoreLog;
        private SolidColorBrush hlBrush; private int hlCachedOpacity = -1, hlCachedR = -1, hlCachedG = -1, hlCachedB = -1;
        private bool prevAboveWarn, prevAboveHigh, prevInRange;
        private int lastWarnAlertBar = -1, lastHighAlertBar = -1, lastBreakoutAlertBar = -1;

        private SharpDX.Direct2D1.SolidColorBrush dxHistoGreenBrush, dxHistoRedBrush, dxLineBuyBrush, dxLineSellBrush;
        private SharpDX.Direct2D1.SolidColorBrush dxDotBuyBrush, dxDotSellBrush, dxWarnBrush, dxHighBrush, dxRangeBrush;
        private int dxCachedHlOpacity = -1, dxCachedHlR = -1, dxCachedHlG = -1, dxCachedHlB = -1;
        private Brush cachedHistoPosBrush, cachedHistoNegBrush, cachedBiasLineBuyBrush, cachedBiasLineSellBrush;
        private Brush cachedDotBuyBrush, cachedDotSellBrush, cachedWarnBandBrush, cachedHighBandBrush;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Context-aware hidden liquidity oscillator. Interprets blocks and icebergs through location in range + price response.";
                Name                     = "Obsidian Flow Hidden Liquidity Bias";
                Calculate                = Calculate.OnBarClose;
                IsOverlay                = false;
                DrawOnPricePanel         = false;
                DisplayInDataBox         = true;
                ScaleJustification       = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive = false;

                BlockThreshold = 50; IcebergTimeWindow = 800; IcebergMaxClipSize = 20; IcebergMinClips = 3; IcebergMinTotal = 40; LookbackMinutes = 20;
                ResponseWeight = 0.30; LocationBiasStrength = 1.00;
                AlertThresholdWarn = 0.5; AlertThresholdHigh = 0.7; EnableAlerts = true;
                AtrPeriod = 14; AtrMultiplier = 0.7; AtrLookback = 20; RangeLookbackBars = 20; MaxRangeTicks = 24;
                BreakoutConfirmTicks = 2; BreakoutConfirmBars = 2; MaxSavedRanges = 2;
                EnableHighlights = true; ShowPriceChartHighlight = true; ShowOscillatorHighlight = true;
                HighlightOpacity = 25; HighlightR = 255; HighlightG = 200; HighlightB = 50;
                ShowHistogramBars = true; ShowBiasLine = true; ShowEventDots = false;
                ShowThresholdBands = false; // -- default OFF per request

                HistoPositiveBrush  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255,   0, 200,  56));
                HistoNegativeBrush  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 224,  26,  26));
                BiasLineBuyBrush    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LimeGreen);
                BiasLineSellBrush   = Brushes.Tomato;
                DotBuyBrush         = Brushes.DodgerBlue;
                DotSellBrush        = Brushes.Orange;
                WarnBandBrush       = Brushes.Orange;
                HighBandBrush       = Brushes.Tomato;
            }
            else if (State == State.Configure)
            {
                AddPlot(new Stroke(Brushes.DodgerBlue, 2f), PlotStyle.Bar, "BiasScore");
                AddLine(new Stroke(Brushes.DodgerBlue, 1f),  0.0,                "Zero");
                AddLine(new Stroke(Brushes.Orange,     1f),  AlertThresholdWarn, "WarnPos");
                AddLine(new Stroke(Brushes.Orange,     1f), -AlertThresholdWarn, "WarnNeg");
                AddLine(new Stroke(Brushes.Tomato,     1f),  AlertThresholdHigh, "HighPos");
                AddLine(new Stroke(Brushes.Tomato,     1f), -AlertThresholdHigh, "HighNeg");

                liqBuf = new LiqEvent[MaxLiqEvents]; liqHead = 0; liqCount = 0;
                icebergPool = new IcebergCandidate[IcebergPoolSz];
                icebergFreeStack = new int[IcebergPoolSz];
                icebergFreeTop = IcebergPoolSz;
                for (int i = 0; i < IcebergPoolSz; i++) { icebergPool[i] = new IcebergCandidate(); icebergPool[i].PoolIndex = i; icebergFreeStack[i] = i; }
                icebergActive     = new Dictionary<double, IcebergCandidate>(IcebergPoolSz);
                icebergExpireKeys = new double[IcebergPoolSz];
                rtPrice = new double[RecentTradeSz]; rtTick = new long[RecentTradeSz]; rtHead = 0; rtCount = 0;
                scoreLog    = new Dictionary<int, double>(2000);
                savedRanges = new SavedRange[Math.Max(1, MaxSavedRanges)];
                hlCachedOpacity = -1; hlCachedR = hlCachedG = hlCachedB = -1;
            }
            else if (State == State.DataLoaded)
            {
                tickSize = Instrument.MasterInstrument.TickSize;
                bullEvidenceSum = 0; bearEvidenceSum = 0; biasScore = 0; responseAccum = 0; priceAtLastRecalc = 0;
                liqHead = 0; liqCount = 0; inRange = false; savedRangeHead = 0; savedRangeCount = 0;
                if (scoreLog != null) scoreLog.Clear();
                rangeHigh = 0; rangeLow = 0; rangeStartBar = 0; breakoutBarsCnt = 0;
            }
            else if (State == State.Terminated)
            {
                DisposeDxResources();
            }
        }

        protected override void OnMarketData(MarketDataEventArgs md)
        {
            switch (md.MarketDataType)
            {
                case MarketDataType.Bid: currentBid = md.Price; return;
                case MarketDataType.Ask: currentAsk = md.Price; return;
                case MarketDataType.Last: break;
                default: return;
            }

            double price  = md.Price;
            int    volume = (int)md.Volume;
            long   now    = md.Time.Ticks;
            int    side   = DetermineSide(price);

            PushRecentTrade(price, now);

            if (hasPendingBlock)
            {
                if (IsAbsorbed(price, pendingBlockPrice, pendingBlockSide))
                    AddLiqEvent(pendingBlockVolume, pendingBlockSide, pendingBlockTick, pendingBlockPrice);
                hasPendingBlock = false;
            }

            if (volume >= BlockThreshold)
            {
                hasPendingBlock    = true;
                pendingBlockPrice  = price;
                pendingBlockSide   = side;
                pendingBlockVolume = volume;
                pendingBlockTick   = now;
            }

            ProcessIcebergPrint(price, volume, side, now);
            ExpireOldEvents(now);
            RecalcBias();
            lastTradedPrice = price;
        }

        private int DetermineSide(double price)
        {
            if (currentAsk > 0 && Math.Abs(price - currentAsk) < tickSize * 0.5) return  1;
            if (currentBid > 0 && Math.Abs(price - currentBid) < tickSize * 0.5) return -1;
            return price >= lastTradedPrice ? 1 : -1;
        }

        private bool IsAbsorbed(double currentPrice, double blockPrice, int blockSide)
            => blockSide > 0 ? currentPrice >= blockPrice - tickSize : currentPrice <= blockPrice + tickSize;

        private void PushRecentTrade(double price, long ticks)
        {
            rtPrice[rtHead] = price; rtTick[rtHead] = ticks;
            rtHead = (rtHead + 1) % RecentTradeSz;
            if (rtCount < RecentTradeSz) rtCount++;
        }

        private double ComputeContextWeight(double price, int side, double volume)
        {
            double locationMod = 1.0;
            if (inRange && rangeHigh > rangeLow)
            {
                double pos = Math.Max(0.0, Math.Min(1.0, (price - rangeLow) / (rangeHigh - rangeLow)));
                locationMod = side > 0
                    ? 1.0 + LocationBiasStrength * 0.5 * (1.0 - 2.0 * pos)
                    : 1.0 + LocationBiasStrength * 0.5 * (2.0 * pos - 1.0);
                locationMod = Math.Max(0.05, locationMod);
            }
            return side * volume * locationMod;
        }

        private void AddLiqEvent(double volume, int side, long now, double price)
        {
            double cw = ComputeContextWeight(price, side, volume);
            if (liqCount == MaxLiqEvents)
            {
                LiqEvent oldest = liqBuf[liqHead];
                if (oldest.ContextWeight >= 0) bullEvidenceSum -= oldest.ContextWeight;
                else                           bearEvidenceSum -= (-oldest.ContextWeight);
                liqHead = (liqHead + 1) % MaxLiqEvents; liqCount--;
            }
            int wi = (liqHead + liqCount) % MaxLiqEvents;
            liqBuf[wi] = new LiqEvent { TimestampTicks = now, Volume = volume, Side = side, ContextWeight = cw };
            liqCount++;
            if (cw >= 0) bullEvidenceSum += cw; else bearEvidenceSum += (-cw);
            if (bullEvidenceSum < 0) bullEvidenceSum = 0;
            if (bearEvidenceSum < 0) bearEvidenceSum = 0;
        }

        private void ExpireOldEvents(long now)
        {
            long cutoff = now - (long)(LookbackMinutes * 60L) * TimeSpan.TicksPerSecond;
            while (liqCount > 0)
            {
                LiqEvent e = liqBuf[liqHead];
                if (e.TimestampTicks >= cutoff) break;
                if (e.ContextWeight >= 0) bullEvidenceSum -= e.ContextWeight;
                else                      bearEvidenceSum -= (-e.ContextWeight);
                liqHead = (liqHead + 1) % MaxLiqEvents; liqCount--;
            }
            if (bullEvidenceSum < 0) bullEvidenceSum = 0;
            if (bearEvidenceSum < 0) bearEvidenceSum = 0;
        }

        private void RecalcBias()
        {
            double total = bullEvidenceSum + bearEvidenceSum;
            if (total < 1.0) { biasScore = 0.0; if (scoreLog != null && CurrentBar >= 0) scoreLog[CurrentBar] = 0.0; priceAtLastRecalc = lastTradedPrice; return; }
            double rawScore = Math.Max(-1.0, Math.Min(1.0, (bullEvidenceSum - bearEvidenceSum) / total));
            if (ResponseWeight > 0.0 && priceAtLastRecalc > 0 && lastTradedPrice > 0)
            {
                double priceDelta  = lastTradedPrice - priceAtLastRecalc;
                int    expectedDir = rawScore >  0.05 ?  1 : (rawScore < -0.05 ? -1 : 0);
                int    actualDir   = priceDelta >  tickSize * 0.5 ?  1 : priceDelta < -tickSize * 0.5 ? -1 : 0;
                if (expectedDir != 0 && actualDir != 0) { double nudge = 0.04 * (expectedDir == actualDir ? 1.0 : -1.0); responseAccum = Math.Max(-1.0, Math.Min(1.0, responseAccum + nudge)); }
                responseAccum *= 0.97;
            }
            biasScore = Math.Max(-1.0, Math.Min(1.0, rawScore * (1.0 + ResponseWeight * responseAccum)));
            priceAtLastRecalc = lastTradedPrice;
            if (scoreLog != null && CurrentBar >= 0) scoreLog[CurrentBar] = biasScore;
        }

        private void ProcessIcebergPrint(double price, int volume, int side, long now)
        {
            long windowTks = (long)IcebergTimeWindow * TimeSpan.TicksPerMillisecond;
            icebergExpireCnt = 0;
            foreach (KeyValuePair<double, IcebergCandidate> kv in icebergActive)
                if (now - kv.Value.FirstTick > windowTks && icebergExpireCnt < icebergExpireKeys.Length)
                    icebergExpireKeys[icebergExpireCnt++] = kv.Key;
            for (int i = 0; i < icebergExpireCnt; i++) { IcebergCandidate exp; if (icebergActive.TryGetValue(icebergExpireKeys[i], out exp)) { icebergActive.Remove(icebergExpireKeys[i]); ReturnIcebergBuf(exp); } }

            IcebergCandidate c;
            if (icebergActive.TryGetValue(price, out c))
            {
                if (volume > IcebergMaxClipSize) { icebergActive.Remove(price); ReturnIcebergBuf(c); return; }
                if (c.ClipCount < IcebergMaxClips) { int idx = c.ClipCount++; c.ClipSizes[idx] = volume; c.ClipTicks[idx] = now; c.CumulativeSize += volume; }
                if (c.ClipCount >= IcebergMinClips && c.CumulativeSize >= IcebergMinTotal) { double cumVol = c.CumulativeSize; int iceSide = c.Side; double icePrice = c.Price; icebergActive.Remove(price); ReturnIcebergBuf(c); AddLiqEvent(cumVol, iceSide, now, icePrice); }
                return;
            }
            if (volume <= IcebergMaxClipSize) { IcebergCandidate fresh = GetFreeIceberg(); if (fresh == null) return; fresh.Price = price; fresh.Side = side; fresh.FirstTick = now; fresh.ClipSizes[0] = volume; fresh.ClipTicks[0] = now; fresh.ClipCount = 1; fresh.CumulativeSize = volume; icebergActive[price] = fresh; }
        }

        private IcebergCandidate GetFreeIceberg() { if (icebergFreeTop == 0) return null; return icebergPool[icebergFreeStack[--icebergFreeTop]]; }
        private void ReturnIcebergBuf(IcebergCandidate c) { c.Reset(); icebergFreeStack[icebergFreeTop++] = c.PoolIndex; }

        protected override void OnBarUpdate()
        {
            Values[0][0] = biasScore;
            scoreLog[CurrentBar] = biasScore;
            int minBars = Math.Max(AtrPeriod + AtrLookback, RangeLookbackBars) + 2;
            if (CurrentBar < minBars) return;

            double atrNow = ATR(AtrPeriod)[0];
            double atrSum = 0.0; int atrN = Math.Min(AtrLookback, CurrentBar);
            for (int k = 0; k < atrN; k++) atrSum += ATR(AtrPeriod)[k];
            double avgAtr = atrSum / atrN;
            bool atrCompressed = atrNow < avgAtr * AtrMultiplier;
            bool atrExpanded   = atrNow > avgAtr;

            if (inRange)
            {
                bool exitNow = false;
                bool closeOutside = Close[0] > rangeHigh + BreakoutConfirmTicks * tickSize || Close[0] < rangeLow - BreakoutConfirmTicks * tickSize;
                if (closeOutside) breakoutBarsCnt++; else breakoutBarsCnt = 0;
                if (breakoutBarsCnt >= BreakoutConfirmBars) exitNow = true;
                if (!exitNow) { if (atrExpanded) atrExpansionBarsCnt++; else atrExpansionBarsCnt = 0; if (atrExpansionBarsCnt >= BreakoutConfirmBars) exitNow = true; }
                if (exitNow) ExitRange();
            }

            if (!inRange)
            {
                double hiCand = double.MinValue, loCand = double.MaxValue;
                for (int k = 0; k < RangeLookbackBars; k++) { if (High[k] > hiCand) hiCand = High[k]; if (Low[k] < loCand) loCand = Low[k]; }
                bool rangeTight = (hiCand - loCand) <= MaxRangeTicks * tickSize;
                bool closeContained = rangeTight;
                if (rangeTight) for (int k = 0; k < RangeLookbackBars && closeContained; k++) if (Close[k] > hiCand || Close[k] < loCand) closeContained = false;
                if (atrCompressed && rangeTight && closeContained) { inRange = true; rangeHigh = hiCand; rangeLow = loCand; rangeStartBar = CurrentBar; breakoutBarsCnt = 0; atrExpansionBarsCnt = 0; }
            }

            if (EnableHighlights && ShowPriceChartHighlight && inRange)
            {
                if (HighlightOpacity != hlCachedOpacity || HighlightR != hlCachedR || HighlightG != hlCachedG || HighlightB != hlCachedB) RebuildHighlightBrush();
                BackBrushes[CurrentBar] = hlBrush;
            }
            FireAlerts();
            prevInRange = inRange;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || ChartBars == null || CurrentBar < 1) return;
            EnsureDxBrushes();

            int   cb       = CurrentBar;
            int   firstBar = Math.Max(0, ChartBars.FromIndex);
            int   lastBar  = Math.Min(ChartBars.ToIndex, cb);
            float zeroY    = (float)chartScale.GetYByValue(0.0);
            float halfBW   = (float)(chartControl.BarWidth * 0.5f);
            float barW     = (float)chartControl.BarWidth;
            float cLeft    = (float)chartControl.CanvasLeft;
            float cRight   = (float)chartControl.CanvasRight;
            float pnlY     = (float)ChartPanel.Y;
            float pnlH     = (float)ChartPanel.H;

            if (EnableHighlights && ShowOscillatorHighlight && dxRangeBrush != null && !dxRangeBrush.IsDisposed)
            {
                for (int s = 0; s < savedRangeCount; s++)
                {
                    SavedRange sr = savedRanges[s]; if (sr.EndBar < firstBar) continue;
                    float srx1 = sr.StartBar >= firstBar ? (float)chartControl.GetXByBarIndex(ChartBars, sr.StartBar) - halfBW : cLeft;
                    float srx2 = sr.EndBar   <= lastBar  ? (float)chartControl.GetXByBarIndex(ChartBars, sr.EndBar)   + halfBW : cRight;
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(srx1, pnlY, Math.Max(0f, srx2 - srx1), pnlH), dxRangeBrush);
                }
                if (inRange)
                {
                    float rx1 = rangeStartBar >= firstBar ? (float)chartControl.GetXByBarIndex(ChartBars, rangeStartBar) - halfBW : cLeft;
                    float rx2 = (float)chartControl.GetXByBarIndex(ChartBars, cb) + halfBW;
                    RenderTarget.FillRectangle(new SharpDX.RectangleF(rx1, pnlY, Math.Max(0f, rx2 - rx1), pnlH), dxRangeBrush);
                }
            }

            if (ShowHistogramBars)
                for (int i = firstBar; i <= lastBar; i++)
                {
                    double score; if (!scoreLog.TryGetValue(i, out score)) continue;
                    float cx = (float)chartControl.GetXByBarIndex(ChartBars, i);
                    float left = cx - halfBW;
                    float scoreY = (float)chartScale.GetYByValue(score);
                    if (score >= 0) { float h = Math.Max(0f, zeroY - scoreY); if (h > 0f && dxHistoGreenBrush != null && !dxHistoGreenBrush.IsDisposed) RenderTarget.FillRectangle(new SharpDX.RectangleF(left, scoreY, barW, h), dxHistoGreenBrush); }
                    else            { float h = Math.Max(0f, scoreY - zeroY); if (h > 0f && dxHistoRedBrush   != null && !dxHistoRedBrush.IsDisposed)   RenderTarget.FillRectangle(new SharpDX.RectangleF(left, zeroY, barW, h), dxHistoRedBrush); }
                }

            if (ShowThresholdBands)
            {
                DrawDashedHLine((float)chartScale.GetYByValue( AlertThresholdWarn), cLeft, cRight, dxWarnBrush);
                DrawDashedHLine((float)chartScale.GetYByValue(-AlertThresholdWarn), cLeft, cRight, dxWarnBrush);
                DrawDashedHLine((float)chartScale.GetYByValue( AlertThresholdHigh), cLeft, cRight, dxHighBrush);
                DrawDashedHLine((float)chartScale.GetYByValue(-AlertThresholdHigh), cLeft, cRight, dxHighBrush);
            }

            if (ShowBiasLine)
            {
                SharpDX.Vector2 prevPt = default(SharpDX.Vector2); bool hasPrev = false;
                for (int i = firstBar; i <= lastBar; i++)
                {
                    double score; if (!scoreLog.TryGetValue(i, out score)) { hasPrev = false; continue; }
                    float cx = (float)chartControl.GetXByBarIndex(ChartBars, i);
                    float cy = (float)chartScale.GetYByValue(score);
                    var cur = new SharpDX.Vector2(cx, cy);
                    if (hasPrev) { var seg = score >= 0 ? dxLineBuyBrush : dxLineSellBrush; if (seg != null && !seg.IsDisposed) RenderTarget.DrawLine(prevPt, cur, seg, 2f); }
                    prevPt = cur; hasPrev = true;
                }
            }

            if (ShowEventDots)
                for (int i = firstBar; i <= lastBar; i++)
                {
                    double score; if (!scoreLog.TryGetValue(i, out score)) continue;
                    float cx = (float)chartControl.GetXByBarIndex(ChartBars, i);
                    float cy = (float)chartScale.GetYByValue(score);
                    var dotBrush = score >= 0 ? dxDotBuyBrush : dxDotSellBrush;
                    if (dotBrush == null || dotBrush.IsDisposed) continue;
                    RenderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(cx, cy), 3f, 3f), dotBrush);
                }
        }

        private static SharpDX.Color4 ToColor4(Brush b, float alpha = 1f)
        {
            var sc = b as System.Windows.Media.SolidColorBrush;
            if (sc == null) return new SharpDX.Color4(1f, 1f, 1f, alpha);
            var c = sc.Color;
            return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha);
        }

        private void RebuildHighlightBrush()
        {
            byte alpha = (byte)Math.Round(HighlightOpacity * 255.0 / 100.0);
            hlBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, (byte)HighlightR, (byte)HighlightG, (byte)HighlightB));
            hlBrush.Freeze();
            hlCachedOpacity = HighlightOpacity; hlCachedR = HighlightR; hlCachedG = HighlightG; hlCachedB = HighlightB;
        }

        private void EnsureDxBrushes()
        {
            if (RenderTarget == null) return;
            void RebuildBrush(ref SharpDX.Direct2D1.SolidColorBrush dx, Brush wpf, ref Brush cached, float alpha = 1f) { if (dx == null || dx.IsDisposed || !object.ReferenceEquals(wpf, cached)) { if (dx != null && !dx.IsDisposed) dx.Dispose(); dx = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, ToColor4(wpf, alpha)); cached = wpf; } }
            RebuildBrush(ref dxHistoGreenBrush, HistoPositiveBrush,  ref cachedHistoPosBrush,      0.60f);
            RebuildBrush(ref dxHistoRedBrush,   HistoNegativeBrush,  ref cachedHistoNegBrush,      0.60f);
            RebuildBrush(ref dxLineBuyBrush,    BiasLineBuyBrush,    ref cachedBiasLineBuyBrush);
            RebuildBrush(ref dxLineSellBrush,   BiasLineSellBrush,   ref cachedBiasLineSellBrush);
            RebuildBrush(ref dxDotBuyBrush,     DotBuyBrush,         ref cachedDotBuyBrush);
            RebuildBrush(ref dxDotSellBrush,    DotSellBrush,        ref cachedDotSellBrush);
            RebuildBrush(ref dxWarnBrush,       WarnBandBrush,       ref cachedWarnBandBrush,      0.85f);
            RebuildBrush(ref dxHighBrush,       HighBandBrush,       ref cachedHighBandBrush,      0.85f);
            if (dxRangeBrush == null || dxRangeBrush.IsDisposed || HighlightOpacity != dxCachedHlOpacity || HighlightR != dxCachedHlR || HighlightG != dxCachedHlG || HighlightB != dxCachedHlB) BuildDxRangeBrush();
        }

        private void BuildDxRangeBrush()
        {
            if (RenderTarget == null) return;
            if (dxRangeBrush != null && !dxRangeBrush.IsDisposed) dxRangeBrush.Dispose();
            dxRangeBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(HighlightR / 255f, HighlightG / 255f, HighlightB / 255f, HighlightOpacity / 100f));
            dxCachedHlOpacity = HighlightOpacity; dxCachedHlR = HighlightR; dxCachedHlG = HighlightG; dxCachedHlB = HighlightB;
        }

        private void DisposeDxResources()
        {
            void D(ref SharpDX.Direct2D1.SolidColorBrush b) { if (b != null) { b.Dispose(); b = null; } }
            D(ref dxHistoGreenBrush); D(ref dxHistoRedBrush); D(ref dxLineBuyBrush); D(ref dxLineSellBrush);
            D(ref dxDotBuyBrush); D(ref dxDotSellBrush); D(ref dxWarnBrush); D(ref dxHighBrush); D(ref dxRangeBrush);
            dxCachedHlOpacity = dxCachedHlR = dxCachedHlG = dxCachedHlB = -1;
        }

        public override void OnRenderTargetChanged() { DisposeDxResources(); }

        private void DrawDashedHLine(float y, float x1, float x2, SharpDX.Direct2D1.SolidColorBrush brush)
        {
            if (brush == null || brush.IsDisposed) return;
            const float dash = 8f, gap = 4f; float x = x1;
            while (x < x2) { float end = Math.Min(x + dash, x2); RenderTarget.DrawLine(new SharpDX.Vector2(x, y), new SharpDX.Vector2(end, y), brush, 1f); x += dash + gap; }
        }

        private void ExitRange()
        {
            if (MaxSavedRanges > 0 && savedRanges != null) { savedRanges[savedRangeHead] = new SavedRange { StartBar = rangeStartBar, EndBar = CurrentBar }; savedRangeHead = (savedRangeHead + 1) % Math.Max(1, MaxSavedRanges); if (savedRangeCount < MaxSavedRanges) savedRangeCount++; }
            inRange = false; rangeHigh = 0; rangeLow = 0; rangeStartBar = 0; breakoutBarsCnt = 0; atrExpansionBarsCnt = 0;
        }

        private void FireAlerts()
        {
            if (!EnableAlerts) return;
            bool aboveWarn = Math.Abs(biasScore) >= AlertThresholdWarn;
            bool aboveHigh = Math.Abs(biasScore) >= AlertThresholdHigh;
            if (inRange && aboveWarn && !prevAboveWarn && CurrentBar != lastWarnAlertBar) { string dir = biasScore >= 0 ? "BUY" : "SELL"; Print(string.Format("HLB WARN | {0} bias={1:F2} (in range)", dir, biasScore)); lastWarnAlertBar = CurrentBar; }
            if (aboveHigh && !prevAboveHigh && CurrentBar != lastHighAlertBar) { string dir = biasScore >= 0 ? "BUY" : "SELL"; Print(string.Format("HLB HIGH | {0} bias={1:F2}", dir, biasScore)); lastHighAlertBar = CurrentBar; }
            if (prevInRange && !inRange && Math.Abs(biasScore) >= AlertThresholdWarn && CurrentBar != lastBreakoutAlertBar) { string breakDir = biasScore >= 0 ? "UPSIDE" : "DOWNSIDE"; Print(string.Format("HLB BREAKOUT {0} | score={1:F2}", breakDir, biasScore)); lastBreakoutAlertBar = CurrentBar; }
            prevAboveWarn = aboveWarn; prevAboveHigh = aboveHigh;
        }

        #region Properties

        [NinjaScriptProperty][Range(1, int.MaxValue)][Display(Name = "Block Threshold (contracts)", GroupName = "Hidden Liquidity", Order = 1)] public int BlockThreshold { get; set; }
        [NinjaScriptProperty][Range(50, 10000)][Display(Name = "Iceberg Time Window (ms)", GroupName = "Hidden Liquidity", Order = 2)] public int IcebergTimeWindow { get; set; }
        [NinjaScriptProperty][Range(1, 500)][Display(Name = "Iceberg Max Clip Size", GroupName = "Hidden Liquidity", Order = 3)] public int IcebergMaxClipSize { get; set; }
        [NinjaScriptProperty][Range(2, 50)][Display(Name = "Iceberg Min Clips", GroupName = "Hidden Liquidity", Order = 4)] public int IcebergMinClips { get; set; }
        [NinjaScriptProperty][Range(1, int.MaxValue)][Display(Name = "Iceberg Min Total (contracts)", GroupName = "Hidden Liquidity", Order = 5)] public int IcebergMinTotal { get; set; }
        [NinjaScriptProperty][Range(1, 1440)][Display(Name = "Lookback (minutes)", GroupName = "Hidden Liquidity", Order = 6)] public int LookbackMinutes { get; set; }
        [NinjaScriptProperty][Range(0.0, 1.0)][Display(Name = "Response Weight", GroupName = "Scoring", Order = 1)] public double ResponseWeight { get; set; }
        [NinjaScriptProperty][Range(0.0, 2.0)][Display(Name = "Location Bias Strength", GroupName = "Scoring", Order = 2)] public double LocationBiasStrength { get; set; }
        [NinjaScriptProperty][Range(0.0, 1.0)][Display(Name = "Warn Threshold", GroupName = "Alerts", Order = 1)] public double AlertThresholdWarn { get; set; }
        [NinjaScriptProperty][Range(0.0, 1.0)][Display(Name = "High Threshold", GroupName = "Alerts", Order = 2)] public double AlertThresholdHigh { get; set; }
        [NinjaScriptProperty][Display(Name = "Enable Alerts", GroupName = "Alerts", Order = 3)] public bool EnableAlerts { get; set; }
        [NinjaScriptProperty][Range(1, 200)][Display(Name = "ATR Period", GroupName = "Range Detection", Order = 1)] public int AtrPeriod { get; set; }
        [NinjaScriptProperty][Range(0.1, 10.0)][Display(Name = "ATR Multiplier", GroupName = "Range Detection", Order = 2)] public double AtrMultiplier { get; set; }
        [NinjaScriptProperty][Range(1, 200)][Display(Name = "ATR Lookback Bars", GroupName = "Range Detection", Order = 3)] public int AtrLookback { get; set; }
        [NinjaScriptProperty][Range(1, 500)][Display(Name = "Range Lookback Bars", GroupName = "Range Detection", Order = 4)] public int RangeLookbackBars { get; set; }
        [NinjaScriptProperty][Range(1, 500)][Display(Name = "Max Range Ticks", GroupName = "Range Detection", Order = 5)] public int MaxRangeTicks { get; set; }
        [NinjaScriptProperty][Range(0, 20)][Display(Name = "Breakout Confirm Ticks", GroupName = "Range Detection", Order = 6)] public int BreakoutConfirmTicks { get; set; }
        [NinjaScriptProperty][Range(1, 20)][Display(Name = "Breakout Confirm Bars", GroupName = "Range Detection", Order = 7)] public int BreakoutConfirmBars { get; set; }
        [NinjaScriptProperty][Range(1, 20)][Display(Name = "Max Saved Ranges", GroupName = "Range Detection", Order = 8)] public int MaxSavedRanges { get; set; }
        [NinjaScriptProperty][Display(Name = "Enable Highlights", GroupName = "Highlight", Order = 1)] public bool EnableHighlights { get; set; }
        [NinjaScriptProperty][Display(Name = "Show on Price Chart", GroupName = "Highlight", Order = 2)] public bool ShowPriceChartHighlight { get; set; }
        [NinjaScriptProperty][Display(Name = "Show on Oscillator", GroupName = "Highlight", Order = 3)] public bool ShowOscillatorHighlight { get; set; }
        [NinjaScriptProperty][Range(0, 100)][Display(Name = "Highlight Opacity %", GroupName = "Highlight", Order = 4)] public int HighlightOpacity { get; set; }
        [NinjaScriptProperty][Range(0, 255)][Display(Name = "Highlight R", GroupName = "Highlight", Order = 5)] public int HighlightR { get; set; }
        [NinjaScriptProperty][Range(0, 255)][Display(Name = "Highlight G", GroupName = "Highlight", Order = 6)] public int HighlightG { get; set; }
        [NinjaScriptProperty][Range(0, 255)][Display(Name = "Highlight B", GroupName = "Highlight", Order = 7)] public int HighlightB { get; set; }
        [NinjaScriptProperty][Display(Name = "Show Histogram Bars", GroupName = "Visual", Order = 1)] public bool ShowHistogramBars { get; set; }
        [NinjaScriptProperty][Display(Name = "Show Bias Line", GroupName = "Visual", Order = 2)] public bool ShowBiasLine { get; set; }
        [NinjaScriptProperty][Display(Name = "Show Event Dots", GroupName = "Visual", Order = 3)] public bool ShowEventDots { get; set; }
        [NinjaScriptProperty][Display(Name = "Show Threshold Bands", GroupName = "Visual", Order = 4)] public bool ShowThresholdBands { get; set; }

        [XmlIgnore][Display(Name = "Histogram Positive Color", GroupName = "Visual", Order = 10)] public Brush HistoPositiveBrush { get; set; }
        [Browsable(false)] public string HistoPositiveBrushSerialize { get { return Serialize.BrushToString(HistoPositiveBrush); } set { HistoPositiveBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "Histogram Negative Color", GroupName = "Visual", Order = 11)] public Brush HistoNegativeBrush { get; set; }
        [Browsable(false)] public string HistoNegativeBrushSerialize { get { return Serialize.BrushToString(HistoNegativeBrush); } set { HistoNegativeBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "Bias Line Buy Color", GroupName = "Visual", Order = 12)] public Brush BiasLineBuyBrush { get; set; }
        [Browsable(false)] public string BiasLineBuyBrushSerialize { get { return Serialize.BrushToString(BiasLineBuyBrush); } set { BiasLineBuyBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "Bias Line Sell Color", GroupName = "Visual", Order = 13)] public Brush BiasLineSellBrush { get; set; }
        [Browsable(false)] public string BiasLineSellBrushSerialize { get { return Serialize.BrushToString(BiasLineSellBrush); } set { BiasLineSellBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "Dot Buy Color", GroupName = "Visual", Order = 14)] public Brush DotBuyBrush { get; set; }
        [Browsable(false)] public string DotBuyBrushSerialize { get { return Serialize.BrushToString(DotBuyBrush); } set { DotBuyBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "Dot Sell Color", GroupName = "Visual", Order = 15)] public Brush DotSellBrush { get; set; }
        [Browsable(false)] public string DotSellBrushSerialize { get { return Serialize.BrushToString(DotSellBrush); } set { DotSellBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "Warn Band Color", GroupName = "Visual", Order = 16)] public Brush WarnBandBrush { get; set; }
        [Browsable(false)] public string WarnBandBrushSerialize { get { return Serialize.BrushToString(WarnBandBrush); } set { WarnBandBrush = Serialize.StringToBrush(value); } }
        [XmlIgnore][Display(Name = "High Band Color", GroupName = "Visual", Order = 17)] public Brush HighBandBrush { get; set; }
        [Browsable(false)] public string HighBandBrushSerialize { get { return Serialize.BrushToString(HighBandBrush); } set { HighBandBrush = Serialize.StringToBrush(value); } }

        #endregion
    }
}
