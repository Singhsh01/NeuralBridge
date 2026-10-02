# Security & privacy design

This document describes what NeuralBridge protects, how, and where the limits are.

## Threat model

| Asset | Threat | Control | Where |
|---|---|---|---|
| Shared text | Read by a stranger who guesses a code | 60-bit CSPRNG codes; per-IP rate limits on joins (HTTP + circuit); optional PIN | `SessionCodeGenerator`, `AppRateLimiter`, `SessionService.JoinAsync` |
| Shared text | Persisted somewhere unexpected | Text lives only in `InMemoryDocumentStore`; never in DB, logs or events; purged on close or expiry, and orphans are swept | `IDocumentStore`, `SessionCleanupService` |
| PIN | Online guessing | 5 attempts → 5-minute lockout per session; rate limits | `SharedSession.RegisterFailedPinAttempt` |
| PIN | Offline cracking after DB leak | PBKDF2-HMAC-SHA512, 210k iterations, 128-bit salt | `Pbkdf2SecretHasher` |
| Session existence | Enumeration ("is this code protected?") | Identical response for unknown code, missing PIN and wrong PIN | `SessionError.NotFoundOrPinIncorrect` |
| Participant identity | Token theft from DB | Only SHA-256 hashes of 256-bit tokens are stored | `TokenService` |
| Participant identity | Token theft in browser | Stored per tab in `sessionStorage`, encrypted and MAC'd with ASP.NET Data Protection; strict CSP limits script injection | `SessionCredentialStore` |
| Owner powers | Guest calls owner operations | Every owner operation re-authorizes token + `IsOwner` inside the per-session lock | `SessionOwnerService.ExecuteAsync` |
| Session isolation | Hub client reads/writes another session | Connection binds to one session at `Attach`; later calls use server-side binding only; broadcasts go to `session:{id}` groups | `SessionHub`, `HubNotificationRelay` |
| Browser | XSS through shared text | Text only set as `textarea.value` or Razor-encoded; no `MarkupString`; CSP `script-src 'self'` (no inline, no eval); links limited to http/https | `editor.js`, `DetectedLinks.razor`, `SecurityHeadersMiddleware` |
| Browser | Clickjacking | `frame-ancestors 'none'`, `X-Frame-Options: DENY` | middleware |
| Browser | Code leak via Referer | `Referrer-Policy: no-referrer`; links use `rel="noopener noreferrer nofollow"` | middleware, `DetectedLinks.razor` |
| Accounts | Login CSRF / open redirect | Google login and logout are POST + antiforgery; return URLs must be local paths (`//`, `/\\`, CR/LF and absolute URLs fall back to `/`) | `AccountEndpoints.SafeReturnUrl` |
| Google OAuth | Code interception / CSRF on callback | Authorization code flow with PKCE (S256), state + correlation cookie validated by the ASP.NET Core OAuth handler; forged callbacks end at `/account/login?error=google_failed` | `GoogleOAuthSetup` |
| Google OAuth | Client secret exposure | Secret read only from user secrets, environment variables or secret files; used only server-side in the token request; never rendered, logged or returned by `/api/auth/providers`; test endpoint overrides ignored outside Development/Testing | `AuthOptions`, `AuthStartupReport`, `AuthProviderStatus` |
| Accounts | Takeover via a Google account with the victim's email | Google identities match only by Google subject ID. An existing account with the same email is never linked automatically; linking is an explicit action while signed in | `ExternalAccountService` |
| Accounts | Password guessing | 10+ character passwords (Identity hasher), lockout after 5 failures for 15 minutes, per-IP limit on login/register/reset; generic error messages; reset requests answer identically for unknown emails | `Login.razor`, `ForgotPassword.razor`, `AuthenticationSetup` |
| Accounts | Stale sessions after password change / "sign out everywhere" / deletion | Security stamp checked every minute (cookies and live circuits) | `IdentityRevalidatingAuthenticationStateProvider` |
| Accounts | Data left behind on deletion | Deleting an account closes its running sessions (everyone is disconnected and the text purged), deletes history and saved text, then the user | `AccountDataService.DeleteAllAsync` |
| Calls | Eavesdropping / joining another session's call | Media is DTLS-SRTP between browsers; signaling goes only through the authenticated circuit and only between members of the same session's call; per-participant rate and size limits | `CallService.SignalAsync` |
| Calls | Camera/microphone used unexpectedly | Requested only on an explicit *Join* click; `Permissions-Policy` allows them only for this origin; tracks are stopped on leave, session end, removal and page hide | `call.js`, `SecurityHeadersMiddleware` |
| Cookies | Theft / CSRF | `HttpOnly`; `Secure` outside Development; `SameSite=Lax` (needed for the OAuth return) | `AuthenticationSetup` |
| Service | Resource exhaustion | Content cap (50k chars, client + server); 512 KB message limits; global per-IP HTTP limiter; per-participant update token bucket | options + `WebApplicationSetup` |
| Location | Over-collection | Requested only on click; rounded to about 1 km; lookup made server-side; never stored or logged; can be disabled | `LocalTimeCard.razor`, `LocationDisplayService` |

## Logging policy

Logs carry internal session and participant GUIDs, outcomes and counts. They **never** contain
document text, join codes, PINs, tokens, IP addresses or coordinates. When adding log
statements, keep it that way.

## Operational notes

- **TLS:** run behind HTTPS. The app redirects to HTTPS by default (`NeuralBridge:Hosting:HttpsRedirection`) and sends HSTS outside Development.
- **Reverse proxies:** only set `NeuralBridge:Hosting:ForwardedHeadersEnabled=true` behind a proxy you control. Otherwise clients could spoof `X-Forwarded-For` and escape rate limits.
- **Data Protection keys** protect auth cookies and the per-tab participant credentials. Persist them (`NeuralBridge:Hosting:DataProtectionKeysPath`) on a private volume. Rotating them signs everyone out and forces tabs to rejoin. For stronger at-rest protection, configure a key-encryption method (certificate, Azure Key Vault, DPAPI).
- **Secrets:** Google credentials and the SMTP password come only from user secrets, environment variables or secret files (`/run/secrets` or `NEURALBRIDGE_SECRETS_PATH`). `appsettings.json` has no credential keys at all. Setting only one of ClientId/ClientSecret stops startup without printing either value.
- **Avatars:** only `https://*.googleusercontent.com` pictures are kept, loaded with `referrerpolicy=no-referrer`, and allowed by the CSP `img-src`.

## Explicit non-goals

NeuralBridge does not and must not read the OS clipboard in the background, capture the
screen, record keystrokes outside its own editor, control other devices, or help anyone
bypass organizational security controls.

## Known gaps (accepted for the MVP)

- An unprotected session's code is a bearer secret. Anyone who sees it can join until the owner rotates it or the session ends. The UI says so, and nudges owners to add a PIN.
- Content is end-to-end encrypted **in transit** (TLS) but is visible to the server process while the session runs. True end-to-end encryption, with keys in the link fragment, is on the roadmap.
- A removed participant who still knows the code can rejoin. The removal dialog tells the owner to rotate the code or set a PIN.
