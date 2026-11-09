# Kaption desktop configuration reference

Kaption keeps its settings in `%APPDATA%\Kaption\Config.json`. The file is created on first run and is safe to edit while the app is closed. Every key has a default that matches a normal install, so the file usually holds only what you changed.

You don't need this page to use Kaption. It is here for forks that point the app at their own servers, and so support can name a specific setting. Page names below are the app's Polish ones, with the English label in brackets the first time.

## Infrastructure overrides

The keys a private fork needs to stop talking to `api.kaption.one`.

| Key | Default | What it does |
|---|---|---|
| `ApiUrl` | `https://api.kaption.one` | Backend API host: activation, the hourly licence check, the subtitle pack catalog, feedback and referrals. |
| `AppUrl` | `https://kaption.one` | Website for account pages, sign-in and "open in browser" links. |
| `UpdateFeedUrl` | `https://files.kaption.one/releases/stable/` | Velopack update feed. The app polls `releases.stable.json` under this prefix. |
| `SentryDsn` | empty in the public source | Crash-report endpoint (Sentry SDK, a Sentry or GlitchTip server). Empty means crashes are only written to the local log. The official installer carries its own value. |

Change them while the app is closed; the next start picks them up.

## Crash reporting

Off by default. The setup window asks once; afterwards the switch is in Pomoc › Prywatność (Help › Privacy).

| Key | Default | What it does |
|---|---|---|
| `CrashReportingEnabled` | `false` | Master switch. When `false`, crashes still go to `app.log` but nothing is sent. |
| `CrashReportingPromptShown` | `false` | Set once the question has been answered, so it never comes back. |
| `CrashReportingPromptShownAtUnix` | `0` | When the answer was given, in Unix seconds. |
| `SentryEnvironment` | `"production"` | Channel tag on crash events, for example `"beta"` in a fork. |

## Auto-start (2.5)

| Key | Default | What it does |
|---|---|---|
| `AutoStartTranslation` | `true` | Start translating by itself when the game is on screen, and stop when the game closes. The switch "Tłumacz sam, gdy gra jest na ekranie" ("Translate by itself when the game is on screen") is on Gra i napisy (Game and subtitles). The setup window and the "Nowość w 2.5" card on Start write the same key. |
| `AutoStartRequireFocus` | `true` | Only start while the game window is active. Turn it off under Gra i napisy › Zaawansowane (Advanced) if you play in a window next to other apps. |

Auto-start checks the same things as the Start button before it starts. If you stop translating yourself, it waits until you start again.

## Look and navigation (2.5)

| Key | Default | What it does |
|---|---|---|
| `Appearance.Theme` | `"Auto"` | `"Auto"` follows the Windows app theme, `"Dark"` and `"Light"` pin one. Set it under Wygląd napisów › Wygląd aplikacji (Subtitle style › Kaption's look). Windows high contrast always wins. |
| `Ui.AdvancedMode` | `false` | Pomoc › Tryb zaawansowany (Help › Advanced mode): opens every Zaawansowane section and shows technical details on Start and in the status bar. |
| `Ui.Intro25Seen` | `false` | Set when the "Nowość w 2.5" card on Start is dismissed with "Rozumiem" ("Got it"). New installs never see that card. |
| `Ui.WhatsNewSeen` | none | The subtitle update whose "Co nowego" ("What's new") card was dismissed on Start. |

## Build card (2.5.1)

"Karta buildu" ("Build card") is the tile in Skróty (Shortcuts) on Start and the button in the Postacie (Characters) header. It opens the build card maker on kaption.one with your UID. The first time, Kaption reads the UID from the corner of the game screen and keeps it here, so the next click opens straight away. Start › Zaawansowane › Zapomnij UID ("Forget UID") clears all three keys.

| Key | Default | What it does |
|---|---|---|
| `BuildCard.Uid.Genshin` | none | Written by Kaption: the last UID read from the Genshin Impact screen. |
| `BuildCard.Uid.StarRail` | none | The same for Honkai: Star Rail. |
| `BuildCard.Uid.ZZZ` | none | The same for Zenless Zone Zero. |

Only 9 or 10 digits without a leading zero count; anything else in these keys is ignored. The UID leaves your PC only in the address the browser opens (`kaption.one/pl/moj-build/?game=genshin&uid=...`, or `/my-build/` with an English interface). The log never names it, and diagnostic bundles mask it.

## Farm today (2.5.5)

"Co dziś farmić" ("What to farm today") is a card over Genshin Impact: today's talent book series and, with a UID, which of your showcase characters can level a talent with them. It shows by itself the first time Genshin is in front in a new server day (after the 04:00 reset), and on demand from the tray menu and Postacie (Characters) › Co dziś farmić. Only Genshin rotates its domains by weekday, so the other games have no card.

| Key | Default | What it does |
|---|---|---|
| `FarmCardEnabled` | `true` | Shows the card when Genshin starts. "Nie pokazuj" ("Don't show") on the card sets it to `false`; the switch under Postacie › Co dziś farmić › Ustawienia turns it back on. |
| `ShowcaseUid_Genshin` | none | The UID you saved under Postacie › Co dziś farmić › Ustawienia. Kaption reads that UID's public showcase through `api.kaption.one/api/cards/genshin/<uid>` at most once per server day. "Usuń UID i dane" ("Remove UID and data") clears it and the cached showcase. |
| `FarmCardRegion` | `"europe"` | Your server, for the reset hour: `"europe"`, `"america"` or `"asia"`. |
| `FarmCardLastShownDay` | none | Written by Kaption: the server day the card last showed by itself. |

The domain schedule comes from `kaption.one/data/today/genshin.json` and is kept in `%APPDATA%\Kaption\Genshin\today.json`; the showcase is kept, trimmed to names, levels and talent levels, in `showcase.json` next to it. A UID the build card read from the game screen is offered in the field but saved only when you click Save. The log never names the UID, and diagnostic bundles mask it.

## Guess game and Start notices

Kaption's guess game lives on kaption.one (Gendle, Stardle, Zendle, Wuwdle). The app can tell you, at most once a day, when someone passes you in this month's ranked leaderboard. It reads your place from `api.kaption.one/api/app/guess/standing` about 2 minutes after start and then every 4 hours (every 12 hours while you have played nothing this month; after an error it tries again in 15 minutes, then an hour, then 4 hours), and only while you are signed in. The server can change the two read intervals (`poll` in its answer); a key below pins a value on this PC instead. A drop on the selected game's board or on the overall board becomes one tray notice, never while subtitles run, while a game is in front, or while Windows is in a full-screen app, presentation mode or quiet time; a waiting notice is checked again every 60 seconds. A drop is measured from the newer of two places: the one Kaption last saw, and your place right after your last puzzle that scored, which the server works out from the month's results. So a pass made while Kaption was closed still becomes a notice the next time it runs. Notices count only once you have points this month, and only when the player now just ahead of you has more points than you (a tie-break at equal points is no notice). Start repeats the notice in a strip with "Zobacz swoje miejsce" ("See your place") and "Wyłącz" ("Turn off"); the switch is in Pomoc › Społeczność (Help › Community). If the server turns the request down, Kaption only notes it in the log: your sign-in is never touched. Every read leaves one line in `app.log` starting `Guess standing`: each watched board's place, what it was measured from, the notice it found (if any) and when the next read is.

| Key | Default | What it does |
|---|---|---|
| `GuessNotify.Enabled` | `true` | Off stops the reads and the notices. The switch "Daj znać, gdy ktoś mnie wyprzedzi w rankingu" ("Tell me when someone passes me in the ranking") and "Wyłącz" on the Start strip write it. |
| `GuessNotify.LastShownDay` | none | Written by Kaption: the local day (`yyyy-MM-dd`) the last notice reached you, as a tray notice or on the Start strip. |
| `GuessNotify.Baseline` | none | Written by Kaption: your place per board the last time it was accepted, as JSON (`{"global":{"period":"2026-11","rank":3,"points":140,"at":"2026-11-08T23:53:55.000Z"}}`; `at` is the server's time of that place, missing in files from 2.5.7 and 2.5.8). A drop is measured from here, or from your place after your last scoring puzzle when that is newer; a new month starts again. |
| `GuessNotify.FirstDelaySeconds` | `120` | Seconds after start before the first read (10 to 3600). |
| `GuessNotify.PollMinutes` | the server's, else `240` | Minutes between reads once you have played this month (15 to 1440). |
| `GuessNotify.IdlePollMinutes` | the server's, else `720` | Minutes between reads while you have played nothing this month (15 to 2880). |
| `GuessNotify.NoSessionRecheckMinutes` | `15` | Minutes between checks for a sign-in while signed out; nothing is sent (1 to 240). |
| `GuessNotify.RetryMinutes` | `[15, 60, 240]` | Waits after the 1st, 2nd and later failures in a row (each 1 to 1440, up to 8 steps; the last repeats). |
| `GuessNotify.GateRetrySeconds` | `60` | Seconds between checks while a notice waits for subtitles to stop, the game to leave the front or Windows to accept notices (10 to 900). |
| `GuessNotify.StandingUrl` | none | Debug builds only: a test server's address used instead of the standing endpoint, called without your session. Local `http` or any `https` address. Release builds ignore it. Never point `ApiUrl` at a test server for this. |
| `Ui.Seen.WuWaPolish` | `false` | The "Nowość w Kaption" ("New in Kaption") card's Wuthering Waves row was closed or used. The setup sets it for new installs. The row shows only while the server lists the Polish WuWa pack. |
| `Ui.Seen.GuessGame` | `false` | The same card's guess game row was closed or used. |
| `Ui.Spotlight.Shows.WuWaPolish`, `Ui.Spotlight.Shows.GuessGame` | `0` | Written by Kaption: launches on which each row was on screen. A row retires after 3. |
| `Ui.DismissedAnnouncements` | none | Ids of the announcements you closed on Start (newest last, at most 50), so a closed one never comes back. |

## OCR and translation

The per-game defaults come from `GameRegionProfile`. Override them only for specific hardware or custom text.

| Key | Default | What it does |
|---|---|---|
| `Game` | `"Genshin"` | Active game: `"Genshin"`, `"StarRail"` or `"ZZZ"`. Also names the per-game data folder (`%APPDATA%\Kaption\ZZZ\` and so on), created with exactly this spelling. |
| `Input` | `"EN"` | Language the game text is read in. |
| `Output` | `"PL"` | Language shown in the overlay. |
| `Output2` | `""` | Second language for dual display. Empty means off. |
| `ShowSecondLang` | `false` | Show the second language under the first. |
| `OcrInterval` | per game | Milliseconds between reads, clamped to 50 to 1000. Genshin 100, Honkai: Star Rail 60, Zenless Zone Zero 60. |
| `StabilityWindow` | per game | Frames that must match before a read counts. Genshin 5, Honkai: Star Rail 2, Zenless Zone Zero 2. |
| `UseSymSpell` | `true` | Fast spelling-correction step in the matcher. |
| `OcrWeightedDistance` | `true` | Cheaper edit distance for common misreads such as `l` and `1`. |
| `OcrModelProfile` | `"ppocr-v6-small"` | Text recognition model: the recommended `"ppocr-v6-small"` or the older `"ppocr-v4-mobile"`. Pomoc › Zaawansowane › Model rozpoznawania tekstu (Text recognition model). |
| `UseGpuOcr` | `true` | Read on the graphics card through DirectML, with an automatic fallback to the processor. The checkbox sits in the same card; ticking it also gives the graphics card a fresh try. |
| `OcrGpuStrikes` | none | Written by Kaption: slow graphics-card starts counted in a row. Two in a row set the quarantine below. |
| `OcrGpuStrikesVersion` | none | App version the strikes belong to. |
| `OcrGpuQuarantine` | none | Written by Kaption: `true` means the graphics card was too slow or unstable here, so reading runs on the processor. Cleared by ticking the GPU checkbox, and ignored after an app update. |
| `OcrGpuQuarantineVersion` | none | App version that set the quarantine; it applies only while this matches the running build. |
| `OcrGpuQuarantineReason` | none | Why, in words, also written to `app.log`. |
| `OcrGpuQuarantineUtc` | none | When the quarantine started, ISO 8601 in UTC. |

## Quick translate (Ctrl+Q)

| Key | Default | What it does |
|---|---|---|
| `QuickTranslateCloseMode` | `"Auto"` | How the popup closes: `"Auto"` after a reading time based on the text, `"Timed"` after a fixed time, `"Manual"` only when you close it. |
| `QuickTranslateSeconds` | `8` | Seconds for `"Timed"`, 2 to 120. |
| `QuickTranslateFontSize` | `16` | Popup text size, 12 to 28. |
| `QuickTranslateLastArea` | none | Written by Kaption: the last selected area, reused by Ctrl+Shift+Q. |

## Overlay and layout

Wygląd napisów (Subtitle style) sets these with a true-scale preview. Its three styles write a set of values at once: Klasyczny (Classic) is size 22, panel 176 and outline 1; Kontrast (Contrast) is size 22, panel 230 and outline 2; Kino (Cinema) is size 26, panel 179, outline 1 and letter spacing 0.03, placed low and centred above the dialogue. Changing a value afterwards makes the style custom. The exact numbers sit under Wygląd napisów › Zaawansowane.

| Key | Default | What it does |
|---|---|---|
| `Region` | `"763,1797,2226,110"` | Dialogue area as `x,y,width,height` in screen pixels. Set it with the area picker on Gra i napisy, or let the setup window find it. |
| `Region2` | `""` | Second area for two-box layouts. Empty means one area. |
| `AnswerRegion` | `""` | Area of the answer choices, for translating them. Empty means off. |
| `Size` | `22` | Subtitle size, 10 to 36. |
| `Overlay.Font` | `"Source Sans 3"` | Subtitle typeface. Source Sans 3 and Inter Tight come with the app; any installed font name works too. |
| `FontFamily` | none | The font key of 2.4 and 2.5. Read only when `Overlay.Font` is missing, and `"Segoe UI"` there means the old default, so it becomes Source Sans 3. |
| `Overlay.Outline` | `1.0` | Outline thickness around the letters, 0 to 4. |
| `Overlay.LetterSpacing` | `0` | Extra letter spacing in em, -0.05 to 0.12. |
| `Overlay.AutoDarken` | `true` | Darken the panel when the scene behind the subtitle is bright, like snow or sky. |
| `Overlay.PadBeforeCinema` | none | Written by Kaption: your position before you picked Kino, put back when you leave it. |
| `SubtitleBgOpacity` | `176` | Panel opacity, 0 to 255. |
| `MaxOverlayHeight` | `0` | Maximum overlay height in pixels; `0` means no limit. |
| `MaxOverlayWidth` | `900` | Maximum overlay width in pixels. |
| `AutoShrinkText` | `true` | Make the text smaller when a line would need more than two rows. |
| `Pad` | per game | `[vertical, horizontal]` offset from the dialogue area. Negative vertical puts the overlay above the game text: -140 for Genshin and Honkai: Star Rail, -180 for Zenless Zone Zero, whose lines often wrap to four or five rows. |
| `PlayerName` | `""` | Your name, used where the dialogue addresses the Traveler. |

## Overlay cards

| Key | Default | What it does |
|---|---|---|
| `ShowQuestBanner` | `true` | Show the quest title above the dialogue. |
| `ShowNpcInfo` | `false` | Show an info card for the speaking character. |
| `EnableAnswerTranslation` | `false` | Translate the answer choices. Needs `AnswerRegion`. |
| `PredictivePreDisplay` | `false` | Show the likely next line before it is read. Off while it is being tested. |

## Startup and application

| Key | Default | What it does |
|---|---|---|
| `UILang` | Windows display language | App language: `"pl-PL"` or `"en-US"`. Japanese and Chinese still load for old settings but are no longer offered. |
| `AutoStart` | `false` | Start Kaption with Windows. Gra i napisy › Zaawansowane › Uruchamianie (Startup). |
| `SetupCompleted` | `false` | Set when the setup window has been finished. |
| `SetupDismissed` | `false` | Left by the setup wizard of 2.4 and earlier. Like `SetupCompleted`, it keeps the setup window from opening by itself. |
| `FullscreenTipAcknowledged` | `false` | Set once the Genshin display-mode tip has been read, in the setup window or on Start. |
| `ReferralCodeAttributedOrSkipped` | `false` | Keeps the referral-code question from coming back. |
| `UiRefreshInterval` | `150` | Interface timer in milliseconds, separate from the reading interval. |

## Licence and update

| Key | Default | What it does |
|---|---|---|
| `HeartbeatHours` | `1` | How often the app confirms the activation with the server. |
| `UpdateSkippedVersion` | none | The last version you chose to skip, so the update banner waits 24 hours. |
| `UpdateSkippedAtUnix` | `0` | When it was skipped, in Unix seconds. |

## Developer and testing flags

Don't turn these on in builds for players. While `DevSkipGameGate` is `true`, every start writes a warning to the log so the flag cannot hide in a fork.

| Key | Default | What it does |
|---|---|---|
| `Debug` | `false` | Extra debug output in the app and the log. |
| `LogLevel` | none | Log level override: `DEBUG`, `INFO`, `WARN`, `ERROR`, `FATAL` or `OFF`. |
| `DevSkipGameGate` | `false` | Skip the "the game must be running" check when starting. Testing only. |
| `SubtitleDebugMode` | `false` | Stop the video subtitle exporter after the first quick pass, to measure timings. |
| `SubtitleDetectionFps` | `5` | Frames per second the video subtitle exporter samples. |
| `SubtitleMinDurationMs` | `200` | Shortest subtitle the exporter writes, in milliseconds. |

For tests there is also the `KAPTION_CONFIG_DIR` environment variable: it moves `Config.json` to another folder (see [CONTRIBUTING](../.github/CONTRIBUTING.md)).

## Internal and legacy

Still read by old code paths, or kept as bookkeeping.

| Key | Default | Why it exists |
|---|---|---|
| `Token` | `"ENGI"` | Token of an old translation service. Not part of the Kaption backend. |
| `Server` | `"https://mp3.2langs.com/download"` | Endpoint of the same old service. Not part of the Kaption backend. |
| `Distant` | `3` | Old spacing value, waiting for clean-up. |
| `Update` | unread | Update address from before Velopack, to be removed. |
| `ConfigMigrationVersion` | set by the app | Which settings migrations have run. Don't edit it by hand. |

## How to override a key

Close Kaption, edit `%APPDATA%\Kaption\Config.json`, then start it again:

```json
{
  "ApiUrl": "https://api.fork.example.com",
  "UpdateFeedUrl": "https://cdn.fork.example.com/releases/",
  "SentryDsn": "",
  "Game": "StarRail"
}
```

The log in Pomoc › Dziennik (Help › Log) shows the values in effect at start-up, so you can check that the change took.

## What this page is not

It is not a backend API reference. Forking the app is easy, running the backend is not: the app expects a `POST /api/app/file-protection-key` endpoint that issues a per-device secret, plus the licensing and subtitle-pack endpoints described in [SECURITY.md](../.github/SECURITY.md). If you need a fully self-hosted setup, open an issue and we will help.
