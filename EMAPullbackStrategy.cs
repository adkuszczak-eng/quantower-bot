using System;
using System.Collections.Generic;
using TradingPlatform.BusinessLayer;

namespace Quantower.Strategies
{
    /// <summary>
    /// EMA(50) trend-following pullback strategy.
    ///
    /// Entry concept:
    /// - Trade only in EMA trend direction.
    /// - Wait for a pullback candle that touches/crosses EMA.
    /// - Enter on next candle close confirmation back in trend direction.
    ///
    /// Exit concept:
    /// - Close long if close < EMA.
    /// - Close short if close > EMA.
    /// </summary>
    public sealed class EMAPullbackStrategy : Strategy
    {
        #region Parameters

        [InputParameter("EMA period", 10, 1, 500, 1, 0)]
        public int EMA_Period = 50;

        [InputParameter("Position size", 20, 0.01, 100000, 0.01, 2)]
        public double PositionSize = 1;

        [InputParameter("Stop Loss (ticks)", 30, 1, 10000, 1, 0)]
        public int StopLossTicks = 25;

        [InputParameter("Take Profit (ticks)", 40, 1, 10000, 1, 0)]
        public int TakeProfitTicks = 50;

        [InputParameter("Min pullback distance from EMA (ticks)", 50, 0, 1000, 1, 0)]
        public int MinPullbackDistanceTicks = 5;

        [InputParameter("Min confirmation candle body (ticks)", 60, 0, 1000, 1, 0)]
        public int MinConfirmationBodyTicks = 3;

        [InputParameter("Max trades per trend", 70, 1, 20, 1, 0)]
        public int MaxTradesPerTrend = 2;

        [InputParameter("Max spread (ticks)", 80, 0, 1000, 1, 0)]
        public int MaxSpreadTicks = 10;

        #endregion

        #region Fields

        private HistoricalData history;
        private Indicator ema;

        // Trend tracking: +1 uptrend, -1 downtrend, 0 unknown.
        private int currentTrendDirection;
        private int tradesTakenInCurrentTrend;

        private bool isInitialized;

        // Small state for pullback detection using closed bars.
        private readonly Dictionary<DateTime, bool> processedBars = new Dictionary<DateTime, bool>();

        #endregion

        public EMAPullbackStrategy()
            : base()
        {
            Name = "EMA Pullback Strategy";
            Description = "Trend-following EMA pullback strategy with risk controls and trade-per-trend cap.";
        }

        protected override void OnCreated()
        {
            base.OnCreated();
            Log("Strategy created.", StrategyLoggingLevel.Info);
        }

        /// <summary>
        /// Initialization method required by this strategy design.
        /// Called once from OnRun.
        /// </summary>
        private void OnInit()
        {
            // Request historical data for current symbol/timeframe.
            history = Symbol.GetHistory(Period, HistoryType.Bid, DateTime.UtcNow.AddDays(-20));

            // Build EMA indicator using Quantower built-ins.
            ema = Core.Indicators.BuiltIn.EMA(EMA_Period, PriceType.Close);
            history.AddIndicator(ema);

            currentTrendDirection = 0;
            tradesTakenInCurrentTrend = 0;
            processedBars.Clear();

            isInitialized = true;
            Log($"Initialized. EMA={EMA_Period}, SL={StopLossTicks} ticks, TP={TakeProfitTicks} ticks.", StrategyLoggingLevel.Info);
        }

        protected override void OnRun()
        {
            if (!isInitialized)
                OnInit();

            if (history == null)
            {
                Log("History is not initialized.", StrategyLoggingLevel.Error);
                return;
            }

            history.NewHistoryItem += OnNewHistoryItem;
            Log("Strategy started.", StrategyLoggingLevel.Info);
        }

        protected override void OnStop()
        {
            if (history != null)
                history.NewHistoryItem -= OnNewHistoryItem;

            Log("Strategy stopped.", StrategyLoggingLevel.Info);
        }

        protected override void OnRemove()
        {
            if (history != null)
                history.NewHistoryItem -= OnNewHistoryItem;

            Log("Strategy removed.", StrategyLoggingLevel.Info);
            base.OnRemove();
        }

        protected override List<StrategyMetric> OnGetMetrics()
        {
            return new List<StrategyMetric>
            {
                new StrategyMetric("EMA Period", EMA_Period),
                new StrategyMetric("Trades in current trend", tradesTakenInCurrentTrend),
                new StrategyMetric("Current trend", currentTrendDirection == 1 ? "Up" : currentTrendDirection == -1 ? "Down" : "Unknown")
            };
        }

        private void OnNewHistoryItem(object sender, HistoryEventArgs e)
        {
            try
            {
                // Work only on closed bars.
                // Indexing convention:
                // [0] - currently forming bar, [1] - last closed bar, [2] - previous closed bar.
                if (history == null || history.Count < 3)
                    return;

                var confirmationBar = history[1]; // bar that just closed
                var pullbackBar = history[2];     // prior closed bar

                // Avoid processing same closed bar multiple times.
                if (processedBars.ContainsKey(confirmationBar.TimeLeft))
                    return;

                processedBars[confirmationBar.TimeLeft] = true;

                var close1 = confirmationBar[PriceType.Close];
                var open1 = confirmationBar[PriceType.Open];
                var high2 = pullbackBar[PriceType.High];
                var low2 = pullbackBar[PriceType.Low];
                var close2 = pullbackBar[PriceType.Close];

                var ema1 = ema.GetValue(1);
                var ema2 = ema.GetValue(2);

                if (double.IsNaN(ema1) || double.IsNaN(ema2))
                    return;

                // Determine trend from confirmation bar close against EMA.
                int newTrend = close1 > ema1 ? 1 : close1 < ema1 ? -1 : currentTrendDirection;
                HandleTrendChange(newTrend);

                // Exit rules first.
                ManageOpenPositions(close1, ema1);

                // Do not open if spread filter fails.
                if (!IsSpreadAcceptable())
                {
                    Log("Entry skipped due to spread filter.", StrategyLoggingLevel.Trading);
                    return;
                }

                // Do not exceed max trades per trend.
                if (tradesTakenInCurrentTrend >= MaxTradesPerTrend)
                    return;

                // Prevent multiple positions in same direction simultaneously.
                bool hasLong = HasOpenPosition(Side.Buy);
                bool hasShort = HasOpenPosition(Side.Sell);

                // Distance filter: pullback starts after the market was away from EMA.
                double minDistance = MinPullbackDistanceTicks * Symbol.TickSize;
                double bodyMin = MinConfirmationBodyTicks * Symbol.TickSize;
                double confirmationBody = Math.Abs(close1 - open1);

                // LONG setup:
                // Trend up, pullback bar touches/breaches EMA, confirmation closes above EMA.
                bool upTrend = close1 > ema1;
                bool longPullbackTouch = low2 <= ema2;
                bool longDistanceOk = (close2 - ema2) >= minDistance;
                bool longConfirmation = close1 > ema1 && confirmationBody >= bodyMin;

                if (upTrend && longPullbackTouch && longDistanceOk && longConfirmation && !hasLong)
                {
                    Log("Pullback detected (LONG).", StrategyLoggingLevel.Trading);
                    if (TryPlaceEntry(Side.Buy))
                        tradesTakenInCurrentTrend++;

                    return;
                }

                // SHORT setup:
                // Trend down, pullback bar touches/breaches EMA, confirmation closes below EMA.
                bool downTrend = close1 < ema1;
                bool shortPullbackTouch = high2 >= ema2;
                bool shortDistanceOk = (ema2 - close2) >= minDistance;
                bool shortConfirmation = close1 < ema1 && confirmationBody >= bodyMin;

                if (downTrend && shortPullbackTouch && shortDistanceOk && shortConfirmation && !hasShort)
                {
                    Log("Pullback detected (SHORT).", StrategyLoggingLevel.Trading);
                    if (TryPlaceEntry(Side.Sell))
                        tradesTakenInCurrentTrend++;
                }
            }
            catch (Exception ex)
            {
                Log($"Error in bar processing: {ex.Message}", StrategyLoggingLevel.Error);
            }
        }

        private void HandleTrendChange(int newTrend)
        {
            if (newTrend == 0)
                return;

            if (newTrend != currentTrendDirection)
            {
                currentTrendDirection = newTrend;
                tradesTakenInCurrentTrend = 0;
                Log(currentTrendDirection == 1 ? "Trend detected: UP" : "Trend detected: DOWN", StrategyLoggingLevel.Info);
            }
        }

        private void ManageOpenPositions(double lastClose, double emaValue)
        {
            foreach (var position in Core.Positions)
            {
                if (position.Symbol != Symbol || position.Account != Account)
                    continue;

                if (position.Side == Side.Buy && lastClose < emaValue)
                {
                    var closeResult = position.Close();
                    if (closeResult != null && closeResult.Status == TradingOperationResultStatus.Success)
                        Log("Trade closed (LONG exit: close below EMA).", StrategyLoggingLevel.Trading);
                    else
                        Log("Failed to close LONG position.", StrategyLoggingLevel.Error);
                }
                else if (position.Side == Side.Sell && lastClose > emaValue)
                {
                    var closeResult = position.Close();
                    if (closeResult != null && closeResult.Status == TradingOperationResultStatus.Success)
                        Log("Trade closed (SHORT exit: close above EMA).", StrategyLoggingLevel.Trading);
                    else
                        Log("Failed to close SHORT position.", StrategyLoggingLevel.Error);
                }
            }
        }

        private bool TryPlaceEntry(Side side)
        {
            var request = new PlaceOrderRequestParameters
            {
                Account = Account,
                Symbol = Symbol,
                Side = side,
                Quantity = PositionSize,
                OrderTypeId = OrderType.Market,
                TimeInForce = TimeInForce.Day,
                StopLoss = SlTpHolder.CreateSL(StopLossTicks, PriceMeasurement.Offset),
                TakeProfit = SlTpHolder.CreateTP(TakeProfitTicks, PriceMeasurement.Offset)
            };

            var result = Core.PlaceOrder(request);
            if (result != null && result.Status == TradingOperationResultStatus.Success)
            {
                Log($"Trade opened: {side} {PositionSize} @ market. SL={StopLossTicks} TP={TakeProfitTicks}", StrategyLoggingLevel.Trading);
                return true;
            }

            Log($"Order placement failed for {side}.", StrategyLoggingLevel.Error);
            return false;
        }

        private bool HasOpenPosition(Side side)
        {
            foreach (var position in Core.Positions)
            {
                if (position.Symbol == Symbol && position.Account == Account && position.Side == side)
                    return true;
            }

            return false;
        }

        private bool IsSpreadAcceptable()
        {
            double spread = Symbol.Ask - Symbol.Bid;
            double maxSpread = MaxSpreadTicks * Symbol.TickSize;
            return spread <= maxSpread;
        }
    }
}
