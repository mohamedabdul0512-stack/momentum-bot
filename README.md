# Eight-Hour Daily Bias Trading Bot

A C# trading robot built for cTrader Automate. The project explores how daily market direction can be used to filter trade setups on an eight-hour chart, with position management and account-level risk limits handled by the bot.

## Strategy overview

The bot builds a directional view from daily price action and the relationship between the latest closed daily candle and a 50-period exponential moving average. It then checks closed eight-hour candles for setups that agree with that view. Previous-day high and low levels are also tracked and displayed on the chart.

The aim is to make the rules explicit and repeatable: establish a bias, check for a qualifying setup, apply risk controls, then manage the position according to its progress.

## Position and risk management

Risk and trade limits can be adjusted through the bot’s parameters, including risk per trade, maximum active positions, weekly loss limit, weekly entry limit, and the lookback used to assess price range.

After entry, the bot tracks each position and can adjust its stop-loss as the trade develops. Its management logic includes break-even and trailing stages, handling for older trades that have made limited progress, and a rule for closing the oldest position when the combined open trades reach a specified return threshold. A weekly loss limit can close the bot’s open positions and pause further activity for the week.

## On-chart information

A chart dashboard shows the current daily and EMA biases, whether new entries are allowed, the number of active trades and weekly entries, and the combined floating result expressed in units of initial risk (“R”).

## Technical details

- **Language:** C#
- **Platform:** cTrader Automate (cAlgo API)
- Uses daily and eight-hour market data
- Separates position state and trade-management stages into dedicated data structures
- Draws and cleans up chart objects for the dashboard and price levels

## Project context

This is a personal project focused on implementing and managing a rule-based automated trading strategy. The code demonstrates work with a trading-platform API, market data, event-driven updates, configurable parameters, and stateful position management.

*For demonstration and educational purposes only. It does not guarantee trading results and is not financial advice.*
