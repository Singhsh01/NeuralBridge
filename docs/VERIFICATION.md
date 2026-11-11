# Verification record

What was actually executed for the redesign release (new interface, QR join, download, sync
status, shortcuts, ending-soon warning, wallpaper and location removal), where, and what was not.

## 1. Cloud sandbox, real packages (this release)

The sandbox has the .NET SDK 10.0.115 and no access to nuget.org, so it restores from an
offline feed copied from a real NuGet cache (EF Core, Identity, real xUnit and the test SDK).
Two dev-only package references (`Microsoft.EntityFrameworkCore.Design`,
`coverlet.collector`) were not in that cache and were removed **in the verification copy
only**; neither affects the build output or the tests. The SDK's Blazor assets pack was pinned
to 10.0.12 for the same reason.

| Step | Command | Result |
|---|---|---|
| Build | `dotnet build NeuralBridge.sln` (warnings as errors) | 0 warnings, 0 errors |
| Format | `dotnet format NeuralBridge.sln --verify-no-changes` | exit 0 |
| Unit tests | `dotnet test NeuralBridge.sln` (real xUnit, EF Core on SQLite included) | **182 passed, 0 failed** (188 before; the 6 location tests were removed with the feature) |
| End-to-end | `npx playwright test` (the config starts the mock OAuth provider and the app, in-memory, Development) | **46 passed, 1 skipped** (the skipped test needs a server without Google; next row) |
| Google not configured | app started without `Authentication:Google`, then `NB_EXPECT_GOOGLE=false npx playwright test tests/auth.spec.ts` | **4 passed**, 8 skipped (the Google tests) |
| Screenshots | `node scripts/screenshots.mjs` against a running instance | 11 images written to `docs/screenshots/`, reviewed by eye |

The full suite was run four times during this release; the last three runs were clean. The
first run had three failures, all in tests: a locator that matched both "5 min" and "15 min",
an SVG size read from an attribute the QR code doesn't set, and a heading whose copy changed
("You were removed from this session"). The tests were corrected, not the assertions relaxed.

Repeating the new specs found one real bug: after *Create a session* or *Join a session* in
the hero, the focus helper kept pulling focus back to the first field for up to three seconds,
so a keyboard user moving to the tabs straight away lost their place (7 of 20 runs). It now
stops as soon as focus moves anywhere on purpose. After the fix: the tab test 20/20, and
`landing.spec.ts` + `workspace.spec.ts` repeated 3 times, 48/48.

New end-to-end coverage in this release (`workspace.spec.ts`, `landing.spec.ts`):

- landing: no wallpaper, location prompt, clock or decorative layers, and none of their files requested; the create button visible without scrolling at 1440×900; hero buttons switch tabs and focus the first field; arrow keys between tabs; system light and dark backgrounds match the tokens
- QR: encodes exactly `<origin>/join/<ID>`, contains neither the PIN nor the workspace token, renders as SVG, and the link prefills the join form on a phone
- Download: file name `neuralbridge-<ID>-YYYYMMDD-HHmm.txt`, exact UTF-8 contents, no network request
- status pill: `pending` while an edit is unsent, `synced` after the server acknowledged it
- shortcuts: Ctrl/⌘+Enter copies all; `?`, `d` typed in the editor stay text; `?` opens the list outside the editor, Escape closes it, `E` focuses the editor
- ending soon: a 5-minute session (an extra length configured only in `playwright.config.ts`) shows the banner to owner and guest; only the owner gets the extend button; extending hides it
- phone: editor in view first, side panels collapsed and expandable, no horizontal scroll
- both themes: the workspace renders without console errors or CSP violations

Contrast was checked by calculation for the main token pairs (WCAG relative luminance):
body text, muted text, accent buttons, links, success, warning and danger all reach 4.5:1 in
both themes; the dark accent was darkened from `#5b6cf0` (4.33:1 with white) to `#5262ea`
(4.89:1) and placeholders moved from the faint to the muted colour as a result.

## 2. macOS (previous release)

The previous release was verified on the Mac with `scripts/verify-mac.sh`
(`Run-Verification.command`): restore, build, format, 188 unit tests, a SQLite smoke test,
40 E2E tests plus the no-Google run on SQLite. **This release has not been run on the Mac
yet** (the computer was not reachable from the session at the end of the work). Run
`Run-Verification.command` once to confirm; the expected results are the counts in section 1.

## 3. Not verified

- **Real Google accounts.** No real OAuth client was configured. Sign-in was exercised end to end against the mock provider (same ASP.NET Core OAuth handler, PKCE, state, token exchange, userinfo).
- **Real devices and browsers.** Phone layouts were tested with Chromium's mobile emulation at 320, 375 and 390 px. Safari, Firefox, real phones and scanning the QR code with a real camera were not tested.
- **Screen readers.** Roles, names and focus order are asserted in tests; no screen reader session (VoiceOver, NVDA) was run.
- **Calls across real networks**, **real SMTP**, **PostgreSQL** and **`docker compose`** were not run.
- **Offline state.** The *Reconnecting*/*Offline* labels follow Blazor's reconnect events; dropping the connection was not simulated in a test.
