# Third-party code

Chest Counter Squad is MIT-licensed (see [LICENSE](LICENSE)). It includes code from the projects below. Their
license texts are reproduced at the end of this file.

## Reused

| Code | From | How |
|---|---|---|
| `lib/FortniteReplayReader`, `lib/Unreal.Core`, `lib/Unreal.Encryption`, `lib/OozSharp` | [Shiqan/FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor), commit 76f74c1, MIT | Copied as patched by LED-N/Fortnite-ChestCounter. Each change is listed in [`lib/PATCHES.md`](lib/PATCHES.md). No further changes here. |
| `src/ChestCounter.Engine/Reference/ReplayAnalyzer.cs`, `ReplayHeaderProbe.cs` | [LED-N/Fortnite-ChestCounter](https://github.com/LED-N/Fortnite-ChestCounter), MIT | Byte-identical copies: how chests, ammo boxes and containers are recognised and counted, and how a replay's readiness is probed. |

## Adapted from the same reference (rewritten, not copied)

- `Reference/ReferenceCompat.cs`: stand-ins for the reference types the two files above expect.
- Replay readiness rules in `Tracking.cs` (live flag, key written, size stable).
- The "start with Fortnite" standby idea (`desktop/src/startup.rs`, after the reference's `FortniteWatch.cs`).
- The native overlay's window technique (`OverlayWindow.cs`, after the reference's `NativeOverlay.cs`): rewritten
  to draw with per-pixel alpha.

## Written for this project

Everything else: the engine around the analyzer (tracking, local database, sync client, outbox), the shared
aggregation rules, the squad server, the local API and OBS page, the desktop shell, the web interface, the dev host
and the tests. The reference project's own interface, report writer, updater, profiles and game-mode tables
(`GameModes.cs`) are not used.

## Bundled at runtime

- .NET runtime, ASP.NET Core, Windows Forms, Microsoft.Data.Sqlite and SQLitePCLRaw (MIT; SQLite itself is public
  domain).
- Microsoft.Web.WebView2 SDK (Microsoft's WebView2 license, BSD-style). The WebView2 runtime is part of Windows and
  is not redistributed.
- Web interface: React (MIT), Motion (MIT), lucide-react (ISC). Fonts: Bricolage Grotesque and Instrument Sans (SIL Open Font
  License 1.1).

Not affiliated with or endorsed by Epic Games. Fortnite is a trademark of Epic Games, Inc.

---

## License: FortniteReplayDecompressor

```
MIT License

Copyright (c) 2020-2026 Shiqan

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## License: Fortnite-ChestCounter

```
MIT License

Copyright (c) 2026 LED-N

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
