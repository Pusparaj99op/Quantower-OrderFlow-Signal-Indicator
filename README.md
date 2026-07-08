# Quantower OrderFlow Signal Indicator

A .NET 10 Quantower indicator and strategy suite for XAU/USD (GC/MGC) scalping built around order-flow, market structure, and risk-management rules.

## What it does

- Detects high-probability long/short setups using order-flow footprint, volume profile, market structure, fair value gaps (FVG), and order blocks.
- Enforces a strict risk-management and compliance guard (account size, daily loss limit, trailing drawdown, max contracts, consistency rule, news blackout, max hold time, weekend flat).
- Visualizes signals directly on the Quantower chart.

## Projects

| Project | Type | Purpose |
| :--- | :--- | :--- |
| **Quantower OrderFlow Signal Indicator** | Indicator | Main Quantower indicator entry point. |
| **LucidGoldCore** | Class library | Engines (`CumulativeDelta`, `DOMAbsorption`, `Footprint`, `FVG`, `HTFBias`, `KillZone`, `MarketStructure`, `OrderBlock`, `SignalScoring`, `TapeReading`, `VolumeProfile`), models, enums, and riskmanagement. |
| **LucidGoldIndicator** | Class library | `LucidGoldOrderFlowIndicator` rendering layer. |
| **LucidGoldStrategy** | Class library | `LucidGoldScalperStrategy` strategy implementation. |
| **LucidGoldFlowScalper** | Class library | Alternative flow-based scalper modules. |
| **Reflector** | Console app | Utility helper for reflection-based exploration. |

## Solution structure

```text
Quantower OrderFlow Signal Indicator/
├── Quantower OrderFlow Signal Indicator.csproj   # main indicator
├── Quantower_OrderFlow_Signal_Indicator.cs
├── LucidGoldSystem/
│   ├── LucidGoldCore/        # analysis engines + risk
│   ├── LucidGoldIndicator/   # indicator rendering
│   └── LucidGoldStrategy/    # automated strategy
├── LucidGoldFlowScalper/     # alternative flow scalper
└── Reflector/                # reflection helper
```

## Key concepts

- **Kill-zone filtering** — only trade during defined high-liquidity sessions.
- **Market structure** — CHoCH / BOS detection for directional bias.
- **Order blocks & FVG** — institutional footprint-based entry zones.
- **Volume profile / delta** — confirmation via cumulative delta and absorption.
- **Compliance guard** — automated rule enforcement for prop-firm style risk.

## Requirements

- .NET 10
- Quantower terminal / SDK references (not included in this repo)
- Windows (uses `System.Drawing.Common` / WPF rendering paths)

## Build status

> The solution currently targets **.NET 10**. Some legacy `obj` artifacts from earlier target frameworks may remain locally; run a clean/rebuild in Visual Studio after restoring packages.

## Getting started

1. Open `Quantower OrderFlow Signal Indicator.slnx` in Visual Studio 2022+.
2. Restore NuGet packages.
3. Build the solution.
4. Deploy the compiled indicator DLL to your Quantower custom-indicators folder.

## Configuration

Risk and session rules can be adjusted through the configuration file used by `LucidConfig` (see `LucidGoldSystem/LucidGoldCore/RiskManagement/LucidConfig.cs`).

## License

This project is maintained by [@Pusparaj99op](https://github.com/Pusparaj99op).
