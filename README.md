# DeepSeek Edge Bar

A slim, always-on-top edge bar for Windows that shows your DeepSeek balance, peak/off-peak
pricing, and API usage at a glance — in DeepSeek's own brand colours.

- Collapses to a 6px vertical strip docked to the left or right screen edge.
- Click the strip (or press Ctrl+Alt+D) to expand a 260×460 panel showing:
  - Balance plus granted vs topped-up split, with the strip filled proportionally
  - Today's and this month's token usage, requests, and cost
  - Most-used model
  - Peak vs off-peak status with a live countdown to the next change
- Real peak pricing: Beijing (GMT+8) Mon–Fri 09:00–12:00 and 14:00–18:00; weekends, Chinese
  statutory holidays, and makeup workdays are off-peak.
- Close (✕) hides to the tray; only the tray menu's **Exit** quits the app.
- Refreshes automatically every minute.

## Requirements

- Windows 10/11
- .NET 10 SDK (or publish a self-contained build — see below)

## Building

There is no `.sln`. Each project is built on its own.

```
taskkill /IM DeepSeekEdgeBar.exe /F 2>$null   # required or the exe is file-locked
dotnet build "DeepSeekEdgeBar\DeepSeekEdgeBar.csproj"
dotnet test  "DeepSeekEdgeBar.Tests\DeepSeekEdgeBar.Tests.csproj"   # 42 tests
dotnet run --project DeepSeekEdgeBar
```

Self-contained single exe (no .NET runtime required on the target machine):

```
dotnet publish "DeepSeekEdgeBar\DeepSeekEdgeBar.csproj" -c Release \
  -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\DeepSeekEdgeBar
```

The test project must target `net10.0-windows` with `UseWPF true` to match the app project,
otherwise it fails with "incompatible targeted frameworks".

## Setup — two tokens

Open settings via the ⚙ button in the expanded bar or the tray menu.

1. **API key** — required for the balance and model list. Sign up at
   [platform.deepseek.com](https://platform.deepseek.com) → API Keys → create one.
2. **Platform session token** — required for the usage rows (requests, tokens, cost). It is
   **not** stored in `localStorage`, so do not look there:

   - Sign in to platform.deepseek.com and open the **Usage** page.
   - Press F12 → **Network**, then click any `api/v0/` request (e.g. a `usage/amount` call).
   - Open **Headers** → **Request Headers** and copy the 64-character value after `Bearer `.
   - Paste it into the settings field and click **Use platform token**.

   > ⚠️ `localStorage.userToken` (92 characters) is **rejected** with code 40003. Use the
   > `authorization` header value above instead.
   >
   > The token is never sent anywhere except to platform.deepseek.com, and it is stored only in
   > the Windows registry (`HKCU\Software\DeepSeekEdgeBar\PlatformSessionToken`).

## Usage

- **Expand/collapse** — click the strip, press **Enter** while it is focused, or use the global
  hotkey **Ctrl+Alt+D** (enable it in settings).
- **Collapse** — click the strip again, press **Esc**, or click anywhere outside the bar.
- The top **Status row** shows peak/off-peak and a countdown to the next change. The strip fill
  always matches the state colour: green = off-peak, red = peak, grey = no API key, red + no
  number = offline/problem.
- The **strip's two-tone fill** = current balance divided by the **Balance bar target**
  (default $10). Equal to the target fills the whole bar. Set your own target in settings.
- Click **⟳** or press **Ctrl+R** to refresh immediately.
- **Hide to tray** — ✕ on the panel, or the tray menu's *Show / Hide*.

## Settings

- **API key** — your DeepSeek platform API key.
- **Platform token** — the 64-char session token described above.
- **Balance bar target** — the amount at which the strip is 100% filled (default 10).
- **Opacity** — applies to the docked strip only (20–100%); the expanded panel stays fully
  opaque. The preview updates live while you drag the slider.
- **Start with Windows** — launch at sign-in.
- **Global hotkey: Ctrl+Alt+D** — toggle expand/collapse from anywhere.

## Features & behaviour

- Docks to whichever monitor the window is currently on (multi-monitor aware).
- The bar stays above other windows; the panel is clickable throughout.
- Theme matches DeepSeek branding (`#3964FE` accents, deep-navy surfaces, DM Sans/Inter stack) —
  all colours live in `DeepSeekEdgeBar/Theme.xaml`.

## Disclaimer

This is an unofficial, personal tool. It is not affiliated with or endorsed by DeepSeek.
Usage-account endpoints are reverse-engineered from the public platform console and may change
without notice.