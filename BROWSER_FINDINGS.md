# Browser findings

Investigated 2026-09-22 against a signed-in session at `https://platform.deepseek.com/usage`.
Values were never read out; only key names, lengths, and API result codes are recorded below.

## Storage keys and lengths

Enumerated `localStorage`, `sessionStorage`, `document.cookie`, and `indexedDB`
(via `indexedDB.databases()` → every object store → `getAll()`). **No HttpOnly cookies are
readable from JS, but see the "Accepted token" note — they are ruled out by other means.**

| Storage | Key | Length |
|---|---|---|
| localStorage | `__appKit_@deepseek/platform_bannerSettings` | 688 |
| localStorage | `awswaf_session_storage` | 658 |
| localStorage | `__appKit_@deepseek/platform_banner` | 649 |
| cookie | `aws-waf-token` | 346 |
| localStorage | `deepseek.platform.usage.boardPreference.v1.3cbd7893-9730-4bc9-9bec-fb9628373648` | 167 |
| localStorage | `__tea_cache_tokens_20006841` | 133 |
| localStorage | `__ds_remote_feature_store` | 128 |
| localStorage | `__paypal_storage__` | 124 |
| localStorage | **`userToken`** | **92** |
| localStorage | `.thumbcache_6b2e5483f9d858d7c661c5e276b6a6ae` | 88 |
| sessionStorage | `__tea_session_id_20006841` | 78 |
| localStorage | `__appKit_userInfo` | 71 |
| localStorage | `smidV2` | 63 |
| localStorage | `aws_waf_token_challenge_attempts` | 51 |
| localStorage | `_INTL_TRACKER_COOKIE__residual` | 49 |
| localStorage | `__appKit_@deepseek/platform_lastSessionValue` | 48 |
| localStorage | `INTL_TRACKER-deivceTag` | 45 |
| localStorage | `_INTL_TRACKER_COOKIE__fullfill_ref` | 38 |
| localStorage | `deepseek-device-id:platform` | 36 |
| localStorage | `__ds_remote_feature_did` | 36 |
| localStorage | `AMSSDK_STORAGE_ID` | 36 |
| localStorage | `__appKit_@deepseek/platform_themePreference` | 34 |
| localStorage | `__appKit_@deepseek/platform_localePreference` | 34 |
| localStorage | `__appKit_@deepseek/platform_debugPanelEnabled` | 31 |
| localStorage | `priceNoticeClosed` | 31 |
| localStorage | `userCurrencyInLastSession` | 31 |
| localStorage | `__appKit_@deepseek/platform_debug` | 31 |
| localStorage | `__appKit_@deepseek/platform_fg_payment` | 30 |
| localStorage | `homeNotice2Closed` | 30 |
| localStorage | `showRefund` | 30 |
| localStorage | `homeNotice1Closed` | 30 |
| localStorage | `userUsageCurrencyCountInLastSession` | 27 |
| localStorage | `aws_waf_referrer` | 15 |
| localStorage | `awswaf_token_refresh_timestamp` | 13 |
| localStorage | `awswaf_captcha_solve_timestamp` | 13 |
| localStorage | `AntomDefaultGrayScaleId` | 2 |
| localStorage | `__tea_cache_first_20006841` | 1 |
| localStorage | `Antom_1.47.2ELEMENT_PAYMENT_LastAppVersion` | 0 |

indexedDB: no databases present for this origin.

**No key in any storage area has a value of length 64.** The closest is `smidV2` at 63.

## Accepted token

key name: **none — it is not persisted in any browser storage**
storage: **none — held in JavaScript memory only**
length: **64** (confirmed by capturing the live `Authorization` header)

test result: **no candidate to test** — because no storage value is 64 characters, step 4's
candidate loop found zero candidates. The 64-char value observed on the wire *is* accepted
(the app's own API calls return `code: 0`); it just cannot be read out of storage.

### How the 64-char value was confirmed, and why it is memory-only

1. Hooked `XMLHttpRequest.prototype.setRequestHeader` **and** `window.fetch`, then triggered a
   genuine refetch by re-selecting the date preset. The frontend's own request carried
   `Authorization: Bearer <64 chars>`. Length recorded; value never read out.
2. Compared that captured value against **every** value in `localStorage` and `sessionStorage`:
   result **`NO STORAGE MATCH`**.
3. It cannot be a non-HttpOnly cookie (none is 64 chars). It also cannot be an HttpOnly cookie:
   an HttpOnly cookie is invisible to JS and is sent automatically by the browser as `Cookie`,
   it cannot be read by JS and re-emitted as a custom `Authorization` header. Since the frontend
   *does* set that header from script, the value must live in script memory.
4. Notably, `localStorage.userToken` is **92** chars — a different value entirely, and it is
   rejected with `code: 40003`. This is what the current tooltip tells users to copy.

## Verdict

The working token is **not stored anywhere readable in the browser**. It is fetched into page
memory at load (most likely minted from the session established by the login cookie) and can
only be observed on the wire. The tooltip must stop pointing at `localStorage.userToken`.

Tooltip should say:
> Open platform.deepseek.com and press F12 → Network. Click any `api/v0/` request (e.g. a
> `usage/amount` call on the Usage page) → Headers → Request Headers → copy the value after
> `Bearer ` in the `authorization` header. It is 64 characters. Do **not** use `userToken` from
> Local Storage — that is a different 92-character value and is rejected.

### Not established (do not claim otherwise)

- **Where** the token is minted — no exchange endpoint was identified. If one exists, the app
  could mint the working token from a stored credential itself and the user would never copy a
  token by hand. Finding it needs a network trace of a **fresh page load** (the token is
  obtained during load, before any user interaction), inspecting the response body of the
  bootstrap/auth call. This was not completed.
