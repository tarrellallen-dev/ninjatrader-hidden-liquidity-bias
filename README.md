# Obsidian Flow Hidden Liquidity Bias

A hidden-liquidity oscillator for NinjaTrader 8. It detects block trades and icebergs, then scores
each one by where in the range it happened and what price did afterward, rather than just reporting
that it happened.

Single file, no dependencies, C# / NinjaScript, MIT licensed. From
[Obsidian Flow](https://obsidianflow.tech).

![Hidden Liquidity Bias](docs/screenshots/chart.png)

<sub>ES 4500-tick. The bias score runs in the lower panel: green where accumulated hidden-liquidity
evidence is bullish, red where it is bearish.</sub>

![Bias oscillator detail](docs/screenshots/oscillator.png)

Full workspace view for context:

![Full chart](docs/screenshots/full-chart.png)

<sub>The volume profile on the left and the lower panel are separate tools and are not part of this
repository. Only the green and red bias oscillator is. The shaded ellipses are annotations from the
teaching chart this frame came from.</sub>

> [!IMPORTANT]
> **This indicator plots forward only. It cannot draw on historical bars.**
>
> The bias score is built from live market data events in `OnMarketData`: individual prints, their
> size, and their timing. NinjaTrader does not retain that stream for bars already on the chart when
> the indicator loads, so there is nothing to reconstruct from. Bars before the moment you add it stay
> blank, and that is correct behavior rather than a fault.
>
> There are two ways to use it. Add it to a live chart and it accumulates from that point forward, or
> run Market Replay, which feeds the same tick events so the indicator populates exactly as it would
> live. Replay is how you study it on past sessions.
>
> This also means it cannot be backtested in the Strategy Analyzer. Replay is the only route to
> historical behavior.

## What it does

Large resting orders leave two fingerprints in the tape. A block is a single oversized print. An
iceberg is the same price refilling over and over in small clips, which is one large order in
disguise. Detecting either is well understood and not the interesting part. What matters is what you
do with the detection.

An iceberg absorbed at the top of a balanced range means something different from the same iceberg
absorbed at the low. So each event gets scored on two axes:

- **Location.** Where in the current developing range the event occurred.
- **Response.** What price actually did in the window afterward. Absorption that holds scores
  differently from absorption that gets run over.

Those combine into one signed bias score, plotted as a histogram. Positive means accumulated bullish
hidden-liquidity evidence, negative means bearish. Threshold bands mark where the score has gotten
interesting, and detected events are dotted onto the plot so a score can be traced back to the prints
that produced it.

Range detection is ATR-based with explicit breakout confirmation. The current range is measured rather
than a fixed lookback, and a range that breaks gets retired instead of quietly distorting the location
term.

## Implementation notes

**Iceberg detection is a time-windowed aggregation, not a size test.** A block is trivially a print
over a threshold. An iceberg is N separate clips, each under a maximum size, at the same price, inside
a rolling millisecond window, summing past a total. Four parameters have to agree, and the window is
the one that matters. Widen it and ordinary two-way trade at a level starts looking like an iceberg.
Candidates live in a fixed-size pool and expire out of it, so nothing allocates per tick.

**Fixed-size ring buffers throughout.** `OnMarketData` fires on every tick of every instrument on the
chart. Liquidity events go into a preallocated ring (`liqBuf`, `liqHead`, `liqCount`) and iceberg
candidates into a fixed pool. A `List<T>` that grows across a session is the standard way a
tick-driven NinjaScript indicator becomes the reason a chart stutters after two hours.

**Scoring decays on a time window, not on bar close.** Hidden liquidity does not stop mattering
because a bar closed. Events age out on a minutes-based window, so the score behaves the same on a
500-tick chart as on a 5-minute one.

**Custom SharpDX `OnRender` with cached device brushes.** Histogram bars, event dots, threshold bands,
and range highlights are drawn directly. Direct2D brushes are expensive to create, so they are cached
against their parameters (`dxCachedHlOpacity`, `dxCachedHlR/G/B`) and rebuilt only when a color
actually changes, rather than per frame.

**Two-axis scoring instead of a boolean signal.** A simple "iceberg here" marker is easier to build
and much less useful, because it throws away the two things that determine whether the event mattered.
Keeping location and response as separately weighted terms (`LocationBiasStrength`, `ResponseWeight`)
keeps the output interpretable and lets each contribution be isolated.

## Parameters

| Setting | Purpose |
|---|---|
| `Block Threshold (contracts)` | Single-print size that qualifies as a block. |
| `Iceberg Time Window (ms)` | Rolling window clips must fall inside to count as one order. |
| `Iceberg Max Clip Size` | Upper bound per clip. Above this it is a block, not an iceberg. |
| `Iceberg Min Clips` | How many refills before a candidate is confirmed. |
| `Iceberg Min Total (contracts)` | Aggregate size the clips must reach. |
| `Lookback (minutes)` | How long an event keeps contributing to the score. |
| `Response Weight` | Weight of the price-response term. |
| `Location Bias Strength` | Weight of the location-in-range term. |
| `ATR Period / Multiplier / Lookback` | Range detection sizing. |
| `Range Lookback Bars / Max Range Ticks` | Bounds on what counts as a range. |
| `Breakout Confirm Ticks / Bars` | Confirmation before a range is retired. |
| `Warn / High Threshold` | Band levels and alert trigger points. |
| `Highlight` group | Range shading: opacity and RGB, on price chart and oscillator. |
| `Visual` group | Histogram bars, bias line, event dots, threshold bands. |

## Install

1. Download `src/HiddenLiquidityBias.cs`.
2. In NinjaTrader 8, open **New > NinjaScript Editor**, right-click **Indicators > New Indicator**, and
   paste the file contents over the generated stub. You can also drop the `.cs` file straight into
   `Documents\NinjaTrader 8\bin\Custom\Indicators\`.
3. Compile with F5. NinjaTrader regenerates its own wrapper code on compile, which is why the
   generated region is not included here.
4. Add **Obsidian Flow Hidden Liquidity Bias** to a chart from **Indicators**.
5. Let it run, or start Market Replay. The panel stays empty on bars that preloaded, which is expected.
   See the note at the top.

Requires NinjaTrader 8 and a data feed with tick-level market data. No Order Flow + subscription is
required. Works on any bar type.

## Background

This was the original flagship indicator behind [Obsidian Flow](https://obsidianflow.tech). I retired
it from the commercial suite and released it in full, with the licensing code removed.

Block and iceberg detection is not a proprietary technique and anyone can arrive at the formula. The
part worth reading is the engineering around it: the aggregation strategy, the allocation discipline
under tick load, and the render caching.

## Disclaimer

This is a visualization and research tool. It is not trading advice, it does not generate orders, and
nothing here is a claim about profitability. Futures trading carries substantial risk of loss. Test on
simulated data before using anything on a live account.

## License

MIT. See [LICENSE](LICENSE). Built by Tarrell Allen.
