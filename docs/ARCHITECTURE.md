# NeuralBridge — Architecture & Phase 1 Plan

## 1. Product in technical terms

NeuralBridge is a single-origin ASP.NET Core (.NET 10 LTS) web application. It hosts
short-lived, access-controlled **sessions**. Each session holds exactly one **shared
plain-text document** (up to 50,000 characters by default). Browsers join a session with a
human-typeable **join code** (plus an optional PIN). They then read and write the document
through a Blazor Interactive Server component.

- Edits are debounced in the browser (≈250 ms) and sent to the server as whole-document
  replacements carrying the version they were based on.
- The server applies a merge strategy (MVP: last-write-wins), bumps a monotonically
  increasing version and publishes a `DocumentChanged` notification. The notification goes
  to every other connected participant of that session: other Blazor circuits, and SignalR
  hub connections from API clients.
- Document text is held **in process memory only** and is purged when the session closes or
  expires. The relational database stores session metadata only: code, expiry, status,
  hashed PIN, hashed participant tokens and audit events without content. Text that a
  signed-in user explicitly saves goes to a separate table.

It is not a remote-desktop tool. It never reads the OS clipboard (it only *writes* on an
explicit button click), never captures keystrokes outside its own text area, and never opens
URLs automatically.

## 2. Architecture

```
┌────────────────────── NeuralBridge.Web (ASP.NET Core host) ──────────────────────┐
│ Blazor Web App (static SSR shell + Interactive Server islands/pages)             │
│   Landing · Join · Workspace · My sessions · Saved · Privacy                     │
│ SessionHub (/hubs/session)  ← SignalR API for non-Blazor clients                 │
│ Minimal APIs (/api/sessions, /api/sessions/join, /account/*, /healthz)           │
│ Middleware: security headers + CSP, rate limiter, exception handler, HSTS        │
│ HubNotificationRelay: session event bus → hub groups                             │
└───────────────┬───────────────────────────────────────────────────────────────────┘
                │ uses
┌───────────────▼──────────── NeuralBridge.Application ─────────────────────────────┐
│ SessionService · SessionOwnerService · SessionAuthorizationService                │
│ TextSynchronizationService (+ IDocumentMergeStrategy) · SessionCleanupService     │
│ SnippetService · LocationDisplayService · DTOs · Options · abstractions (ports)   │
└───────────────┬───────────────────────────────────────────────────────────────────┘
                │ implements ports
┌───────────────▼──────────── NeuralBridge.Infrastructure ──────────────────────────┐
│ Security: SessionCodeGenerator (CSPRNG), Pbkdf2SecretHasher, TokenService         │
│ Realtime: InMemorySessionEventBus, InMemoryPresenceTracker, InMemoryDocumentStore │
│ Persistence: EF Core (SQLite / PostgreSQL) + InMemory provider                    │
│ Identity: ApplicationUser, EF Identity stores · Location: Nominatim geocoder       │
│ Hosted: SessionCleanupHostedService                                               │
└───────────────┬───────────────────────────────────────────────────────────────────┘
┌───────────────▼──────────── NeuralBridge.Domain ──────────────────────────────────┐
│ SharedSession (aggregate) · SessionParticipant · SessionEvent · SessionCode       │
│ SharedDocument · SavedSnippet · invariants & domain exceptions (no dependencies)  │
└───────────────────────────────────────────────────────────────────────────────────┘
```

### Real-time path

1. `editor.js` listens to `input` on the `<textarea>`. It debounces for 250 ms, then calls
   `.NET OnLocalEditAsync(text, baseVersion)` over the Blazor circuit. Each keystroke is
   **not** a server call.
2. `TextSynchronizationService.UpdateAsync` checks authorization (participant token, edit
   rights, session active) and length. It applies `IDocumentMergeStrategy`, stores the
   result in `IDocumentStore`, and publishes `DocumentChanged { OriginId }` on
   `ISessionEventBus`.
3. Every subscriber for that session receives the event: Blazor workspace components and
   `HubNotificationRelay`. The subscriber whose `OriginId` matches ignores it, and the hub
   relay uses `GroupExcept(originConnection)`. This prevents circular updates.
4. Receivers call `editor.applyRemote(text, version)`. It keeps the caret position stable
   using a prefix/suffix diff. If the local user has unsent edits, the local text wins and
   is sent on the next flush (documented LWW).

### Why an in-process bus *and* a hub?

Blazor Server circuits already run over SignalR. For Blazor to talk to its own hub, it
would have to open a loopback HTTP client connection to itself. That doubles connections
and complicates cookies and TLS behind proxies. Blazor components therefore subscribe to
the in-process bus directly. The dedicated `SessionHub` exposes the same service layer to
other clients (scripts, future native apps) with strict per-session group isolation. Both
sit behind `ISessionEventBus`, so a Redis backplane can replace the in-memory bus for
scale-out.

### Concurrency

- Session mutations are serialized per session with `ISessionLockProvider`, an in-process
  `SemaphoreSlim` per session id.
- Document updates are atomic in `InMemoryDocumentStore`, via a lock per document.
- `IDocumentMergeStrategy` is the seam for OT or a CRDT later.

## 3. Folder structure

```
NeuralBridge/
├─ NeuralBridge.sln · global.json · Directory.Build.props · Directory.Packages.props
├─ .editorconfig · .gitignore · .dockerignore · Dockerfile · docker-compose.yml · README.md
├─ docs/ARCHITECTURE.md · docs/SECURITY.md
├─ src/
│  ├─ NeuralBridge.Domain/          Sessions/, Documents/, Snippets/, Common/
│  ├─ NeuralBridge.Application/     Abstractions/, Options/, Sessions/, Documents/, Realtime/,
│  │                                Snippets/, Location/, DependencyInjection.cs
│  ├─ NeuralBridge.Infrastructure/  Security/, Realtime/, Persistence/{InMemory,EntityFramework}/,
│  │                                Identity/, Location/, Hosting/, DependencyInjection.cs
│  └─ NeuralBridge.Web/             Components/{Layout,Pages,Session,Landing,Shared}/, Hubs/,
│                                   Api/, Auth/, Security/, Infrastructure/, wwwroot/{css,js,fonts}
└─ tests/
   ├─ NeuralBridge.Tests/           xUnit unit + service-level integration tests
   └─ NeuralBridge.E2ETests/        Playwright (Node/TypeScript) browser + API tests
```

## 4. Security risks and mitigations

| Risk | Mitigation |
|---|---|
| Brute-force/enumeration of join codes | 12-char Crockford Base32 codes (60 bits, CSPRNG); per-IP rate limits on create/join (HTTP limiter + app-level limiter for circuit calls); generic "not found or PIN incorrect" response for unknown codes and wrong PINs |
| PIN guessing | PBKDF2-SHA512 (210k iterations, 16-byte salt); 5 failed attempts lock the session's PIN entry for 5 minutes; rate limits |
| Session hijack through leaked participant token | 256-bit random tokens; only SHA-256 hashes stored; kept in ASP.NET Data Protection-encrypted tab `sessionStorage`; validated on every operation; revoked on removal/close |
| Unauthorized owner operations | Every owner operation re-authorizes the participant token *and* checks `IsOwner` (or the signed-in owner's user id) on the server |
| Cross-session leakage over SignalR | Hub connections bind to one session in `Attach`; all later calls use server-side connection state and never a client-supplied session id; broadcasts target `session:{id}` groups only |
| XSS through shared content | Content is only ever set as a textarea `value` or rendered through Razor text encoding (never `MarkupString`); strict CSP (`script-src 'self'`, no inline script); URL detection allows only `http`/`https` and renders with `rel="noopener noreferrer nofollow"`; links never open automatically |
| CSRF | Blazor SSR forms use antiforgery; logout is POST + antiforgery; JSON APIs don't use cookie auth |
| Clickjacking | `frame-ancestors 'none'` + `X-Frame-Options: DENY` |
| Code leaking through Referer | `Referrer-Policy: no-referrer` |
| Sensitive data in logs | Structured logs carry internal ids and outcomes only; never content, codes, PINs, tokens or coordinates |
| Secrets in source control | Google credentials only through user secrets or environment variables; `.gitignore` excludes `*.db`, keys, `secrets.json` |
| DoS through huge payloads | Max content length (50k chars) enforced client- and server-side; circuit/hub receive-size limits; per-connection update rate limit |
| Stale sessions | Lazy expiry check on every access plus a background sweeper; content purge; metadata deleted after retention |

## 5. MVP acceptance criteria

1. `dotnet build` succeeds; the app starts with SQLite and no Google credentials.
2. The landing page offers Create and Join. Create makes a session without registration and shows code, link and copy buttons.
3. A second browser context joins by code and sees the text; edits sync in both directions within about 1 s.
4. PIN-protected sessions require the PIN; wrong PINs give a generic error and lead to lockout.
5. Expired or closed sessions reject joins and edits, show an explanation to connected users and purge content.
6. Owner controls work: close, clear, extend, remove participant, guest editing toggle, rotate code, set/remove PIN. Non-owners are rejected server-side.
7. Copy to clipboard happens only on an explicit click.
8. Google sign-in appears only when configured. Signed-in users can list, open and close their sessions and save snippets.
9. The UI is responsive (≥320 px), keyboard-accessible, supports light/dark and reduced motion.
10. Unit tests and Playwright E2E cover the workflows listed in the brief.

## 6. Assumptions

- Single app instance for the MVP. The document store, event bus, presence and locks are
  in process. Scale-out needs Redis (backplane plus store) and sticky sessions for Blazor.
  This is documented as a known limitation.
- "Session ID" in the UI means the **join code** (e.g. `7KQ4-M2XD-9PHT`). Database keys
  (GUIDs) are never shown. The workspace URL uses a separate random public id.
- Guest owners prove ownership with the participant token issued at creation. Losing the
  tab (sessionStorage) loses guest ownership. Signed-in owners can reopen from "My sessions".
- The city is resolved only after the user clicks "Enable location" and the browser grants
  permission. Coordinates are rounded to about 1 km and reverse-geocoded server-side through
  OpenStreetMap Nominatim. This can be disabled in configuration, and then only rounded
  coordinates and the time zone are shown. No weather is shown or claimed.
- The Playwright suite is written in TypeScript (`@playwright/test`). It works on every OS
  and doesn't need the Playwright .NET package.

## Accounts and sign-in

- `Web/Auth/AuthenticationSetup` registers ASP.NET Core Identity (always), the Google OAuth handler (only when both credentials are configured), cookies and the email sender, and publishes an `AuthProviderStatus` (three booleans) that the UI and `GET /api/auth/providers` use.
- `GoogleOAuthSetup` uses the shared-framework `AddOAuth` handler with Google's endpoints and PKCE. Because it is framework code, the same handler runs against the mock provider in the Playwright suite.
- `AccountEndpoints` holds the HTTP-only steps (challenge, callback, link, logout). `ExternalAccountService` holds the decisions (match by subject ID, create, refuse automatic linking) so they are unit-testable.
- `IAccountDataService` (Application) owns account-scoped session data: history (metadata only, `AccountHistoryRetentionDays`), clearing it, and deleting everything on account deletion.

## Calls

- `ICallService` (Application) authorizes every operation with the participant's credentials, tracks members in `ICallRegistry` (in memory) and publishes `CallStateChangedNotification` / `CallSignalNotification` on the session event bus.
- The Blazor circuit relays signals to `CallPanel`, which drives `wwwroot/js/call.js`: a WebRTC mesh using the perfect-negotiation pattern (the first offer always comes from the impolite peer, signals are processed in order, and a watchdog renegotiates with an ICE restart if a handshake is lost).
- `CallJanitorHostedService` clears a call when its session ends or a participant is removed.
