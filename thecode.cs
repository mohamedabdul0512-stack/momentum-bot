using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
[Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
public class EightHourDailyBiasBot : Robot
{
[Parameter("Risk %", DefaultValue = 1)]
public double RiskPercent { get; set; }
    [Parameter("Max Active Trades", DefaultValue = 5)]
    public int MaxTrades { get; set; }

    [Parameter("Weekly Loss %", DefaultValue = 5)]
    public double WeeklyLossPercent { get; set; }

    [Parameter("Max Entries Per Week", DefaultValue = 5)]
    public int MaxWeeklyEntries { get; set; }

    [Parameter("Range Lookback", DefaultValue = 50)]
    public int RangeLookback { get; set; }

    [Parameter("Max Candle Size Multiplier", DefaultValue = 2.0)]
    public double MaxCandleMultiplier { get; set; }

    private const string label = "8H_DAILY_BIAS";
    private const string dashboardId = "DASH";
    private const string pdhLineId = "PDH_LINE";
    private const string pdlLineId = "PDL_LINE";

    private const int minimumDailyBarsForEma = 55;
    private const int minimumDailyBarsForBias = 4;
    private const int minimumStopBufferPips = 5;
    private const int maxBiasLookbackBars = 30;
    private const int sessionStartHour = 0;
    private const int sessionEndHour = 16;

    private const double basketCloseThresholdR = 10.0;
    private const double breakEvenThresholdR = 2.0;
    private const double momentumAgeDays = 2.0;
    private const double slowTradeAgeDays = 2.0;
    private const double slowTradeCloseThresholdR = 0.2;
    private const double slowTradeReduceThresholdR = 0.5;
    private const double breakEvenNetProfitTolerance = 0.5;
    private const double trailingAfter25ROffset = 5.0;
    private const double stopMoveMinPips = 0.5;

    private Bars daily;
    private Bars eightH;
    private ExponentialMovingAverage ema50;

    private TradeType? dailyBias;
    private TradeType? emaBias;

    private bool weeklyLossLock;
    private DateTime lastDay;
    private double weekStartBalance;
    private int lastWeek = -1;
    private int entriesThisWeek;
    private DateTime lastEightHProcessed = DateTime.MinValue;
    private DateTime lastProcessedDailyClosedBarTime = DateTime.MinValue;
    private DateTime lastAlignmentTrackedBarTime = DateTime.MinValue;

    private double pdh;
    private double pdl;

    private int consecutiveLosses;
    private int consecutiveBreakevens;
    private int consecutiveAlignedDays;

    private bool skipNextValidSetup;
    private bool doubleRiskNextTrade;
    private bool alignmentDoubleRiskArmed;
    private DateTime alignmentDoubleRiskArmedDate = DateTime.MinValue;

    private readonly Dictionary<long, PositionContext> contexts = new Dictionary<long, PositionContext>();
    private readonly HashSet<long> reducedTrades = new HashSet<long>();
    private readonly HashSet<long> breakEvenLockedTrades = new HashSet<long>();
    private readonly List<string> chartObjects = new List<string>();

    private sealed class PositionContext
    {
        public long PositionId { get; set; }
        public double RiskDistance { get; set; }
        public DateTime OpenTime { get; set; }
        public TradeStage Stage { get; set; }
        public bool MomentumApplied { get; set; }
        public bool SlowTradeReduced { get; set; }
        public bool BreakEvenLocked { get; set; }
        public double? LastStopLoss { get; set; }
    }

    private enum TradeStage
    {
        Initial = 0,
        BreakEven = 1,
        PlusHalf = 2,
        Trail2R = 3,
        Trail5R = 4,
        MomentumReduced = 5,
        Trail20Plus = 6
    }

    protected override void OnStart()
    {
        daily = MarketData.GetBars(TimeFrame.Daily);
        eightH = MarketData.GetBars(TimeFrame.Hour8);
        ema50 = Indicators.ExponentialMovingAverage(daily.ClosePrices, 50);

        lastDay = Server.Time.Date;
        weekStartBalance = Account.Balance;
        lastWeek = GetWeek();

        Positions.Closed += OnPositionClosed;

        HydrateContextsFromOpenPositions();
        UpdateDailyContextIfNeeded(true);
        DrawPDLines();
        DrawDashboard();
    }

    protected override void OnBar()
    {
        ResetDayIfNeeded();
        ResetWeekIfNeeded();
        UpdateDailyContextIfNeeded(false);
        CleanupOrphanContexts();
    }

    protected override void OnTick()
    {
        UpdateDailyContextIfNeeded(false);

        WeeklyLossProtection();

        if (weeklyLossLock)
        {
            DrawDashboard();
            return;
        }

        CheckNewClosed8HCandle();
        ManageSlowTrades();
        ManageTrailing();
        ApplyMomentumLogic();
        CloseOnBasketProfit();
        CleanupOrphanContexts();

        DrawDashboard();
    }

    protected override void OnStop()
    {
        Positions.Closed -= OnPositionClosed;

        ClearTrackedObjects();

        SafeRemoveChartObject(dashboardId);
        SafeRemoveChartObject(pdhLineId);
        SafeRemoveChartObject(pdlLineId);
    }

    private void HydrateContextsFromOpenPositions()
    {
        foreach (var position in GetBotPositions())
        {
            if (contexts.ContainsKey(position.Id))
                continue;

            var context = new PositionContext
            {
                PositionId = position.Id,
                OpenTime = position.EntryTime,
                RiskDistance = position.StopLoss.HasValue
                    ? Math.Abs(position.EntryPrice - position.StopLoss.Value)
                    : 0,
                Stage = TradeStage.Initial,
                MomentumApplied = false,
                SlowTradeReduced = false,
                BreakEvenLocked = false,
                LastStopLoss = position.StopLoss
            };

            contexts[position.Id] = context;

            if (IsBreakEvenClosed(position))
                breakEvenLockedTrades.Add(position.Id);
        }
    }

    private void CleanupOrphanContexts()
    {
        var openIds = new HashSet<long>();

        foreach (var p in GetBotPositions())
            openIds.Add(p.Id);

        foreach (var id in contexts.Keys.ToArray())
        {
            if (!openIds.Contains(id))
                contexts.Remove(id);
        }

        foreach (var id in reducedTrades.ToArray())
        {
            if (!openIds.Contains(id))
                reducedTrades.Remove(id);
        }

        foreach (var id in breakEvenLockedTrades.ToArray())
        {
            if (!openIds.Contains(id))
                breakEvenLockedTrades.Remove(id);
        }
    }

    private void ResetDayIfNeeded()
    {
        if (Server.Time.Date == lastDay)
            return;

        lastDay = Server.Time.Date;
        lastEightHProcessed = DateTime.MinValue;

        if (alignmentDoubleRiskArmed && alignmentDoubleRiskArmedDate != Server.Time.Date)
        {
            alignmentDoubleRiskArmed = false;
            alignmentDoubleRiskArmedDate = DateTime.MinValue;
        }

        ClearTrackedObjects();
        DrawPDLines();
    }

    private void ResetWeekIfNeeded()
    {
        int week = GetWeek();

        if (week == lastWeek)
            return;

        lastWeek = week;
        weekStartBalance = Account.Balance;
        weeklyLossLock = false;
        entriesThisWeek = 0;
    }

    private void UpdateDailyContextIfNeeded(bool force)
    {
        if (daily == null || daily.Count < 2)
            return;

        var lastClosedDailyBarTime = daily.Last(1).OpenTime;

        if (!force && lastClosedDailyBarTime == lastProcessedDailyClosedBarTime)
            return;

        lastProcessedDailyClosedBarTime = lastClosedDailyBarTime;

        UpdateEMABias();
        UpdateBias();
        UpdatePDLevels();
        UpdateAlignmentDoubleRiskState(force);
        DrawPDLines();
    }

    private void UpdateAlignmentDoubleRiskState(bool force)
    {
        if (force)
        {
            lastAlignmentTrackedBarTime = lastProcessedDailyClosedBarTime;
            return;
        }

        if (lastAlignmentTrackedBarTime == lastProcessedDailyClosedBarTime)
            return;

        lastAlignmentTrackedBarTime = lastProcessedDailyClosedBarTime;

        bool aligned = dailyBias.HasValue &&
                       emaBias.HasValue &&
                       dailyBias == emaBias;

        if (aligned)
        {
            consecutiveAlignedDays++;

            if (consecutiveAlignedDays >= 3)
            {
                alignmentDoubleRiskArmed = true;
                alignmentDoubleRiskArmedDate = Server.Time.Date;
                consecutiveAlignedDays = 0;
            }
        }
        else
        {
            consecutiveAlignedDays = 0;
            alignmentDoubleRiskArmed = false;
            alignmentDoubleRiskArmedDate = DateTime.MinValue;
        }
    }

    private void UpdateEMABias()
    {
        if (daily.Count < minimumDailyBarsForEma)
        {
            emaBias = null;
            return;
        }

        var lastClosedDaily = daily.Last(1);
        double ema = ema50.Result.Last(1);

        if (lastClosedDaily.Close > ema)
            emaBias = TradeType.Buy;
        else if (lastClosedDaily.Close < ema)
            emaBias = TradeType.Sell;
        else
            emaBias = null;
    }

    private void UpdateBias()
    {
        if (daily.Count < minimumDailyBarsForBias)
        {
            dailyBias = null;
            return;
        }

        int lookbackLimit = Math.Min(daily.Count - 1, maxBiasLookbackBars);

        for (int offset = 1; offset < lookbackLimit; offset++)
        {
            var current = daily.Last(offset);
            var previous = daily.Last(offset + 1);

            bool inside = current.High <= previous.High && current.Low >= previous.Low;
            bool outside = current.High > previous.High && current.Low < previous.Low;

            if (outside)
            {
                dailyBias = null;
                return;
            }

            if (inside)
                continue;

            bool brokeHigh = current.High > previous.High;
            bool brokeLow = current.Low < previous.Low;

            if (brokeHigh && !brokeLow)
                dailyBias = TradeType.Buy;
            else if (brokeLow && !brokeHigh)
                dailyBias = TradeType.Sell;
            else
                dailyBias = current.Close > current.Open
                    ? TradeType.Buy
                    : TradeType.Sell;

            return;
        }

        dailyBias = null;
    }

    private void UpdatePDLevels()
    {
        if (daily.Count < 2)
            return;

        var prev = daily.Last(1);

        pdh = prev.High;
        pdl = prev.Low;
    }

    private void DrawPDLines()
    {
        SafeRemoveChartObject(pdhLineId);
        SafeRemoveChartObject(pdlLineId);

        Chart.DrawHorizontalLine(pdhLineId, pdh, Color.Blue);
        Chart.DrawHorizontalLine(pdlLineId, pdl, Color.Blue);

        TrackChartObject(pdhLineId);
        TrackChartObject(pdlLineId);
    }

    private void CheckNewClosed8HCandle()
    {
        if (weeklyLossLock)
            return;

        if (!dailyBias.HasValue || !emaBias.HasValue)
            return;

        if (dailyBias != emaBias)
            return;

        if (!CanTakeNewEntry())
            return;

        if (entriesThisWeek >= MaxWeeklyEntries)
            return;

        if (eightH.Count < 2)
            return;

        var candle = eightH.Last(1);

        if (candle.OpenTime == lastEightHProcessed)
            return;

        if (!IsAllowedSession(candle.OpenTime))
            return;

        if (skipNextValidSetup)
        {
            skipNextValidSetup = false;
            lastEightHProcessed = candle.OpenTime;
            return;
        }

        if (IsOversizedCandle(candle))
            return;

        bool buySignal = dailyBias == TradeType.Buy && candle.Close > candle.Open;
        bool sellSignal = dailyBias == TradeType.Sell && candle.Close < candle.Open;

        if (buySignal)
        {
            lastEightHProcessed = candle.OpenTime;
            ExecuteEntry(candle, TradeType.Buy);
        }
        else if (sellSignal)
        {
            lastEightHProcessed = candle.OpenTime;
            ExecuteEntry(candle, TradeType.Sell);
        }
    }

    private bool IsAllowedSession(DateTime time)
    {
        int h = time.Hour;
        return h >= sessionStartHour && h < sessionEndHour;
    }

    private bool IsOversizedCandle(Bar candle)
    {
        if (RangeLookback <= 0 || MaxCandleMultiplier <= 0)
            return false;

        if (eightH.Count < RangeLookback + 2)
            return false;

        double totalRange = 0;

        for (int i = 2; i < RangeLookback + 2; i++)
        {
            var bar = eightH.Last(i);
            totalRange += bar.High - bar.Low;
        }

        double averageRange = totalRange / RangeLookback;

        if (averageRange <= 0)
            return false;

        double currentRange = candle.High - candle.Low;

        return currentRange > averageRange * MaxCandleMultiplier;
    }

    private bool CanTakeNewEntry()
    {
        var positions = GetBotPositions();

        if (positions.Count >= MaxTrades)
            return false;

        if (positions.Count == 0)
            return true;

        foreach (var p in positions)
        {
            if (p.EntryTime.Date == Server.Time.Date)
                return false;
        }

        foreach (var p in positions)
        {
            if (!p.StopLoss.HasValue)
                return false;

            bool protectedTrade =
                (p.TradeType == TradeType.Buy && p.StopLoss.Value >= p.EntryPrice) ||
                (p.TradeType == TradeType.Sell && p.StopLoss.Value <= p.EntryPrice);

            if (!protectedTrade)
                return false;
        }

        return true;
    }

    private void ExecuteEntry(Bar candle, TradeType type)
    {
        bool specialTrade = doubleRiskNextTrade ||
                            (alignmentDoubleRiskArmed && alignmentDoubleRiskArmedDate == Server.Time.Date);

        double entryPrice = type == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
        double stopPrice = type == TradeType.Buy ? candle.Low : candle.High;
        double stopDistance = Math.Abs(entryPrice - stopPrice);

        if (stopDistance <= Symbol.PipSize * minimumStopBufferPips)
            return;

        double stopLossPips = stopDistance / Symbol.PipSize;

        if (stopLossPips <= 0)
            return;

        double effectiveRiskPercent = RiskPercent * (specialTrade ? 2.0 : 1.0);

        if (effectiveRiskPercent <= 0)
            return;

        double riskAmount = Account.Balance * effectiveRiskPercent / 100.0;

        if (riskAmount <= 0)
            return;

        double volumeInUnits = Symbol.VolumeForFixedRisk(riskAmount, stopLossPips, RoundingMode.Down);

        volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);

        volumeInUnits = Math.Max(Symbol.VolumeInUnitsMin,
                                 Math.Min(Symbol.VolumeInUnitsMax, volumeInUnits));

        var result = ExecuteMarketOrder(type,
                                        SymbolName,
                                        volumeInUnits,
                                        label,
                                        stopLossPips,
                                        null);

        if (!result.IsSuccessful)
            return;

        doubleRiskNextTrade = false;
        alignmentDoubleRiskArmed = false;
        alignmentDoubleRiskArmedDate = DateTime.MinValue;

        var pos = result.Position;

        var context = new PositionContext
        {
            PositionId = pos.Id,
            RiskDistance = stopDistance,
            OpenTime = Server.Time,
            Stage = TradeStage.Initial,
            MomentumApplied = false,
            SlowTradeReduced = false,
            BreakEvenLocked = false,
            LastStopLoss = pos.StopLoss
        };

        contexts[pos.Id] = context;

        entriesThisWeek++;

        DrawEntryVisuals(pos, candle, type);
    }

    private void DrawEntryVisuals(Position pos, Bar candle, TradeType type)
    {
        string rectId = "ENTRY_RECT_" + pos.Id;
        SafeRemoveChartObject(rectId);

        Chart.DrawRectangle(rectId,
                            candle.OpenTime,
                            candle.Low,
                            candle.OpenTime.AddHours(8),
                            candle.High,
                            Color.FromArgb(40, Color.Yellow));

        TrackChartObject(rectId);

        string lineId = "ENTRY_LINE_" + pos.Id;
        SafeRemoveChartObject(lineId);

        Color lineColor = type == TradeType.Buy ? Color.LimeGreen : Color.Red;

        Chart.DrawHorizontalLine(lineId, pos.EntryPrice, lineColor);

        TrackChartObject(lineId);

        if (pos.StopLoss.HasValue)
        {
            string slId = "SL_LINE_" + pos.Id;

            SafeRemoveChartObject(slId);

            Chart.DrawHorizontalLine(slId, pos.StopLoss.Value, Color.OrangeRed);

            TrackChartObject(slId);
        }
    }

    private double? GetTargetLockR(double currentR)
    {
        if (currentR >= 25.0)
            return Math.Max(20.0, currentR - trailingAfter25ROffset);

        if (currentR >= 20.0)
            return 15.0;

        if (currentR >= 15.0)
            return 10.0;

        if (currentR >= 10.0)
            return 7.0;

        if (currentR >= 7.0)
            return 5.0;

        if (currentR >= 5.0)
            return 2.0;

        if (currentR >= 3.0)
            return 1.0;

        if (currentR >= 2.0)
            return 0.0;

        return null;
    }

    private bool ShouldMoveStopLoss(Position position, double newStopLossPrice)
    {
        if (!position.StopLoss.HasValue)
            return true;

        double minDelta = Symbol.PipSize * stopMoveMinPips;

        if (position.TradeType == TradeType.Buy)
            return newStopLossPrice > position.StopLoss.Value + minDelta;

        return newStopLossPrice < position.StopLoss.Value - minDelta;
    }

    private void ManageTrailing()
    {
        var positions = GetBotPositions();

        foreach (var p in positions)
        {
            PositionContext context;

            if (!contexts.TryGetValue(p.Id, out context))
                continue;

            double currentR = CalculateCurrentR(p, context.RiskDistance);

            if (double.IsNaN(currentR))
                continue;

            double? targetLockR = GetTargetLockR(currentR);

            if (!targetLockR.HasValue)
                continue;

            double newSL = p.TradeType == TradeType.Buy
                ? p.EntryPrice + (targetLockR.Value * context.RiskDistance)
                : p.EntryPrice - (targetLockR.Value * context.RiskDistance);

            if (!ShouldMoveStopLoss(p, newSL))
                continue;

            if (TryModifyStopLoss(p, newSL))
            {
                context.LastStopLoss = newSL;

                if (currentR >= 25.0)
                    context.Stage = TradeStage.Trail20Plus;
                else if (currentR >= 7.0)
                    context.Stage = TradeStage.Trail5R;
                else if (currentR >= 5.0)
                    context.Stage = TradeStage.Trail2R;
                else if (currentR >= 3.0)
                    context.Stage = TradeStage.PlusHalf;
                else if (currentR >= 2.0)
                    context.Stage = TradeStage.BreakEven;

                if (currentR >= breakEvenThresholdR)
                {
                    context.BreakEvenLocked = true;
                    breakEvenLockedTrades.Add(p.Id);
                }

                contexts[p.Id] = context;
            }
        }
    }

    private void ManageSlowTrades()
    {
        var positions = GetBotPositions();

        foreach (var p in positions)
        {
            PositionContext context;

            if (!contexts.TryGetValue(p.Id, out context))
                continue;

            double r = context.RiskDistance;

            if (r <= 0)
                continue;

            double price = p.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;

            double profit = p.TradeType == TradeType.Buy
                ? price - p.EntryPrice
                : p.EntryPrice - price;

            double profitR = profit / r;

            if ((Server.Time - context.OpenTime).TotalDays < slowTradeAgeDays)
                continue;

            if (profitR <= slowTradeCloseThresholdR)
            {
                TryClosePosition(p);
                continue;
            }

            if (profitR > slowTradeCloseThresholdR &&
                profitR <= slowTradeReduceThresholdR)
            {
                double newSL = p.TradeType == TradeType.Buy
                    ? p.EntryPrice - (0.5 * r)
                    : p.EntryPrice + (0.5 * r);

                if (!p.StopLoss.HasValue ||
                    (p.TradeType == TradeType.Buy && newSL > p.StopLoss.Value) ||
                    (p.TradeType == TradeType.Sell && newSL < p.StopLoss.Value))
                {
                    if (TryModifyStopLoss(p, newSL))
                    {
                        context.SlowTradeReduced = true;
                        context.LastStopLoss = newSL;

                        reducedTrades.Add(p.Id);
                        contexts[p.Id] = context;
                    }
                }
            }
        }
    }

    private void ApplyMomentumLogic()
    {
        var positions = GetBotPositions();

        foreach (var p in positions)
        {
            PositionContext context;

            if (!contexts.TryGetValue(p.Id, out context))
                continue;

            if ((Server.Time - context.OpenTime).TotalDays < momentumAgeDays)
                continue;

            double currentR = CalculateCurrentR(p, context.RiskDistance);

            if (double.IsNaN(currentR))
                continue;

            if (currentR >= breakEvenThresholdR)
                continue;

            if (context.BreakEvenLocked || breakEvenLockedTrades.Contains(p.Id))
                continue;

            double reducedStop = p.TradeType == TradeType.Buy
                ? p.EntryPrice - (0.5 * context.RiskDistance)
                : p.EntryPrice + (0.5 * context.RiskDistance);

            if (!p.StopLoss.HasValue ||
                (p.TradeType == TradeType.Buy && reducedStop > p.StopLoss.Value) ||
                (p.TradeType == TradeType.Sell && reducedStop < p.StopLoss.Value))
            {
                if (TryModifyStopLoss(p, reducedStop))
                {
                    context.MomentumApplied = true;
                    context.Stage = TradeStage.MomentumReduced;
                    context.LastStopLoss = reducedStop;

                    contexts[p.Id] = context;
                }
            }
        }
    }

    private void CloseOnBasketProfit()
    {
        var positions = GetBotPositions();

        if (!positions.Any())
            return;

        double totalR = CalculateFloatingR(positions);

        if (totalR < basketCloseThresholdR)
            return;

        Position oldest = null;
        DateTime oldestTime = DateTime.MaxValue;

        foreach (var p in positions)
        {
            if (p.EntryTime < oldestTime)
            {
                oldestTime = p.EntryTime;
                oldest = p;
            }
        }

        if (oldest != null)
            TryClosePosition(oldest);
    }

    private void WeeklyLossProtection()
    {
        if (weeklyLossLock)
            return;

        double lossLimit = weekStartBalance * WeeklyLossPercent / 100.0;
        double pnl = Account.Equity - weekStartBalance;

        if (pnl <= -lossLimit)
        {
            foreach (var p in GetBotPositions())
                TryClosePosition(p);

            weeklyLossLock = true;
        }
    }

    private void OnPositionClosed(PositionClosedEventArgs args)
    {
        if (args.Position.Label != label || args.Position.SymbolName != SymbolName)
            return;

        bool wasBreakEven = IsBreakEvenClosed(args.Position);
        bool wasLoss = args.Position.NetProfit < 0;

        SafeRemoveChartObject("ENTRY_RECT_" + args.Position.Id);
        SafeRemoveChartObject("ENTRY_LINE_" + args.Position.Id);
        SafeRemoveChartObject("SL_LINE_" + args.Position.Id);

        contexts.Remove(args.Position.Id);
        reducedTrades.Remove(args.Position.Id);
        breakEvenLockedTrades.Remove(args.Position.Id);

        if (wasBreakEven)
        {
            consecutiveBreakevens++;
            consecutiveLosses = 0;

            if (consecutiveBreakevens >= 2)
            {
                doubleRiskNextTrade = true;
                consecutiveBreakevens = 0;
            }

            return;
        }

        consecutiveBreakevens = 0;

        if (wasLoss)
        {
            consecutiveLosses++;

            if (consecutiveLosses >= 3)
            {
                skipNextValidSetup = true;
                consecutiveLosses = 0;
            }
        }
        else
        {
            consecutiveLosses = 0;
        }
    }

    private bool IsBreakEvenClosed(Position position)
    {
        if (breakEvenLockedTrades.Contains(position.Id))
            return true;

        return Math.Abs(position.NetProfit) <= breakEvenNetProfitTolerance;
    }

    private void DrawDashboard()
    {
        var positions = GetBotPositions();
        double totalR = CalculateFloatingR(positions);

        var sb = new StringBuilder();

        sb.AppendLine("=== 8H DAILY BIAS ENGINE ===");
        sb.AppendLine("Daily Bias: " + BiasToText(dailyBias));
        sb.AppendLine("EMA Bias: " + BiasToText(emaBias));
        sb.AppendLine("--------------------------------");
        sb.AppendLine("Re-entry Allowed: " + CanTakeNewEntry());
        sb.AppendLine("Skip Next Setup: " + skipNextValidSetup);
        sb.AppendLine("Double Risk Next: " + doubleRiskNextTrade);
        sb.AppendLine("Loss Streak: " + consecutiveLosses);
        sb.AppendLine("Active Trades: " + positions.Count);
        sb.AppendLine("Weekly Entries: " + entriesThisWeek + "/" + MaxWeeklyEntries);
        sb.AppendLine("Floating Total R: " + Math.Round(totalR, 2));

        SafeRemoveChartObject(dashboardId);

        Chart.DrawStaticText(dashboardId,
                             sb.ToString(),
                             VerticalAlignment.Top,
                             HorizontalAlignment.Left,
                             Color.White);

        TrackChartObject(dashboardId);
    }

    private static string BiasToText(TradeType? bias)
    {
        if (!bias.HasValue)
            return "NONE";

        return bias == TradeType.Buy ? "BUY" : "SELL";
    }

    private List<Position> GetBotPositions()
    {
        return Positions
            .Where(p => p.Label == label && p.SymbolName == SymbolName)
            .ToList();
    }

    private double CalculateFloatingR(IEnumerable<Position> positions)
    {
        double totalR = 0;

        foreach (var p in positions)
        {
            PositionContext context;

            if (!contexts.TryGetValue(p.Id, out context))
                continue;

            double currentR = CalculateCurrentR(p, context.RiskDistance);

            if (double.IsNaN(currentR))
                continue;

            totalR += currentR;
        }

        return totalR;
    }

    private double CalculateCurrentR(Position p, double riskDistance)
    {
        if (riskDistance <= 0)
            return double.NaN;

        double price = p.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;

        double profit = p.TradeType == TradeType.Buy
            ? price - p.EntryPrice
            : p.EntryPrice - price;

        return profit / riskDistance;
    }

    private bool TryClosePosition(Position position)
    {
        var result = ClosePosition(position);
        return result.IsSuccessful;
    }

    private bool TryModifyStopLoss(Position position, double newStopLossPrice)
    {
        var result = position.ModifyStopLossPrice(newStopLossPrice);
        return result.IsSuccessful;
    }

    private void TrackChartObject(string id)
    {
        if (!chartObjects.Contains(id))
            chartObjects.Add(id);
    }

    private void ClearTrackedObjects()
    {
        foreach (var id in chartObjects.ToArray())
        {
            SafeRemoveChartObject(id);
        }

        chartObjects.Clear();
    }

    private void SafeRemoveChartObject(string id)
    {
        try
        {
            Chart.RemoveObject(id);
        }
        catch
        {
        }
    }

    private int GetWeek()
    {
        int day = (int)Server.Time.DayOfWeek;

        if (day == 0)
            day = 7;

        var thursday = Server.Time.AddDays(4 - day);
        var first = new DateTime(thursday.Year, 1, 1);

        return (int)((thursday - first).TotalDays / 7) + 1;
    }
}
}
