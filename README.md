# DeepSeek Edge Bar

A slim, always-on-top edge bar for Windows that shows your DeepSeek balance, peak/off-peak pricing, and API usage at a glance.

- Collapses to a 6px vertical strip docked to an edge of your screen.
- Hover/click to expand and see:
  - Balance + granted vs topped-up split
  - Today's and current month's token usage, requests, and cost
  - Most-used model
  - Peak vs off-peak status with countdown
- Expanded window height and balance indicator are scaled against a full balance of $10.
- Tray icon with start-with-Windows and opacity settings.

## Requirements

- Windows 10/11
- .NET 10 SDK (or publish a self-contained build)

## Getting started

```bash
dotnet build DeepSeekEdgeBar/DeepSeekEdgeBar.csproj
dotnet run --project DeepSeekEdgeBar
```

## Settings

Open the settings cog in the expanded bar (or tray menu):

- **API key** — required for balance and model list. Sign up at [platform.deepseek.com](https://platform.deepseek.com) and create an API key.
- **Platform session token** — used for the usage endpoints. Do not share it; it grants access to your account on the DeepSeek Open Platform.
- **Edge** — dock left or right.
- **Opacity** — 10–100%.
- **Start with Windows** — launch automatically at sign-in.

## Disclaimer

This is an unofficial, personal tool. It is not affiliated with or endorsed by DeepSeek. Usage-account endpoints are reverse-engineered from the public platform console and may change without notice.