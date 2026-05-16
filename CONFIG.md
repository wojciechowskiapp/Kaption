# Kaption desktop configuration reference

Kaption reads its settings from `%APPDATA%\Kaption\Config.json`. The file is created on first run and is safe to hand-edit when the app is closed. Every key has a default that matches the standard install, so the file usually only holds the keys you have changed.

You do not need this document to use Kaption. It exists so forks and self-hosted deployments can redirect the client to their own infrastructure, and so support can point users at specific knobs.

## Infrastructure overrides

These four keys are the ones you want if you are running a private fork and need the client to stop talking to `api.kaption.one`.

| Key | Default | What it does |
|---|---|---|
| `ApiUrl` | `https://api.kaption.one` | Backend API host. Activation, licence heartbeat, translation-pack catalog, feedback, and referral endpoints all live here. |
| `AppUrl` | `https://kaption.one` | Web frontend for account pages, activation flow, and "open dashboard in browser" links. |
| `UpdateFeedUrl` | `https://files.kaption.one/releases/stable/` | Velopack update feed URL. The client polls `releases.stable.json` at this prefix. Point it at your own R2/S3/static host if you ship your own build. |
| `SentryDsn` | *baked-in GlitchTip DSN* | Crash-reporting ingestion URL (Sentry SDK, pointed at a Sentry or GlitchTip instance). Set to `""` to disable network crash sends entirely while still keeping local `[CRASH-REPORT:*]` log lines. |

Change these when the app is closed, then relaunch. No migration is needed — the next boot picks them up.

## Crash reporting

Opt-in, off by default. First-run EULA dialog asks once; the answer is stored so the dialog does not re-prompt on every update.

| Key | Default | What it does |
|---|---|---|
| `CrashReportingEnabled` | `false` | Master switch. When `false` the crash hook still runs locally (writes `crash.log`) but never reaches the network. |
| `CrashReportingPromptShown` | `false` | One-shot flag set to `true` after the opt-in dialog has been answered, so it cannot appear twice. |
| `CrashReportingPromptShownAtUnix` | `0` | Unix-seconds timestamp of the opt-in decision, for audit correlation. |
| `SentryEnvironment` | `"production"` | Release-channel tag attached to crash events. Override to `"beta"` / `"staging"` if you fork into a channel. |

## OCR and translation

The defaults here are tuned per game via `GameRegionProfile` and `GameOcrTuning`, so override them only if you have specific hardware quirks or custom text.

| Key | Default | What it does |
|---|---|---|
| `Game` | `"Genshin"` | Active game profile. Set to `"StarRail"` for HSR tuning. |
| `Input` | `"EN"` | OCR source language. Only `EN` and `JP` have PaddleOCR recognizers shipped. |
| `Output` | `"PL"` | Primary translation language displayed on the overlay. |
| `Output2` | `""` | Secondary translation language for dual-display mode. Empty means off. |
| `ShowSecondLang` | `false` | Render the secondary language alongside the primary. |
| `OcrInterval` | `100` | OCR polling interval in milliseconds. Lower = faster reaction, higher CPU. Per-game profiles can override: Genshin 100 ms, HSR 60 ms. |
| `StabilityWindow` | `4` (or per-game) | Number of consecutive near-identical frames required before OCR triggers. Higher = fewer false triggers during typewriter text animation. |
| `UseSymSpell` | `true` | SymSpell fast-path for spelling-error correction. Off degrades to pure Levenshtein. |
| `OcrWeightedDistance` | `true` | OCR-confusion-weighted edit distance (e.g. `l`↔`1` cost = 0.1). Off degrades to unweighted Levenshtein. |
| `UseGpuOcr` | `true` | Route PaddleOCR through ONNX Runtime DirectML on the GPU. Falls back to CPU automatically if initialisation fails. |

## Overlay and layout

| Key | Default | What it does |
|---|---|---|
| `Region` | `"763,1797,2226,110"` | Primary dialogue region in `x,y,width,height` screen pixels. Set via in-app region picker. |
| `Region2` | `""` | Secondary region for dual-box layouts. Empty = single region. |
| `AnswerRegion` | `""` | Answer/choice prompt region for quest-answer translation. Empty = feature off. |
| `Size` | `22` | Subtitle font size in pixels. |
| `FontFamily` | `"Segoe UI"` | Font typeface. Must be installed on the system. |
| `SubtitleBgOpacity` | `176` | Overlay background opacity, 0–255. |
| `MaxOverlayHeight` | `0` | Maximum overlay height in pixels. `0` = unconstrained. |
| `MaxOverlayWidth` | `900` | Maximum overlay width in pixels. |
| `AutoShrinkText` | `true` | Reduce font size automatically if text would overflow the max bounds. |
| `Pad` | `[-175, 0]` | `[vertical, horizontal]` pixel offset applied when anchoring the overlay to the dialogue region. Negative vertical puts the overlay above the game text. |
| `PlayerName` | `""` | Custom "Traveler" replacement inserted into dialogue templates. |

## Overlay cards

| Key | Default | What it does |
|---|---|---|
| `ShowQuestBanner` | `true` | Render the quest-title card above dialogue. |
| `ShowNpcInfo` | `false` | Render an NPC info card. Off by default — marked low-value in user feedback. |
| `EnableAnswerTranslation` | `false` | Turn on the Answer region translation overlay. Requires `AnswerRegion`. |
| `PredictivePreDisplay` | `false` | Pre-render the next likely dialogue line before it is recognised. Off by default — gated for stability. |

## Startup and application

| Key | Default | What it does |
|---|---|---|
| `UILang` | OS locale (`en-US` fallback) | UI language. Only `en-US` and `pl-PL` are actively maintained; `zh-CN` and `ja-JP` exist as legacy-compatibility fallbacks. |
| `AutoStart` | `false` | Launch Kaption with Windows. Sets a registry run-key entry. |
| `SetupCompleted` | `false` → `true` | One-shot flag; `true` once the first-run wizard has been completed. |
| `ReferralCodeAttributedOrSkipped` | `false` | One-shot flag so the referral-code prompt does not reappear. |
| `UiRefreshInterval` | `200` | UI timer tick interval in milliseconds (separate from OCR interval). |

## Licence and update

| Key | Default | What it does |
|---|---|---|
| `HeartbeatHours` | `1` | How often the licence service pings the backend to refresh the effective tier. |
| `UpdateSkippedVersion` | `null` | Last version the user clicked "Skip" on. Used to avoid re-nagging within 24 hours. |
| `UpdateSkippedAtUnix` | `0` | Unix-seconds timestamp of the last skip. |

## Developer and testing flags

Do not enable these in user-facing builds. On every boot when `DevSkipGameGate` is `true`, Kaption writes a prominent `Logger.Log.Warn` line so the flag cannot hide in a rebrand fork.

| Key | Default | What it does |
|---|---|---|
| `Debug` | `false` | Enables additional in-app debug outputs and more verbose Logs-tab content. |
| `LogLevel` | `null` | Root log4net level override. `DEBUG` / `INFO` / `WARN` / `ERROR` / `FATAL` / `OFF`. Unset means use the bundled `app.config` default. |
| `DevSkipGameGate` | `false` | Skips the "target game must be running" gate on OCR-start paths. Testing only. Logs a warn on boot when `true`. |
| `SubtitleDebugMode` | `false` | Stops the video-OCR pipeline after the first fast scan so timings can be measured. |
| `SubtitleDetectionFps` | `5` | Frame sampling rate for the video-subtitle exporter. |
| `SubtitleMinDurationMs` | `200` | Minimum subtitle duration for the exporter to emit a cue. |

## Internal / legacy

These are still read by code paths that are either internal bookkeeping or planned for removal.

| Key | Default | Why it exists |
|---|---|---|
| `Token` | `"ENGI"` | Legacy translation-service token. Not part of the Kaption backend. |
| `Server` | `"https://mp3.2langs.com/download"` | Legacy translation-service endpoint. Not part of the Kaption backend. |
| `Distant` | `3` | Legacy UI spacing parameter. Pending cleanup. |
| `Update` | *(unread)* | Deprecated pre-Velopack update manifest URL. Scheduled for removal. |
| `ConfigMigrationVersion` | *tracked automatically* | Pointer updated by `ConfigMigrations.RunAll()`. Do not edit by hand. |

## How to override a key

Edit `%APPDATA%\Kaption\Config.json` while Kaption is closed:

```json
{
  "ApiUrl": "https://api.fork.example.com",
  "UpdateFeedUrl": "https://cdn.fork.example.com/releases/",
  "SentryDsn": "",
  "Game": "StarRail"
}
```

Relaunch. The Logs tab in Settings shows the effective values on startup so you can confirm the override took effect.

## What this document is not

This is not an API reference for the backend. Forking the client is easy; running the backend is not — see `.plan/research/SOURCE-AVAILABLE-STRATEGY.md` for the design of `POST /api/app/file-protection-key` and the per-device key scheme that replaces the currently embedded encryption material.
