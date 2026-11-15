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
- **Treasure hunt.** Treasures found in your replays (named for sure when a golden chest carries its prize id), the
  chance each treasure of the week and of the season has already dropped for you (with Treasure Hour boosts), your
  collection (ticked by itself when a replay shows the prize), where your treasures came from, and chests by type.
  Hunt data (odds, weeks, prizes, Treasure Hours) is built in and refreshed from the reference project's
  `content/content.json`; prize pictures are fetched once from its repository or fortnite.bot and kept in the data
  folder. Nothing about you is sent.
- **Activity feed.** A live timeline on the home page: match in progress, replay being read, each match's chests,
  treasures and new total, big imports summed up in one card.
- **Chests added by hand.** For chests opened before you used the app (the pencil next to your total), with an
  estimate from your Career match count. They stay on your PC and are never part of a squad figure.
- **Squad totals.** Optional. Each player runs the app, and a small self-hosted server combines what each of you
  opened. Two figures are shown: *combined* (each member's lifetime total, added up) and *together* (only matches you
  played as a team). Join with a single invite link.
- **In-game overlay.** A separate transparent, always-on-top window that never takes the focus. Drag it to move, scroll on it to resize,
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
- The desktop shell is now Tauri (`desktop/`). On Windows 11: the main window, the overlay, its two shortcuts
  (show, lock) were checked, the overlay does not take the focus, and the engine stops when the shell is killed.
  **Dragging and resizing the overlay, its right-click menu, the folder picker and the tray menu have not been
  checked yet in the Tauri version.** "Start with Windows" and "start with Fortnite" were not exercised end to end.

## Install and run

Download `ChestCounterSquad-win-x64.zip`, unzip it anywhere, run `ChestCounterSquad.exe` (keep the `engine`
folder next to it). Nothing else is needed on Windows 10 or 11: the .NET runtime is bundled in the engine, and the
Microsoft Edge WebView2 runtime ships with Windows.

On first launch the app finds Fortnite's replay folder
(`%LOCALAPPDATA%\FortniteGame\Saved\Demos`), imports what is there, and keeps watching it.

To bring over history from the original Chest Counter, open Settings → Import from the original Chest Counter.
The app detects `%APPDATA%\ChestCounter\data.json` automatically, or you can choose another file.
Already recorded matches are not duplicated. Imported matches with an
Epic ID are also queued for squad sync. Matches without a reliable session ID, unsupported modes, and manual or
estimated additions are excluded; the import reports how many matches were skipped.

| Where | What |
|---|---|
| `%APPDATA%\ChestCounterSquad\history.db` | Your history and settings (one SQLite file) |
| `%APPDATA%\ChestCounterSquad\backups`, `exports` | Backups and CSV exports made from Settings |
| `%APPDATA%\ChestCounterSquad\webview` | The embedded browser's profile |
| `%APPDATA%\app.chestcounter.squad` | The main window's size and position |
| `HKCU\...\CurrentVersion\Run`: `ChestCounterSquad`, `ChestCounterSquad-Standby` | Only when "start with Windows" or "start with Fortnite" is on |

Command line: `--minimized` (start in the tray), `--data <folder>` (another data folder, e.g. a second profile),
`--standby` (the "start with Fortnite" watcher; you never need to run it yourself).

To uninstall, turn off both startup options in Settings, quit from the tray, then delete the app folder,
`%APPDATA%\ChestCounterSquad` and `%APPDATA%\app.chestcounter.squad`.

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

Requirements: .NET SDK 10, Node.js 20 or newer, Rust (stable, MSVC toolchain) with the Visual Studio C++ Build
Tools. On the development PC .NET and Node are portable copies under `.tools/`.

```bash
npm --prefix web ci
```

```bash
npm --prefix web run build
```

```bash
dotnet test tests/ChestCounter.Tests
```

The web build writes into `src/ChestCounter.Host/wwwroot`, which the engine serves. The Tauri shell starts the
engine from an `engine` folder next to its own exe, so publish the engine there once, then build and run the shell:

```bash
dotnet publish src/ChestCounter.Host -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o desktop/engine
```

```bash
cargo run --manifest-path desktop/Cargo.toml -- --data "%TEMP%\cc-dev"
```

`CC_ENGINE=<path to ChestCounterEngine.exe>` points the shell at another engine build. A debug build of the shell
never writes the startup entries. To produce the release folders in `dist/`:

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
| `src/ChestCounter.Host` | The engine as a windowless process the shell starts (JSON lines over stdin/stdout) |
| `desktop/` | Windows shell (Tauri, Rust): main window, tray, overlay window, shortcuts, startup |
| `web/` | The interface and the overlay page (React, TypeScript, Vite) |
| `tools/DevHost` | Fixture-data host for interface work |
| `tests/` | xUnit tests: engine, aggregation, server API, sync end to end |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the parts fit together.

## License

MIT, see [LICENSE](LICENSE). Third-party code and its licenses are listed in [NOTICE.md](NOTICE.md).
