# HANDOVER — DeepSeek usage client: verified state

Written 2026-09-22. **This file has now been rewritten twice, and the correction matters.**

- The *original* version tasked the reader with migrating `DeepSeekUsageClient.cs` to
  `usage/by_api_key/*` because the legacy endpoints "report TODAY as all zeros". That premise is
  **false** — the legacy endpoints return real nonzero TODAY numbers.
- The *second* version (also this session) asserted in turn that `by_api_key` "returns
  INVALID_PARAM under every shape tried" and was therefore unusable. **That was also wrong** — it
  was my own bug (misaligned window timestamps, plus `[DateTime]::UnixEpoch` being null in
  Windows PowerShell 5.1, which sent garbage epochs). `by_api_key` works fine.

Both errors had the same shape: a confident claim built on a probe that was itself broken. The
contract below is now measured, not inferred, and the exact commands that produced it are listed so
you can re-run them yourself rather than trust this file. **Do that before acting on it.**

## TL;DR for the next agent

1. **There is no TODAY bug to fix.** The shipped client already returns real nonzero today
   numbers, from the legacy endpoints it already calls. Do not "fix" it.
2. `usage/by_api_key/*` **does work** and is fully specified below. It is an *alternative*, not a
   repair.
3. The two surfaces **agree exactly on the month** (to the cent and the token) and differ **only
   on TODAY**, purely because they put the day boundary in different places. That is the entire
   practical difference — see "The one real difference".
4. The usage parser has **zero test coverage** until this session added some — see "What changed
   2026-09-22". It now has 18 tests over recorded fixtures, and the stale-day regression is proven
   to be caught (verified by reintroducing the bug and watching the test fail).

## Claim vs. reality

| Claim | Verified reality |
|---|---|
| `DeepSeekApiClient.cs` "already uses `by_api_key`; copy its pattern" | No `by_api_key` code exists there. It calls `https://api.deepseek.com/user/balance` with the **API key** (`DeepSeekApiClient.cs:33`) — different host, credential, and endpoint family. |
| Legacy `usage/amount` + `usage/cost` "report TODAY as all zeros" | False. Measured repeatedly, live: `TODAY tok=18,377,955 req=231 cost=USD 0.315926`. |
| `by_api_key` is unusable (`INVALID_PARAM` for everything) — *my second version's claim* | False. Works; see the contract below. The failures were my misaligned epochs, documented under "Window rules". |
| `by_api_key` "returns the real nonzero today" and is therefore the fix | True that it returns real data, but it is not a fix, because nothing is broken. |

## Evidence (live, 2026-09-22 22:50 local, machine UTC offset **+5** = 18000s)

Nothing was changed by this session. Token read from
`HKCU\Software\DeepSeekEdgeBar\PlatformSessionToken` (length 64, value never printed). The shipped
parser was run **unmodified** by linking the real `DeepSeekUsageClient.cs` into a scratch console
project — see "How to reproduce":

```
IsAvailable = True   Error = <none>
TODAY       tokens=3,295,058  requests=39  cost=$0.121473     <- nonzero, from the legacy endpoints
MONTH       tokens=113,477,759  requests=503  cost=$0.889944
LastActive  = 2026-09-22
```

Legacy per-day buckets (`usage/amount`, summing the three non-zero token types):

```
2026-09-18  tokens=0            requests=0
2026-09-19  tokens=0            requests=0
2026-09-20  tokens=0            requests=0
2026-09-21  tokens=109,558,091  requests=455
2026-09-22  tokens=2,415,917    requests=29      <- today, nonzero
```

`MainWindow.xaml.cs:340` reads the same `SettingsStore.LoadPlatformSessionToken()` value, so this is
the exact path the panel renders.

### Why anyone would think TODAY was broken

Usage here is wildly bursty: the 18th–20th are genuinely zero, the 21st is 109M tokens. On an idle
day the panel deliberately prints `TODAY  no usage yet` (`MainWindow.xaml.cs:347-352`: *"Usage is
bursty, so on most days 'today' is legitimately zero"*). That string is almost certainly what was
mistaken for a bug. It is the behaviour added by commit `ca6a4e5` to fix the **opposite** bug
(relabelling a stale day as today).

## The modern `by_api_key` contract (verified)

```
GET /api/v0/usage/by_api_key/amount?start={unix}&end={unix}&tz={seconds east of UTC}
GET /api/v0/usage/by_api_key/cost?start={unix}&end={unix}&tz={seconds east of UTC}
Authorization: Bearer <platform token>
```

**The two endpoints have DIFFERENT response shapes.** Both wrap in
`{code, msg, data:{biz_code, biz_msg, biz_data}}`, but inside `biz_data`:

- **amount** → `biz_data.series[]`, each `{api_key:{tracking_id, name, sensitive_id, valid,
  key_type}, model, buckets[]}`, each bucket `{time:<unix sec>, usage:{<TYPE>:<number>, ...}}`.
  Types observed: `RESPONSE_TOKEN`, `REQUEST`, `PROMPT_CACHE_HIT_TOKEN`, `PROMPT_CACHE_MISS_TOKEN`
  (this endpoint does **not** emit the legacy `PROMPT_TOKEN` key). `REQUEST` is a count, the rest
  are token counts. `biz_data` also carries `start`, `end`, `bucket`, `models[]`.
- **cost** → `biz_data.data[0].series[]` — **not** `biz_data.series[]`. Each bucket is
  `{time:<unix sec>, cost:<string number>}`, and the currency is at `biz_data.data[0].currency`
  (observed `"USD"`). Note this mirrors the legacy quirk in CLAUDE.md: amount is an object, cost is
  wrapped in a 1-element array.

**I got this wrong at first** and reported cost as `$0.000000`; the cause was reading
`biz_data.series` (which does not exist on the cost endpoint), so the sum was over an empty set
and silently returned zero. On the **modern** cost endpoint a shape miss therefore degrades to a
plausible-looking zero rather than an error.

That hazard does **not** apply to the shipped legacy parser, which was checked: a month with no
data answers `"biz_data":null`, and `ParseBizData` throws `HttpRequestException` on it rather than
returning zeros (pinned by `AnEmptyMonthReadsAsUnavailableRatherThanZero`). So the current code
fails loudly on an unrecognised envelope; a future `by_api_key` parser would not, and should be
written to throw rather than sum an empty series.

### Window rules (and how `INVALID_PARAM` is actually caused)

Measured, all with `tz=18000`:

| Window | Result |
|---|---|
| 1 hour, hour-aligned | OK, `bucket=3600` |
| 1 day, day-aligned | OK, `bucket=3600` |
| **1 day + 1 hour (90000s)**, both ends hour-aligned | **INVALID_PARAM** |
| 2, 3, 4, 5, 7, 30, 31 days, day-aligned | OK, `bucket=86400` |
| `<now>`..`<now+1h>` where now = 22:48 (not hour-aligned) | INVALID_PARAM |
| day-start..`<now>` (unaligned end) | INVALID_PARAM |

Observed rule: **the endpoints must be aligned to the bucket the server picks for the span**
(`3600` for spans ≤ 1 day, `86400` above that) *in the tz you pass*. The `90000` case is the
discriminator — it is hour-aligned but spans > 1 day, so it must be day-aligned and is not.
Practical form: **pass whole calendar days, or hour-aligned spans of ≤ 1 day.** All of my original
failures used `end = now + 1 day`, computed from a `DateTime` with minutes and seconds attached —
that alone produces `INVALID_PARAM` for every window length.

`tz` appears to be **ignored**: the same window with `tz=28800`, `tz=18000`, and no `tz` returned
byte-identical numbers. The window you pass is used literally. Treat `tz` as decoration and compute
the window yourself.

The route itself is real — bogus siblings (`/usage/by_api_key/nonsense`, `/usage/by_api_key`) return
`{"detail":"Not Found"}` with HTTP 404, while a missing/invalid `start`/`end` gives FastAPI-style
`422 {"detail":[{"loc":"query.end"}]}`, and date strings are rejected with the same 422 (the params
must be **integers**).

## The one real difference: where the day boundary falls

Same instant, same token, back to back:

| | `by_api_key` (local-calendar window) | legacy (platform calendar) | delta |
|---|---|---|---|
| TODAY tokens | 26,577,228 | 18,377,955 | +8,199,273 |
| TODAY requests | 309 | 231 | +78 |
| TODAY cost | USD 0.432967 | USD 0.315926 | +0.117041 |
| MONTH tokens | 128,560,656 | 128,560,656 | **0** |
| MONTH requests | 695 | 695 | **0** |
| MONTH cost | USD 1.084397 | USD 1.084397 | **0.000000** |

The month agrees **exactly** on both surfaces. So the two are reading the same data and differ only
in windowing: legacy uses the platform's own calendar day for "2026-09-22"; the `by_api_key` row
above uses *this machine's* local day (UTC+5 midnight → midnight). Beijing time is GMT+8, so the
platform's day and the local day overlap but are offset by 3 hours — the delta above is
(usage 21:00–24:00 today) − (usage 21:00–24:00 yesterday), and it can be either sign.

**Which is "correct" for the panel?** The XAML tooltip already promises "TODAY: usage so far today
(**DeepSeek's calendar**)" — which is exactly what legacy delivers. A migration to a local-calendar
window would silently contradict the app's own tooltip and change a number users watch, for no
correctness gain. If you migrate, migrate the window convention deliberately and update the
tooltip.

## How to reproduce

```powershell
# The repo path has a space — quote it.
Set-Location 'C:\Users\etc\Desktop\Deepseek Cheaper'
taskkill /IM DeepSeekEdgeBar.exe /F 2>$null   # release build locks the exe otherwise
dotnet build DeepSeekEdgeBar/DeepSeekEdgeBar.csproj -c Release
dotnet test  DeepSeekEdgeBar.Tests/DeepSeekEdgeBar.Tests.csproj
```

Baseline measured 2026-09-22 with no source changes: Release build succeeds 0 warnings / 0 errors;
`dotnet test` passes **60/60**. If something fails for you, it is your change.

To exercise the **real** parser (not a reimplementation), a scratch `Exe` targeting
`net10.0-windows` linked the shipped file:

```xml
<Compile Include="C:\Users\etc\Desktop\Deepseek Cheaper\DeepSeekEdgeBar\DeepSeekUsageClient.cs" Link="DeepSeekUsageClient.cs" />
```

with a `Program.cs` calling `new DeepSeekUsageClient().GetUsageAsync(token)` and printing the
snapshot. Pass the token by environment variable so it never reaches a command line:

```powershell
$env:DSU_TOKEN = (Get-ItemProperty 'HKCU:\Software\DeepSeekEdgeBar').PlatformSessionToken
& 'C:\tmp\usageprobe\bin\Debug\net10.0-windows\usageprobe.exe'
$env:DSU_TOKEN = $null
```

Raw probes are plain `curl.exe -s -H "Authorization: Bearer $t" <url>`.

### Two traps that produced this file's own errors

1. **`[DateTime]::UnixEpoch` is NULL in Windows PowerShell 5.1** (it is .NET Framework; the property
   was added in .NET Core 2.1). `$d.Ticks - [DateTime]::UnixEpoch.Ticks` silently evaluates to
   `$d.Ticks - 0` and yields ~`63925614000` instead of ~`1790017200` — a year-3995 window that the
   API happily accepts and echoes back. Use a literal `621355968000000000`.
2. **PowerShell parsing `T 'label',  $a $b 'tz'`** — the stray comma makes `'label', $a` a single
   array argument and shifts every parameter, so a window can silently become `end..28800`. Both
   long-window "failures" in my second probe came from exactly this. When a probe disagrees with a
   sibling probe, suspect the probe.

**Never print the token or API key — lengths only** (CLAUDE.md). Registry holds
`PlatformSessionToken` (64) and `ApiKey` (35).

## Do not break this

- `TODAY  no usage yet` is correct on idle days. Do not add a "fall back to the most recent active
  day" — that was the original bug, fixed in `ca6a4e5`. `LastActiveDate` exists precisely so the
  panel can say how stale the last real numbers are instead of mislabelling them.
  `AStaleDayIsNeverRelabelledAsToday` in `DeepSeekUsageClientTests.cs` now guards this.
- `dotnet test` passes **60/60** (42 pre-existing peak-hours tests, 8 usage-parser tests, 10 strip
  fill-ratio tests). See "What changed 2026-09-22".
- `DeepSeekUsageAuthException` is caught separately in `MainWindow` (`usage: token expired`); other
  failures degrade to `usage: unavailable`. Usage failures must never blank the panel.

## State of the working tree (uncommitted, unrelated to usage)

`git status` shows `M` on `MainWindow.xaml.cs`, `SettingsStore.cs`, `BROWSER_FINDINGS.md`,
`HANDOVER.md`. The first two hold a **dock-position memory** feature and a **peak-coloured strip**,
neither written nor reviewed by this session:

- `SettingsStore.LoadDockPosition/SaveDockPosition` — persists the docked monitor as `"left|top"`
  DIPs in the `DockPosition` registry value; `MainWindow.PositionWindow()` restores it before
  `DockToEdge()`, `MainWindow_Closed` saves it. The registry already contains
  `DockPosition = 1524.00|298.00`, so it has been run.
- `MainWindow.PeakGradient()` + `_lastStripBalance`/`_lastStripFullAmount` — repaints the collapsed
  strip red during peak and back to the balance tone when peak ends.
- ~~**Smell:** `PeakGradient()` computes `_lastStripBalance / _lastStripFullAmount` with no zero
  guard.~~ **Fixed 2026-09-22** — both `PeakGradient` and `BarGradient` had the identical
  unguarded division; the formula now lives once in `MainWindow.FillRatio`, which returns `0` for a
  zero divider. See "What changed 2026-09-22".

Decide deliberately whether to commit, review, or drop that work.

## What changed 2026-09-22

No behaviour change to the usage client — the endpoints it calls, the numbers it shows and the
window it uses are all exactly as before. What changed is that the untested parts now have tests,
and one latent crash is gone.

| File | Change |
|---|---|
| `DeepSeekEdgeBar/DeepSeekUsageClient.cs` | `BuildSnapshot` `private` → `internal` (+ doc comment) so tests can drive it from recorded JSON. **No logic touched** — `git diff` on this file is only that keyword and comment. |
| `DeepSeekEdgeBar/DeepSeekEdgeBar.csproj` | `<InternalsVisibleTo Include="DeepSeekEdgeBar.Tests" />`. |
| `DeepSeekEdgeBar.Tests/DeepSeekUsageClientTests.cs` | **New.** 8 tests over fixtures matching the live legacy envelopes: today-from-today's-bucket-only, month totals, the stale-day regression, an idle-but-zero-filled today, per-model aggregation/`TopModel`, `biz_data:null` → error, `code 40003` → `DeepSeekUsageAuthException`, other codes → `HttpRequestException`. Dates are computed from `DateTime.Today` so the fixtures do not rot. |
| `DeepSeekEdgeBar.Tests/FillRatioTests.cs` | **New.** 10 tests pinning `FillRatio`, including `FillRatio(0, 0)` not being `NaN`. |
| `DeepSeekEdgeBar/MainWindow.xaml.cs` | Extracted `internal static double FillRatio(decimal, decimal)` and used it in both `BarGradient` and `PeakGradient`, replacing two copies of an unguarded `balance / fullAmount`. |

**The regression test was verified to have teeth, not just to pass.** The first version of
`AStaleDayIsNeverRelabelledAsToday` gave today its own all-zero bucket, so the exact-date lookup
succeeded and the fallback path the test exists to police was never reached — it passed even with
the `ca6a4e5` bug reintroduced. Removing today's bucket from the fixture made it fail correctly
(`Expected: 0, Actual: 1250`), and the mutation was then reverted. Worth repeating before trusting
any test in this repo: **break the code, watch the test fail, put it back.**

### Not verified

The app builds (Release, 0 warnings / 0 errors), launches, and stays responsive, and
`dotnet test` is 60/60 — but the **strip's rendering was not confirmed visually**. This app hides to
tray by default (commit `bf1c5fd`), so nothing is on screen until Ctrl+Alt+D, and the capture
attempt landed on the terminal instead. Separately, `PeakGradient` only runs during Beijing peak
hours (09:00–12:00, 14:00–18:00) and the session was off-peak, so that path could not have painted
even with the bar visible. The fix rests on the unit tests, not on a screenshot.

## Aside: the balance is reachable with the platform token too

Discovered 2026-09-22 by capturing the platform site's own traffic (Playwright), *not* by guessing.

```
GET https://platform.deepseek.com/api/v0/users/get_user_summary
Authorization: Bearer <platform token>          -> 200
{ "code":0, "data": { "biz_code":0, "biz_data": {
    "normal_wallets":[{"currency":"USD","balance":"3.7989404254000000","token_estimation":"0"}],
    "bonus_wallets" :[{"currency":"USD","balance":"0","token_estimation":"0"}],
    "total_costs"   :[{"currency":"USD","amount":"35.2010595746000000"}] }}}
```

`normal_wallets` = topped-up, `bonus_wallets` = granted, and their sum is the number
`DeepSeekApiClient` gets from `api.deepseek.com/user/balance` (that returned `"3.81"` minutes
earlier — consistent once you allow for usage accruing between the two calls; do not claim the two
are bit-identical without measuring them together). `total_costs` is lifetime spend, which the API
key route does not expose at all. Verified headlessly with `curl.exe` and only the `Authorization`
header — no cookies, no browser — so it is usable from the app as-is.

**The guesses all 404'd first**: `api/v0/user/balance`, `balance`, `user/balance_info`, `user/info`,
`user/profile`, `account/balance`, `user/quota`, `usage/balance`, `top_up/amount`, `user`. The real
naming is **plural `users` + a `get_` verb** (`users/get_user_summary`, and `users/get_api_keys`
which the site also calls). Lesson: on this host, guessing route names is unreliable; capture the
site's traffic and read the paths off it.

The same capture shows the usage page calling **exactly the `by_api_key/amount` + `/cost` pair
documented above**, with `tz=18000` — i.e. the browser's own offset. So `tz` is client-supplied and
the site passes the local offset; that is consistent with (and does not contradict) the finding
below that changing it does not change the numbers.

**So the API key is not the only source of the headline balance.** What it still uniquely provides
is `/v1/models`, the model list — which `MainWindow` treats as decoration that must not blank the
panel when it fails (`MainWindow.xaml.cs:331-332`). Why the app nevertheless gates on it: the API
key is the *documented, stable* credential, while the platform token is an unofficial 64-char wire
capture that expires (`code 40003`) — and the code is arranged so that fragile credential can only
degrade the *usage rows*, never the headline balance. That is a real reason to keep both, but it is
a design choice, not a technical necessity. See item 5 below.

## Recommended next steps, in order

1. ~~**Change nothing about the usage client.**~~ **Still true, and now confirmed done** — the
   client is unchanged. Trust the next "TODAY is broken" report only after re-running the harness
   above; three separate confident claims about this endpoint have already turned out to be
   artefacts of broken probes.
2. ~~**Add usage-parser tests from recorded JSON.**~~ **Done 2026-09-22** — 8 tests in
   `DeepSeekUsageClientTests.cs`. The one shape the suite does not cover is the **modern**
   `by_api_key` envelope; if you migrate (item 4), that is the net you need first, because a shape
   miss there degrades to a plausible zero instead of throwing.
3. ~~**Deal with the uncommitted dock/peak work**, including the `PeakGradient` zero guard.~~
   **Half done**: the guard is in (`FillRatio`, 10 tests). The dock/peak work itself is still
   uncommitted and still unreviewed by anyone — deciding its fate is the real remaining item.
4. **Only if you have a concrete reason** (e.g. you want per-API-key breakdown, which
   `series[].api_key` provides, or live-to-the-hour granularity): migrate — with the contract above,
   the day-boundary decision made explicitly, the tooltip updated, and tests landed first.
   Note what this is *not*: it is not a bug fix, and per "The one real difference" it will change
   the TODAY number users see.

Nothing has been committed. The working tree still holds the pre-existing dock/peak changes
alongside the test work described above.

5. **Decide whether one credential should be enough.** Right now `MainWindow.xaml.cs:319-324`
   returns early with `ShowNoApiKeyState()` when the API key is absent, so a user who has a working
   platform token but no API key sees a blank panel — even though `users/get_user_summary` would
   fill the headline number. The shape of a fix, if you want one: prefer the platform token for
   balance + usage, keep the API key purely for `/v1/models`, and let either credential alone
   produce a usable bar. Note the panel currently derives `accountProblem` from
   `!IsAvailable || TotalBalance <= 0` (`:501`), and the platform summary has no `is_available`
   field, so that mapping needs a decision rather than a copy-paste — a genuine zero balance is a
   real state, not an outage.
