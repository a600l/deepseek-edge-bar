# CLAUDE.md

Guidance for working on the DeepSeek Edge Bar (WPF, .NET 10, `net10.0-windows`).

## Build & test

There is no `.sln`. Build the two `.csproj` files individually:

```
dotnet build "DeepSeekEdgeBar\DeepSeekEdgeBar.csproj"
dotnet test  "DeepSeekEdgeBar.Tests\DeepSeekEdgeBar.Tests.csproj"   # 42 tests
```

The test project must also target `net10.0-windows` with `UseWPF true` or it fails with
"incompatible targeted frameworks".

**Kill the running app before building** or the exe is file-locked (MSB3027):
`taskkill /IM DeepSeekEdgeBar.exe /F`.

## Architecture

- `MainWindow.xaml/.cs` — the bar. Collapsed = 6px strip in a 12px-wide topmost window (extra
  6px is a transparent grab target). Expanded = 260x460 panel. Shows balance, peak/off-peak
  status with countdown, usage rows, model list.
- `DeepSeekApiClient.cs` — `/user/balance` and `/v1/models` using the **API key**.
- `DeepSeekUsageClient.cs` — `/usage/amount` and `/usage/cost` on `https://platform.deepseek.com/api/v0`
  using the **platform session token**. Envelope: `{code, msg, data:{biz_code, biz_msg, biz_data}}`.
  `biz_data` is an **object** for `amount` (keys `total`, `days`) and a **1-element array** for
  `cost`. `days` entries are `{date, data:[{model, usage:[{type, amount}]}]}`. Tokens = sum of the
  three non-zero token types (`PROMPT_TOKEN` is always 0; `REQUEST` is a count). A missing day
  bucket means "no usage that day" — **no fallback to the most recent active day** (that bug
  relabelled stale data as TODAY).
- `PeakHoursService.cs` + `MarketCalendar.cs` — peak = Beijing GMT+8, Mon–Fri 09:00–12:00 and
  14:00–18:00; weekends, Chinese statutory holidays, makeup workdays are off-peak.
- `SettingsStore.cs` — registry `HKCU\Software\DeepSeekEdgeBar`. Keys: `ApiKey`, `Edge`,
  `FullBarAmount`, `Opacity` (stored as String, default 0.70), `PlatformSessionToken` (64 chars),
  `StartWithWindows`, `ToggleHotkey`. Registry edits need an app restart; the Settings window
  live-previews opacity via `SettingsWindow.OpacityPreviewRequested`.
- `Theme.xaml` — resource brushes (`BarStatusGreenBrush`, `BarTextPrimaryBrush`, etc.). Define
  colors there rather than inline hex in markup.

## Hard-won gotchas

- `BeginAnimation(HeightProperty, …)` silently no-ops on the main window — assign `Height`
  directly (see `ExpandPanel`/`CollapsePanel`).
- `Visibility.Hidden` still participates in layout; use `Collapsed` for the expanded panel.
- `AllowsTransparency=True` makes a layered window — `WindowFromPoint` sees the window *below*
  the bar. The global hotkey (Ctrl+Alt+D) handles show/hide instead.
- `WS_EX_TOOLWINDOW` ⇒ `Process.MainWindowHandle` is zero. Use `EnumWindows` +
  `GetWindowThreadProcessId`.
- `SetProcessDPIAware()` makes `GetWindowRect` return physical pixels. This machine's 125%
  scaling: divide by 1.25 (1920x1080 physical = 1536x864 logical).
- Multi-monitor docking uses `MonitorFromWindow` + `GetMonitorInfo` (rcWork physical px, divided
  by `VisualTreeHelper.GetDpi` to get DIPs). Dock/verify on a single monitor is unaffected.
- Opacity applies to `collapsedStrip` only; the expanded panel must stay fully opaque.
- Position is driven by `SizeChanged`; only Width animates.

## Platform token capture (do not get this wrong again)

The working 64-char platform token is **not in any browser storage** — it is held in page memory
and only visible on the wire. `localStorage.userToken` (92 chars) is rejected with code 40003.

Correct capture: sign in to platform.deepseek.com → F12 → Network → click an `api/v0/` request
(Usage page) → Headers → copy the value after `Bearer ` in the authorization header (exactly 64
chars). See `BROWSER_FINDINGS.md`.

## Security

Never print the value of `PlatformSessionToken` (registry) or API keys — lengths only. DeepSeek
masks API keys server-side in its responses.

## Git

Commits must end with `Co-Authored-By: Claude Code <noreply@anthropic.com>`. PR descriptions
must end with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.