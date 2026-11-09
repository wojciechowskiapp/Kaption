# Contributing to Kaption

Thanks for helping. This page covers building Kaption, finding your way around the code and sending a change that can be merged.

By opening a pull request you agree that your contribution is licensed under both of Kaption's licences, AGPL-3.0 and the commercial one (see [LICENSE](../LICENSE)). There is no CLA to sign.

This repository is the Windows desktop app. The backend, the website and the translation pipeline are separate and closed, so a change that needs them is welcome as an issue but cannot be merged on its own.

## Prerequisites

- Windows 10 or 11, 64-bit
- [.NET 10 SDK](https://dotnet.microsoft.com/download) 10.0.403 or newer (`global.json` pins the feature band)

NuGet restores everything else, ONNX Runtime and DirectML included.

## Build and test

```
dotnet build GI-Subtitles.sln -c Release
dotnet build GI-Subtitles/GI-Subtitles.csproj -c Debug
dotnet test GI-Test/GI-Test.csproj -c Debug
```

The suite must end with 0 failed. A few tests show as skipped when data they need is not on your machine, such as recorded game text or video samples; that is expected. The build also checks every XAML resource key, so a typo fails the build instead of crashing a window later.

The self-contained build, the same shape the installer ships:

```
dotnet publish GI-Subtitles/GI-Subtitles.csproj -c Release -r win-x64 --self-contained true
```

## Run the app with a test profile

Settings normally live in `%APPDATA%\Kaption\Config.json`. To start with fresh settings and leave yours alone, point `KAPTION_CONFIG_DIR` at an empty folder before you launch a Debug build:

```
set KAPTION_CONFIG_DIR=%TEMP%\kaption-profile
GI-Subtitles\bin\Debug\Kaption.exe
```

With no saved settings you get the first-run setup window. Your sign-in, the subtitle packs and `app.log` still come from `%APPDATA%\Kaption`.

## Where things live

| Path | What's in it |
|---|---|
| `GI-Subtitles/` | The WPF app: `Views/` for windows and pages, `Services/` for OCR, matching, capture, network, security and characters, `Themes/` and `Resources/` for palettes, styles and the `Strings.*.xaml` dictionaries |
| `PaddleOCRSharp/` | ONNX Runtime wrapper around PaddleOCR |
| `Screenshot/` | Screen capture and region selection |
| `GI-Test/` | MSTest suite |

Each game is one entry in `Services/Detection/GameRegionProfile.cs`: process name, dialogue area, pacing and how speaker names are found. New per-game behaviour goes there as a flag on the profile, not as `if (game == ...)`.

## Rules that matter

- **Two string files.** Every new UI string goes into `Strings.en-US.xaml` and `Strings.pl-PL.xaml`, and a test checks that their keys match. The app ships English and Polish only.
- **No em dashes** in the string files. A test fails on them; use a comma, a colon or a full stop.
- **Tests must not depend on your machine.** They run on a clean Windows runner with no Kaption install and no sign-in on disk. If your change touches start-up, sign-in or file protection, run the tests once with `%APPDATA%\Kaption\activation.dat` renamed, then rename it back. Use `TestProtection` for a fake activation, and never let a test write to the real profile.
- Match the surrounding code: the newest C# on .NET 10, four-space indents, comments that say why. Window code-behind stays next to its XAML.

## Issues

A useful bug report has the Kaption version (the chip at the bottom left, or the first line of `%APPDATA%\Kaption\app.log`), your Windows version, graphics card and game version, what you expected, what happened instead, and the last 200 lines of `app.log`. For a feature, start with what you want to do in the game.

Found a security problem? Please don't open an issue; follow [SECURITY.md](./SECURITY.md).

## Pull requests

Branch off `main`, keep the change focused, build and run the tests, and add before and after screenshots for anything visible. Short commit subjects with the area first read best, for example `Characters: keep the reading position on redraw`.
