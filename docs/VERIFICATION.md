# Verification record

What was actually executed for this version (accounts, Google sign-in, calls and the new
interface), where, and what was not.

## 1. macOS with the real packages (primary)

`scripts/verify-mac.sh` (double-click `Run-Verification.command` in Finder), on a MacBook Air
(arm64), macOS 26.6.2, .NET SDK 10.0.401, packages restored from nuget.org. Logs are written to
`verify-logs/`.

| Step | What it runs | Result |
|---|---|---|
| 01-restore | `dotnet restore NeuralBridge.sln` | exit 0 |
| 02-build | `dotnet build` (warnings as errors, latest-recommended analyzers incl. xUnit analyzers) | 0 warnings, 0 errors |
| 03-format | `dotnet format --verify-no-changes` | exit 0 |
| 04-unit-tests | `dotnet test` (real xUnit) | **188 passed, 0 failed**, including the 5 EF Core tests on SQLite |
| 05-sqlite-smoke | the app on SQLite over HTTP: health, home, `/api/auth/providers`, create with PIN, join without/wrong/right PIN | 200, 200, `{"google":false,...}`, created, 404 / 404 / 200; Identity and session tables present |
| 06-e2e-playwright | full Playwright suite; the config starts the mock OAuth provider and the app (in-memory) | **40 passed**, 1 skipped (the "Google not configured" test, which needs a server without Google; see step 07) |
| 07-e2e-sqlite-accounts-no-google | the app on **SQLite/EF Core Identity** with Google **not** configured; `auth.spec.ts` + `security-and-owner.spec.ts` | **10 passed**, 8 skipped (the Google tests) — disabled Google button with the explanation, register/log in/wrong password, forgot → reset, delete account, PIN flows |

The first run on the Mac failed at build: two new tests used `.GetAwaiter().GetResult()`, which
the real xUnit analyzer (xUnit1031) rejects. They were made `async`; one E2E locator matched a
hidden duplicate of the "not configured" note. Both were fixed and the whole script re-run
with the results above.

## 2. Cloud sandbox (offline copy)

The sandbox cannot reach nuget.org, so it builds an offline copy with EF Core and xUnit
replaced by stubs/a minimal xUnit shim (the Google handler is now the shared-framework OAuth
handler, so it is **not** stubbed). Results there:

- build 0 warnings / 0 errors, `dotnet format --verify-no-changes` exit 0
- unit tests via the shim: 183 passed (EF tests excluded)
- `dotnet publish -c Release`, then the full Playwright suite against the published build in the `Testing` environment with the mock provider: 40 passed, 1 skipped
- the same suite against `dotnet run` (Development): 40 passed, 1 skipped
- Google not configured (`NB_EXPECT_GOOGLE=false`): the disabled-button test passed
- Production with `ExpectedInProduction=true` and no credentials: startup warning logged, no values
- only one of ClientId/ClientSecret set: startup stops with `Authentication:Google needs both ClientId and ClientSecret, or neither.`; the value does not appear in the output
- logs from the E2E runs contain neither the test client ID/secret nor names, emails or PINs used in the tests

The WebRTC call test was also run 5 times in a row after the negotiation fix (5/5 passed).

## 3. Not verified

- **Real Google accounts.** No real OAuth client credentials were configured, so the redirect to `accounts.google.com` and back was **not** exercised against Google. It was exercised end to end against the mock provider (same ASP.NET Core OAuth handler, PKCE, state, token exchange, userinfo), and unit tests check that production uses Google's published endpoints.
- **Calls across real networks.** Calls were tested between browser contexts on one machine with Chromium's fake camera and microphone. NAT traversal between different networks, TURN, Safari/Firefox and real devices were not tested.
- **Real SMTP.** Password-reset email was tested through the Development outbox only.
- **PostgreSQL** and **`docker compose`** were not run.
- Real reverse geocoding (Nominatim) was not reachable from the test environments; the rounded-coordinates fallback was exercised.
