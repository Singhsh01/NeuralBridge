# Roadmap and assessments

These items were reviewed during the interface redesign. None of them is implemented. Each
entry says what it would give people, what it would change in the code, and the main risks,
so it can be planned separately. Effort is a rough guide for one developer who knows the
codebase: S is a few days, M one to two weeks, L more than that.

The privacy model is the constraint on every item: shared text stays in server memory only,
is never logged, and disappears when the session ends. Anything that would store text has to
be explicit and opt-in.

## Priority B: candidates for the next release

### Read-only code view (S)

**What:** a toggle above the editor that shows the same text with syntax highlighting and
line numbers, without changing the editor itself.

**How:** a second, read-only `<pre>` rendered in the browser from the editor's value, with a
small highlighter vendored like the QR encoder (no CDN; strict CSP allows it because it is a
same-origin script). Language detection by heuristics, with a manual override. The
`textarea` stays the only input, so sync, the status pill and shortcuts are unaffected.

**Risks:** highlighters that build HTML are an XSS surface. Use one that emits DOM text nodes,
or escape every token, and add an E2E test with the existing XSS payload. Large documents
(50k characters) must not block the main thread; highlight lazily or in a worker.

### Owner activity panel (S–M)

**What:** a collapsible panel for owners listing what happened in this session: joins,
leaves, removals, PIN changes, code rotation, extensions, clears and lockouts.

**How:** the data already exists. `SessionEvent` records these types (`ParticipantJoined` …
`PinLockout`) and never contains text, codes, PINs or tokens. Add an
`ISessionActivityService.ListAsync(credentials)` in Application that re-authorizes the owner
and maps events to display rows (time, kind, device name), and a panel component that
refreshes on the existing event-bus notifications.

**Risks:** device names are user input; render them as text only (Razor already encodes).
Keep it owner-only, and do not add new event types that would carry content.

### Several documents per session (proposal only, M–L)

**What:** tabs inside a session ("Prompt", "Notes", "Links") so people can keep separate
pieces of text without opening several sessions.

**Proposed design:**

- Domain: `SharedSession` gains a small, capped list of document descriptors (id, title,
  order). Titles are metadata and may be stored; content stays in `IDocumentStore`.
- Store: `IDocumentStore` becomes keyed by `(sessionId, documentId)`. The default document
  keeps today's behaviour, so the API and hub stay compatible (`documentId` optional).
- Sync: `TextSynchronizationService.UpdateAsync` and the `DocumentChanged` notification take
  a `documentId`; `editor.js` binds to the active tab and ignores changes for other tabs
  except to mark them as updated.
- Owner controls: add, rename, reorder and delete tabs; a cap (for example 8 tabs) and the
  existing per-document character limit, plus a per-session total.
- UI: a tab strip above the editor; copy and download act on the active tab, with
  "Download all" producing one `.txt` per tab or a single file with headings.

**Test impact:** every sync test gains a document dimension; new tests for isolation between
tabs, deletion purging content, cleanup sweeping every tab, and the hub contract staying
backwards compatible. The E2E suite needs tab switching on two devices and on phones.

**Risks:** more state to purge on close; more rate-limit surface (per tab and per session);
the phone layout gets tighter. Ship behind a configuration flag.

### CRDT or OT merging (L)

**What:** typing at the same moment on two devices merges instead of the later write
replacing the earlier one. Today the status pill tells people when that happened
("Your edit replaced a newer one").

**Options:**

- **CRDT (Yjs-compatible):** best fit for text with presence (remote cursors). It needs a
  Yjs client bundle in the browser and a server that either relays updates opaquely or
  understands the format (for .NET, `YDotNet` bindings, or treating updates as opaque blobs
  with a periodic snapshot). It also prepares the ground for end-to-end encryption, because
  updates can be encrypted and relayed blind.
- **OT:** smaller client, but the server must transform every operation, and correctness is
  notoriously hard to get right.

**Where it plugs in:** `IDocumentMergeStrategy` handles whole-text writes today. A CRDT
changes the wire format (incremental updates instead of whole text), so it means a new
editor module and a new hub/circuit message, not just a new strategy class.

**Risks:** memory per session grows with the update history (needs compaction); the
50k-character limit has to be enforced on the materialised text; migration of in-flight
sessions is unnecessary because text is ephemeral.

## Priority C: assessed, not planned yet

### End-to-end encryption (L)

Generate a key in the browser, put it in the URL fragment (`#k=…`, never sent to the server),
and encrypt every update with AES-GCM before sending. The server would relay ciphertext only.
Consequences: whole-text last-write-wins still works (ciphertext replaces ciphertext), but the
server can no longer detect links, enforce the character limit on plaintext, or offer
"Save to my account" without the browser decrypting first. The QR code and join link would
have to carry the fragment, which means the key travels wherever the link does; a PIN would
no longer protect content on its own. Needs an independent review before it can be described
as end-to-end encrypted.

### Redis for scale-out (M)

Implement `IDocumentStore`, `ISessionEventBus`, `IPresenceTracker`, `ISessionLockProvider`
and the call registry on Redis, add the SignalR Redis backplane, and use sticky sessions for
Blazor Server circuits. Text in Redis must have a TTL equal to the session expiry and Redis
persistence (RDB/AOF) must be disabled for that keyspace, or the "memory only" promise breaks.

### TURN server for calls (S to configure, ongoing cost to run)

`NeuralBridge:Calls:IceServers` already accepts TURN entries. What is missing is short-lived
credentials (the TURN REST API pattern: HMAC of an expiry time with a shared secret) issued
per call by the server, so a static TURN password never reaches the browser. Bandwidth cost
is the main consideration.

### File and image sharing (M–L)

Small files relayed through the server would need size limits, type checks, malware
scanning, memory accounting per session and purging on close. Peer-to-peer transfer over the
existing WebRTC data path avoids storing anything on the server but only works while both
devices are online. The second fits the privacy model better.

### More sign-in options (S–M each)

Passkeys (WebAuthn) fit well: ASP.NET Core Identity in .NET 10 supports them. Microsoft or
GitHub sign-in would reuse the OAuth plumbing behind Google. Accounts must stay optional.

### Command-line client (S–M)

A small CLI on the existing `/api/sessions` endpoints and the `/hubs/session` hub:
`nb create`, `nb join CODE`, `echo text | nb send`, `nb watch`. The participant token would be
kept in memory for the life of the process only.
