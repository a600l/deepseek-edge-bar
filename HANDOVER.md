# DeepSeek Edge Bar — Handover

Date: 2026-09-22
Branch: `master` — **uncommitted changes** (nothing committed this session)

---

## TL;DR

The app builds clean, all 42 tests pass, and it runs correctly. Two bugs were fixed and
verified on screen this session. There is **one open question** (see *Known gaps*) and a
handful of untouched polish items.

```
dotnet build "DeepSeekEdgeBar\DeepSeekEdgeBar.csproj"     # 0 warnings, 0 errors
dotnet test  "DeepSeekEdgeBar.Tests\DeepSeekEdgeBar.Tests.csproj"   # 42 passed
```

There is **no .sln file** — build the two `.csproj` files individually.

---

## What was fixed this session

### 1. `TODAY` row was showing yesterday's numbers (real data bug)

`DeepSeekUsageClient.DaysForDate` matched today's date, and on no match **fell back to the
most recent day that had any usage**. Because usage here is bursty (5 active days in 30), that
fallback silently relabelled a stale day as "TODAY".

Proven against the live platform, not inferred:

| App showed | Platform actually says |
|---|---|
| `TODAY 101,358,818 tok · 377 req · $0.6071` | **2026-09-21** = 101,358,818 tok · 377 req · $0.6071 |
| `MONTH 101,983,428 tok · $0.6514` | September 2026 = 101,983,428 tok · $0.6514 ✅ (was already correct) |

Today (2026-09-22) genuinely had zero usage.

**Fix:** removed the fallback entirely (`DaysForDate` → `RowsForExactDate`). A missing bucket
now means "no usage that day". Added `MostRecentActiveDate` + a `LastActiveDate` field on
`UsageSnapshot`, and the panel renders:

- `TODAY  no usage yet` when the day is empty
- `last used Sep 21` on the hint line — so the information is preserved but **dated**

Also corrected the stale `ToolTip` on the usage `StackPanel` in `MainWindow.xaml` which had
literally documented the buggy behaviour (*"TODAY: last day of usage data"*).

> **Important:** `tz=18000` on `/usage/amount` is **silently ignored** — verified byte-identical
> responses with and without it. There is no server-side knob to align day buckets to the local
> time zone. Do not try to "fix" this by passing a tz parameter.

### 2. Opacity faded the expanded panel, not just the strip

The setting was applied to the `Window`, so it dimmed everything. Replaced with
`ApplyStripOpacity(double)` which sets `collapsedStrip.Opacity` only.

**Verified by measurement:** mean RGB over the strip column = **(150,155,153)** at opacity 1.0
vs **(25,28,29)** at opacity 0.25 (desktop ≈ (22,23,23)). The strip genuinely responds, and the
expanded panel renders fully opaque at the shipped 0.70 setting.

---

## Known gaps / open items

1. **Opacity 0.25 panel appearance — unverified.** During the A/B test the expanded panel
   sampled *brighter* at 0.25 than at 1.0, which should be impossible since the panel is not
   supposed to be affected at all. Almost certainly a bad capture (the sample region likely
   included desktop wallpaper before the expand animation settled), but it was not chased down.
   **Re-check by eye at 0.25 before trusting it.**

2. **`userToken` in the Settings tooltip is wrong.** `SettingsWindow.xaml` tells users to copy
   `localStorage.userToken` from DevTools. That value (92 chars) is **rejected** by the API with
   `code 40003`. The token that actually works is a **64-char** value. The tooltip needs
   rewriting (or an easier capture path).

3. **`CLAUDE.md` was offered but never created.** Not started.

4. **Multi-monitor docking** is still hardcoded to the primary monitor. Needs
   `MonitorFromWindow` + `GetMonitorInfo` + DPI conversion. Unverifiable on this single-monitor
   machine; a comment sits at `DockToEdge`.

---

## How the app works (orientation for a fresh model)

- **`MainWindow.xaml` / `.cs`** — the bar. Collapsed = a 6px strip in a 12px-wide window
  (extra 6px is a transparent grab area). Expanded = 260x460 panel. Balance, peak/off-peak
  status with a live countdown, usage rows, model list.
- **`DeepSeekApiClient.cs`** — `/user/balance` and `/v1/models`, using the **API key**.
- **`DeepSeekUsageClient.cs`** — `/usage/amount` and `/usage/cost` on
  `https://platform.deepseek.com/api/v0`, using the **platform session token**. Response
  envelope is `{code, msg, data:{biz_code, biz_msg, biz_data}}`; `biz_data` is an object for
  `amount` (keys `total`, `days`) and a **1-element array** for `cost` (element holds
  `total`, `days`, `currency`). `days` entries are `{date, data:[{model, usage:[{type, amount}]}]}`
  with `type` ∈ `PROMPT_TOKEN`, `PROMPT_CACHE_HIT_TOKEN`, `PROMPT_CACHE_MISS_TOKEN`,
  `RESPONSE_TOKEN`, `REQUEST`. Total tokens = sum of the three non-zero types (`PROMPT_TOKEN`
  is always 0 and `REQUEST` is a count, not tokens).
- **`PeakHoursService.cs` + `MarketCalendar.cs`** — peak = Beijing GMT+8, Mon–Fri 09:00–12:00
  and 14:00–18:00. Weekends, Chinese statutory holidays, and makeup workdays are off-peak.
- **`SettingsStore.cs`** — Windows Registry, `HKCU\Software\DeepSeekEdgeBar`. Current keys:
  `ApiKey`, `Edge`, `FullBarAmount`, `Opacity` (0.70), `PlatformSessionToken` (64 chars),
  `StartWithWindows` (true), `ToggleHotkey` (true).
- **Tests** — `DeepSeekEdgeBar.Tests`, xunit, `net10.0-windows` + `UseWPF true` (must match the
  app project or it fails with "incompatible targeted frameworks").

---

## Hard-won gotchas — save yourself the debugging

- **`BeginAnimation(HeightProperty, …)` silently no-ops on this Window.** Width animates, Height
  does not. Assign `Height` directly (see `ExpandPanel`).
- **`Visibility.Hidden` still participates in layout; `Collapsed` does not.** The expanded
  panel's natural height was keeping the strip at full height until it was switched to `Collapsed`.
- **`AllowsTransparency=True` makes it a layered window** — `WindowFromPoint` returned the window
  *below* the bar despite `Topmost=True`. Hence the global hotkey instead of click-through.
- **`WS_EX_TOOLWINDOW` makes `Process.MainWindowHandle` return zero.** Use `EnumWindows` +
  `GetWindowThreadProcessId` to find the bar window.
- **`SetProcessDPIAware()`** makes `GetWindowRect` return *physical* pixels. At this machine's
  125% scaling, divide by 1.25 for logical (1920x1080 physical = 1536x864 logical).
- **Killing the app before `dotnet build`** — otherwise `MSB3027` file lock:
  `taskkill /IM DeepSeekEdgeBar.exe /F`.
- **`Opacity` is stored as a Registry `String`, not a DWORD.**

---

## Verifying UI changes (no test harness for WPF)

Helper scripts already exist in `C:\tmp\` and work:

- `C:\tmp\toggle.ps1` — finds the bar window via `EnumWindows`, prints its rect in physical and
  logical px, fires **Ctrl+Alt+D**, and saves before/after screenshots to `C:\tmp\bar-*.png`.
- `C:\tmp\opacityab2.ps1` / `panelab.ps1` — set the registry `Opacity`, restart, sample mean RGB
  over the strip or panel. Good pattern for any "did the visual actually change" question.

Registry edits need an app restart to take effect (settings load at startup), though the Settings
window live-previews opacity via the `OpacityPreviewRequested` event.

---

## Security note

The platform session token lives in the registry at
`HKCU\Software\DeepSeekEdgeBar\PlatformSessionToken`. It was used read-only this session to query
the user's own usage data; **only its length was ever printed, never its value**. Keep it that way.
The DeepSeek server masks API keys itself in its API responses (`sk-7dd37***…***3ea3`).

---

## Git attribution

Commits must end with:

```
Co-Authored-By: Claude Code <noreply@anthropic.com>
```

PR descriptions must end with:

```
🤖 Generated with [Claude Code](https://claude.com/claude-code)
```
