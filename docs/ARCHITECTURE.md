# Architecture

The rule the code follows: **accuracy first, sync second, design throughout.** A figure is either read from a replay
or not shown; nothing is estimated to fill a gap. Solo counting never depends on the network.

```
 Fortnite ──writes──▶ Demos\*.replay
                          │  (file events + periodic scan)
                          ▼
 ┌──────────── engine\ChestCounterEngine.exe (.NET, no window) ───────────────┐
 │  ReplayTracker ─▶ ReplayAnalyzer (reference, unchanged) ─▶ LocalDb (SQLite) │
 │                                                     │            │         │
 │                                                     │        outbox        │
 │                                                     ▼            ▼         │
 │  ChestEngine (facade) ◀──────────────────────── SyncService ◀─WebSocket──┐ │
 │       │  Changed("tracking" | "matches" | "sync" | "settings")            │ │
 │       ├─▶ LocalServer: 127.0.0.1 HTTP + SSE ─▶ pages (React UI, overlay)  │ │
 │       │                                   └──▶ /obs page (OBS source)     │ │
 │       └─▶ stdout JSON lines ("ready", "changed", "pickFolder")            │ │
 └───────┬───────────────────────────────────────────────────────────────────┼─┘
         │ stdin/stdout                                                       │
 ┌───────▼──── ChestCounterSquad.exe (Tauri, Rust) ──────────┐                │
 │  main window + overlay window (WebView2), tray, shortcuts, │                │
 │  Run-key startup, standby, one instance per data folder    │                │
 └────────────────────────────────────────────────────────────┘                │
                                                                             │ HTTPS
                                                        Squad server (Docker, SQLite)
```

## Engine (`src/ChestCounter.Engine`)

**Analysis.** `Reference/ReplayAnalyzer.cs` and `Reference/ReplayHeaderProbe.cs` are copied byte for byte from
LED-N/Fortnite-ChestCounter, so fixes there can be taken as they are. `Reference/ReferenceCompat.cs` supplies the
few types they expect (localisation stub, treasure catalogue stub, records). `Analysis.cs` adapts the reference's
output to this project's model behind `IReplayAnalyzer`, applies `ModeRules` (which playlists count: regular BR
only, by playlist name), and computes the content `Fingerprint` used as a fallback match id.

**Tracking.** `ReplayTracker` reads each finished replay once. Two identities are kept apart: a *file*
(`name|size|last write`, never reopened while unchanged) and a *match* (the replicated `GameSessionId`). A copy or a
renamed replay is a new file but the same match, and changes nothing. A replay is only read once Fortnite has
written its key and its size has settled; a replay still being recorded is reported as "recording". `TrackerLoop`
wakes on file system notifications and runs a slow reconciliation scan for whatever a notification missed.

When the analyzer's version number goes up, matches analysed by an older version are queued again on start
(`RequeueOutdated`), and the new result replaces the old one.

**Storage.** `LocalDb` is one SQLite file. Every counting update is a single transaction keyed by match id, so
running it twice equals running it once. The outbox row for the squad server is written in that same transaction.

**Sync.** `SyncService` holds the device token and squad membership. Uploads are upserts keyed by
`(matchId, playerId)` and leave the outbox only when the server confirms them, so a crash or an offline evening loses
nothing. A WebSocket pushes "squad changed" so the other members' figures update without polling; the REST state is
always the source of truth.

**Treasure hunt** (`Hunt/`). `HuntCatalog` reads the same `content.json` format as the reference project (schema 1,
every value range-checked, pictures https only); a copy is embedded, and `HuntContent` replaces it with a higher
revision downloaded from the reference repository (kept as `content.json` in the data folder, checked every 6 h). It
also fetches missing prize pictures once (reference repository, then fortnite.bot) into `treasure-images/`; the
interface ships the pictures it already knows (`web/public/treasures`). `Treasures.Build` computes the whole hunt
view from the stored match details: chances `1 − Π(1 − p × boost)` per chest time, projections at the player's pace,
the collection, sources, chests by type. Treasures are stored raw (prize id, item) and named at read time, so a new
revision names old finds too. Hand-added chests and ticked prizes are settings rows, never uploaded.

**Activity** (`Activity.cs`). `ActivityFeed` listens to the tracker (scan start, each file, scan end, recording
replays) and stores one row per event in the `activity` table (last 600). A pass of 3 replays or more is one
"import" row; otherwise each match gets its own row with its chests, treasures and the total after it.

## Shared rules (`src/ChestCounter.Shared`)

`SquadAggregator` is pure and used by both the server and the tests. Squad figures are **sums of each player's own
result**, one row per `(matchId, playerId)`:

- *Combined*: each member's lifetime total, added up, whoever they played with.
- *Together*: matches where two or more members took part. A shared match is *complete*, *partial* (a member who
  was there has not uploaded yet; shown with `+`), or *ambiguous* (conflicting information about who played; left
  out of totals).

- *Treasures*: each upload carries the uploader's own treasures (raw prize id and item; the clients name them with
  their catalogue). Combined and together follow the same rules as chests. Treasures deduced for teammates
  (`TeamTreasureInfo`) are estimates and are never uploaded. An upload without the field (`null`, an older client)
  is "unknown", not zero: the interface says how many such matches there are.
- *Session*: the members' latest run of matches with no break longer than `SessionGap` (2 h) between the end of one
  and the start of the next, solo games included. It is *active* while its last match ended less than 2 h ago. This
  is the squad's own definition, computed on the server; the local session counter (since the app started) stays
  personal.

Roster entries a replay records about teammates are used only to decide who played together, and as a labelled
hint ("teammates' replays saw about N"). They never enter a total, because a replay only sees what happened near
its recorder.

New fields are added as optional fields: `Wire.ProtocolVersion` stays 1, older servers ignore what they do not know,
older clients omit it. When the client starts sending a new field it bumps `sync.payload` (`Sync.cs`), so its history
is uploaded once more with it (an upsert per `(match, player)`: nothing is counted twice).

A player id belongs to the first member who uploads it. A member who has never uploaded is not yet detectable as
"missing" from a match.

## Squad server (`src/ChestCounter.Server`)

ASP.NET Core minimal API over SQLite. Devices register and receive a bearer token (stored hashed with
`CC_AUTH_SECRET`). Squads are joined with expiring invite codes; `/join/{code}` is a small page that shows the code
and how to paste it. Settings are environment variables (`ServerOptions.cs`, `.env.example`). The server never sees
replay files, only per-match results.

## Local API (`src/ChestCounter.LocalApi`)

Kestrel on `127.0.0.1:47821` (another free port if that one is taken). Requests must use a loopback host name
(defeats DNS rebinding) and carry a random per-run token, except the read-only overlay feed for OBS. The window
receives the token by script injection limited to that origin; it never appears in the address bar. Changes are
pushed to the page by server-sent events naming a topic, and the page reloads only what that topic affects.

## Engine host (`src/ChestCounter.Host`)

The engine and the local API in a windowless process (`ChestCounterEngine.exe`). It talks to the shell with one
JSON object per line: on stdout `ready` (URL and token), `changed` (every engine change), `pickFolder` (the
interface asked for a folder, which the shell draws); on stdin `pickedFolder` and `quit`. It exits when its stdin
closes, so it never outlives the shell, even a killed one. It opens Explorer itself.

## Desktop shell (`desktop/`, Tauri 2)

- `main.rs`: one instance per data folder (named mutex, same names as before; a second launch signals the first to
  show itself), and the `--standby` mode.
- `engine.rs`: starts the engine from the `engine` folder next to the exe, waits for `ready`, and reads its events.
  Small HTTP calls (`/api/settings`) go through the same local API as the pages, with the token.
- `shell.rs`: the main window (created on first show, so starting in the tray costs no browser), pointed at the
  local API. The token is injected by an initialization script limited to that origin. Navigation outside it goes
  to the default browser. Close hides to the tray. Tray menu, shortcuts (global-shortcut plugin) and startup entries
  are re-applied on every `settings` change. Window size and position: window-state plugin.
- `overlay.rs` + `web/src/overlay.ts`: a transparent, always-on-top, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` window
  showing `overlay.html` from the engine. The page draws the figures and reports its size. The shell places the
  window, shows it without activating it, and ignores the mouse when locked so clicks reach the game. The feed also
  says whether a match is being recorded (`live`) or its replay read (`reading`), and carries the latest match with
  its treasures: the page shows the state of play, rolls its figures, and shows an end-of-match card when a match it
  saw being played is counted. Drag, wheel
  and right-click on the page go back to the shell as commands; only those are allowed to that remote origin
  (`capabilities/overlay.json`). The main window gets no Tauri access.
- `startup.rs`: the per-user Run key for "start with Windows". "Start with Fortnite" registers a windowless
  `--standby` process at logon that checks the process list every few seconds and opens the app when Fortnite runs.

## Interface (`web/`)

React + TypeScript + Vite, built into `src/ChestCounter.Host/wwwroot` (two pages: `index.html`, `overlay.html`). Colour carries meaning everywhere, including
the overlay and the OBS page: **gold** = personal, **cyan** = squad, **pink** = session, **violet** = something you
can act on. Fonts (Bricolage Grotesque, Instrument Sans) are bundled. The page itself makes no external request:
prize pictures come through the engine (`/treasure-img/{id}`). Motion uses the `motion` library (page transitions,
dialogs, the sliding nav and segmented controls) and CSS for entrances (`fill-mode: backwards`, so hover transforms
still apply), with `prefers-reduced-motion` honoured everywhere.

## Tests (`tests/ChestCounter.Tests`)

xUnit. Fixture replays (`FakeReplays.cs`) stand in for real ones so tracking rules, duplicates, mode filtering,
re-analysis, aggregation, the server API and end-to-end sync can be tested without Fortnite. They do not test the
replay parser itself against real files (see "Verification status" in the README).
