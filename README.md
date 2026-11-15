# Chest Counter Squad

A Windows app that counts the chests you open in Fortnite Battle Royale, and adds up your squad's chests across
everyone's PCs.

It reads the replay files Fortnite writes at the end of each match. It never touches the game process, reads no
memory, and injects nothing. The in-game counter is an ordinary always-on-top window.

> Not affiliated with Epic Games. Replay analysis comes from
> [LED-N/Fortnite-ChestCounter](https://github.com/LED-N/Fortnite-ChestCounter) and
> [Shiqan/FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor), both MIT. See
> [NOTICE.md](NOTICE.md).

## What it does

- **Personal history.** Every finished match is analysed once and stored locally (SQLite): chests opened, ammo
  boxes, other containers, placement, mode. Totals, sessions, per-day and per-week charts, best match, CSV export,
  backups.
- **Squad totals.** Optional. Each player runs the app, and a small self-hosted server combines what each of you
  opened. Two figures are shown: *combined* (each member's lifetime total, added up) and *together* (only matches you
  played as a team). Join with a single invite link.
- **In-game overlay.** A separate transparent window, drawn natively. Drag it to move, scroll on it to resize,
  lock it to make clicks pass through. Default shortcuts are Ctrl+Shift+O (show or hide) and Ctrl+Shift+L (lock).
  It shows over windowed and borderless Fortnite, not over exclusive fullscreen.
- **OBS source.** `http://127.0.0.1:47821/obs` is a transparent page with the same figures. Add it as a Browser
  source.
- **Background mode.** Runs from the tray. It can start with Windows, or start when Fortnite starts.

## Limits (read these)

These come from what a replay file contains. The app documents them instead of estimating around them.

1. **No live count.** Fortnite only writes the key that unlocks a replay when the match ends, so a match is counted
   after it finishes.
2. **Battle Royale only.** Regular BR, build and zero build, in solo, duo, trio and squad. Ranked, Reload, OG,
   limited-time modes and Creative are read but not counted (playlist rules in `ModeRules`,
   `src/ChestCounter.Engine/Analysis.cs`).
3. **A replay only holds what happened near the recording player.** That is why every squad member counts their
   own chests on their own PC and uploads only their own result. Teammates' figures seen from your replay are shown
   as a labelled hint and never added to a total.
4. **Vehicles.** Chests destroyed by a fast vehicle are sometimes missing from the replay. Those matches are
   flagged "may be missing chests".
5. **Replays must be on.** In Fortnite: Settings, Game, Replays, "Record Replays". Fortnite keeps a limited number
   of replays, so matches played while the app was closed for a long time may already be gone.
6. **Matching a match across PCs** uses the match's `GameSessionId`, which every recorder of a match should share.
   Replays without one are kept locally but never matched with teammates. A match where the server cannot tell who
   played together for sure is shown as *ambiguous* and left out of squad totals.

### Verification status

- The replay analyzer is the reference project's code, kept byte-identical
  (`src/ChestCounter.Engine/Reference/`). **In this repository it has only been run on synthetic fixture replays,
  never on a real Fortnite replay.** Cross-device matching by `GameSessionId` is likewise unverified on real data.
- The Docker image of the squad server **has never been built** here (no Docker on the development PC). The server
  itself runs and is covered by integration tests.
- The desktop shell has been run on Windows 11: window, WebView2, tray, overlay and the local API were checked by
  hand. "Start with Windows" and "start with Fortnite" were not exercised end to end.

## Install and run

Download `ChestCounterSquad-win-x64.zip`, unzip it anywhere, run `ChestCounterSquad.exe`. Nothing else is
needed on Windows 10 or 11: the .NET runtime is bundled, and the Microsoft Edge WebView2 runtime ships with Windows
(if it is missing, the app offers to open the interface in your browser instead).

On first launch the app finds Fortnite's replay folder
(`%LOCALAPPDATA%\FortniteGame\Saved\Demos`), imports what is there, and keeps watching it.

| Where | What |
|---|---|
| `%APPDATA%\ChestCounterSquad\history.db` | Your history and settings (one SQLite file) |
| `%APPDATA%\ChestCounterSquad\backups`, `exports` | Backups and CSV exports made from Settings |
| `%APPDATA%\ChestCounterSquad\webview` | The embedded browser's profile |
| `HKCU\...\CurrentVersion\Run`: `ChestCounterSquad`, `ChestCounterSquad-Standby` | Only when "start with Windows" or "start with Fortnite" is on |

Command line: `--minimized` (start in the tray), `--data <folder>` (another data folder, e.g. a second profile),
`--standby` (the "start with Fortnite" watcher; you never need to run it yourself).

To uninstall, turn off both startup options in Settings, quit from the tray, then delete the app folder and
`%APPDATA%\ChestCounterSquad`.

## Host a squad server

One person (or a small VPS) runs the server; everyone else just pastes an invite link. The server stores display
names, player ids, and per-match results. It never receives replay files.

### With Docker

```bash
cp .env.example .env
```

```bash
docker compose up -d
```

Edit `.env` first. The settings that matter:

| Variable | Default | Meaning |
|---|---|---|
| `CC_PORT` | `8080` | Port published on the host |
| `CC_PUBLIC_URL` | (request address) | Address friends use; written into invite links. Set it when behind a proxy or NAT |
| `CC_AUTH_SECRET` | generated | Key device tokens are hashed with. Generated once into the data volume if empty |
| `CC_REGISTRATION_KEY` | (none) | When set, a new PC must enter it to register. Use it on a public address |
| `CC_ADMIN_TOKEN` | (none) | Enables `/api/v1/admin/stats` |
| `CC_MAX_SQUAD_SIZE` | `4` | Members per squad (2 to 16) |
| `CC_INVITE_TTL_HOURS` | `168` | Default invite lifetime |
| `CC_TRUST_PROXY` | `0` | `1` only behind a reverse proxy that sets `X-Forwarded-*` |

For anything reachable from the internet, put it behind a reverse proxy with TLS (Caddy, nginx, Traefik), set
`CC_TRUST_PROXY=1`, `CC_PUBLIC_URL=https://your.domain`, and a `CC_REGISTRATION_KEY`. Health check: `GET /healthz`.
Data lives in the `squad-data` volume (`/data/squads.db` plus `auth-secret.key`); back up both together.

### Without Docker

`dist/ChestCounterSquad-Server-win-x64/ChestCounter.Server.exe` is self-contained. Set the same variables in the
environment, plus `CC_DATABASE` (SQLite file path) and `CC_LISTEN` (e.g. `http://0.0.0.0:8080`), and run it. On
Linux, `dotnet publish src/ChestCounter.Server -c Release` and run `dotnet ChestCounter.Server.dll` with the same
variables.

## Build from source

Requirements: .NET SDK 10, Node.js 20 or newer. On the development PC both are portable copies under `.tools/`.

```bash
npm --prefix web ci
```

```bash
npm --prefix web run build
```

```bash
dotnet test tests/ChestCounter.Tests
```

```bash
dotnet run --project src/ChestCounter.App
```

The web build writes into `src/ChestCounter.App/wwwroot`, which the app serves. To produce the release folders in
`dist/`:

```bash
powershell -ExecutionPolicy Bypass -File build/publish.ps1
```

### Working on the interface without Fortnite

`tools/DevHost` runs the engine and the local API on synthetic fixture replays (all marked as fixture data), so the
interface can be developed in a normal browser:

```bash
dotnet run --project src/ChestCounter.Server
```

```bash
dotnet run --project tools/DevHost -- --server http://127.0.0.1:8080
```

Then open `http://127.0.0.1:47821/?token=dev`. For hot reload, also run `npm --prefix web run dev` and open
`http://localhost:5173/?token=dev` (it forwards `/api` and `/obs` to the dev host).

## Project layout

| Path | What |
|---|---|
| `lib/` | Replay parser libraries from FortniteReplayDecompressor, with the reference project's patches (`lib/PATCHES.md`) |
| `src/ChestCounter.Engine` | Analysis adapter, local database, replay tracking, sync client |
| `src/ChestCounter.Shared` | Wire contracts and the squad aggregation rules (pure, shared by client and server) |
| `src/ChestCounter.Server` | Self-hosted squad server (ASP.NET Core minimal API, SQLite, WebSocket push) |
| `src/ChestCounter.LocalApi` | Loopback HTTP API and the OBS page |
| `src/ChestCounter.App` | Windows shell: WebView2 window, tray, native overlay, hotkeys, startup |
| `web/` | The interface (React, TypeScript, Vite) |
| `tools/DevHost` | Fixture-data host for interface work |
| `tests/` | xUnit tests: engine, aggregation, server API, sync end to end |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the parts fit together.

## License

MIT, see [LICENSE](LICENSE). Third-party code and its licenses are listed in [NOTICE.md](NOTICE.md).
