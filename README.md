# Kaption — real-time in-game subtitle translation

[![Website: kaption.one](https://img.shields.io/badge/website-kaption.one-2563EB?style=flat&logo=icloud&logoColor=white)](https://kaption.one)
[![Download](https://img.shields.io/badge/download-Windows-2563EB?style=flat&logo=windows&logoColor=white)](https://kaption.one/#download)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/wojciechowskiapp/kaption)
[![License: AGPL-3.0 OR Commercial](https://img.shields.io/badge/license-AGPL--3.0%20OR%20Commercial-blue.svg)](./LICENSE)

Kaption translates the dialogue in Hoyoverse games (Genshin Impact and Honkai: Star Rail) in real time on Windows. It reads on-screen text, matches it against the game's own translation strings, and renders an overlay above the dialogue box. No streaming, no cloud OCR. Everything runs on your machine.

The official build, install instructions, supported languages, pricing, and changelog are at [kaption.one](https://kaption.one). This repository is the full source of the desktop client, published source-available under AGPL-3.0 + Commercial. You can browse it, audit it, build it. Or just grab the installer:

> Download Kaption for Windows: [kaption.one/#download](https://kaption.one/#download)

For a guided tour of the codebase, ask [DeepWiki](https://deepwiki.com/wojciechowskiapp/kaption).

## What it does

- Reads the dialogue from your game window using GPU-accelerated screen capture and the PaddleOCR engine.
- Matches the recognised text against the game's TextMap with a three-stage matcher (SymSpell, then n-gram filter, then OCR-confusion-weighted Levenshtein) so typos and OCR noise don't break recognition.
- Renders an anchored overlay above the dialogue box. Quest banner, NPC card, and answer translations are optional and toggleable in Settings.
- Auto-detects your dialogue region on first launch, walks you through a four-step setup, and remembers per-game profiles.
- Ships with the .NET 10 Desktop runtime bundled in the installer. Velopack handles delta updates after that.
- Crash reporting is opt-in. Local-only unless you turn it on at first launch.

End-to-end latency on a mid-range machine: about 80 ms from text appearing on screen to translation showing up.

## Languages

Genshin Impact and Honkai: Star Rail are supported out of the box, with per-game OCR tuning. The primary translation target is Polish. Other language pairs work whenever a translation pack exists; new locales can be added on the backend without a client update.

## System requirements

- Windows 10 version 2004 or later, 64-bit (`WDA_EXCLUDEFROMCAPTURE` needs 2004).
- 64-bit CPU with AVX (PaddleOCR requirement).
- About 500 MB of disk for translation packs.
- A supported Hoyoverse game installed.

GPU OCR is preferred. A DirectX 12 / DirectML-capable GPU recognises text in under 10 ms. The CPU path works on any AVX CPU at 40–80 ms per frame.

## Install

End users: download the installer from [kaption.one/#download](https://kaption.one/#download). Velopack takes over for updates after that.

The installer is self-contained. You don't need to install .NET or any other runtime; it's all in the package.

## Per-device file protection

Translation packs use AES-256-CBC and HMAC-SHA256 with PBKDF2-stretched keys. The 32-byte upstream secret is issued per device by Kaption's backend the first time you start, then mixed with your local machine fingerprint. A `.gisub` pulled off one machine cannot be decrypted on another, and nothing in this source tree is usable as a key on its own. The secret is fetched once on first launch (you'll see a brief "Preparing translations…" dialog) and then cached DPAPI-wrapped on disk.

## Building from source

You can build, modify, and audit this code under AGPL-3.0. Selling a rebranded fork or shipping Kaption code in a closed-source product needs either AGPL-3.0 compliance or a commercial licence. See [`LICENSE`](./LICENSE).

Requirements:

- .NET 10 SDK 10.0.203 or newer, from [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download). Get the SDK package, not just the runtime.
- Windows 10 or newer. The desktop project targets `net10.0-windows`; building on Linux or macOS won't produce a usable binary.

```
dotnet build GI-Subtitles/GI-Subtitles.csproj -c Debug
dotnet test  GI-Test/GI-Test.csproj            -c Debug
```

Expected on a clean checkout: 188 tests pass, 2 pre-existing data-dependent fails (`DialoguePredictionTests`), 5 external-data skips.

For a self-contained Release build that bundles the runtime (the same shape end users get from the official installer):

```
dotnet publish GI-Subtitles/GI-Subtitles.csproj ^
    -c Release -r win-x64 --self-contained true
```

Output lands under `GI-Subtitles/bin/Release/net10.0-windows/win-x64/publish/`.

## Configuration

Settings live at `%APPDATA%\Kaption\Config.json`. See [`CONFIG.md`](./CONFIG.md) for every key. Short version:

- Redirect the client to your own backend by setting `ApiUrl`, `AppUrl`, `UpdateFeedUrl`.
- Disable crash reporting with `SentryDsn=""`.

## Project layout

- `GI-Subtitles/` is the WPF app: OCR pipeline, matcher, overlay rendering, settings UI, licensing, networking.
- `PaddleOCRSharp/` is an ONNX-Runtime-backed wrapper around PaddleOCR.
- `Screenshot/` does screen capture (DXGI default, GDI fallback) and the region-selection UI.
- `GI-Test/` holds MSTest coverage for text matching, dialogue prediction, and security primitives.

For contributor expectations and PR rules, see [`CONTRIBUTING.md`](./CONTRIBUTING.md).

## Contributing

Bug reports, translation additions, and focused PRs are welcome. Match the surrounding style, test on Windows with a real game, and attach screenshots for UI changes. Contributions land under the same dual licence (inbound = outbound).

Security issues: see [`SECURITY.md`](./SECURITY.md). Please don't open public issues for anything exploitable; the disclosure address is in that file.

## Licence and trademarks

- Code: dual-licensed under [AGPL-3.0](./LICENSE-AGPL) and a [commercial option](./LICENSE-COMMERCIAL). The top-level [`LICENSE`](./LICENSE) explains which option applies to which use.
- Trademarks: "Kaption" and the Kaption logo are trademarks of the project author. The source licence does not grant trademark rights. If you fork and redistribute, ship under a different name and icon. See [`TRADEMARKS.md`](./TRADEMARKS.md).
- Bundled third-party software: see [`THIRD_PARTY_LICENSES.txt`](./THIRD_PARTY_LICENSES.txt).

## Acknowledgements

Built on PaddleOCR, ONNX Runtime, Velopack, the Sentry SDK, and OpenCV. Hoyoverse owns the game text we match against; translation packs are licensed compilations published for personal in-game use.

---

Made at [kaption.one](https://kaption.one).
