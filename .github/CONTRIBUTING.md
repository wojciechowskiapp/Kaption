# Contributing to Kaption

Kaption is a small, opinionated project. Contributions are welcome. The
goal of this file is to save you from writing code we would bounce on
review.

## Before you start

- **Licence.** Kaption is dual-licensed under AGPL-3.0 and a commercial
  licence (see [`../LICENSE`](../LICENSE)). **By opening a pull request
  you agree that your contribution is licensed under both options
  (inbound = outbound).** This means we can keep offering Kaption under
  the same dual licence without coming back to ask permission later. We
  do not require a signed CLA — opening the PR is the agreement.
- **Scope.** This repository is the Kaption **desktop app**. The
  backend (`api.kaption.one`), landing site (`kaption.one`), translation
  pipeline, and release / deploy scripts are separate, closed-source
  services. Issues or features that need backend changes are fine to
  discuss, but PRs that require coordinated backend or pipeline changes
  cannot be merged without our involvement on the other side.
- **Trademarks.** "Kaption" and the icon are trademarks of the project
  author. Source rights are not trademark rights. If you fork, give your
  fork a different name. See [`../docs/TRADEMARKS.md`](../docs/TRADEMARKS.md).

## Project layout

- `GI-Subtitles/` — the WPF desktop app, `net10.0-windows`. UI in
  `Views/`, models in `Models/`, services grouped by area in `Services/`
  (OCR, Translation, Rendering, Security, Capture, Network, Update,
  Observability, Data, Detection, Video).
- `PaddleOCRSharp/` — ONNX-Runtime wrapper around PaddleOCR.
- `Screenshot/` — screen capture and region-selection UI.
- `GI-Test/` — MSTest coverage for matching, OCR primitives, dialogue
  prediction, and the security stack.

The folder names retain the historical `GI-Subtitles` prefix as a
project-structure detail; the product is **Kaption**.

## What we are happy to accept

- Bug fixes, with a reproducible scenario in the PR description.
- Per-game OCR tuning tweaks — they land in `GameRegionProfile` /
  `GameOcrTuning` without touching hot paths.
- New locales for the UI — add keys to
  `GI-Subtitles/Resources/Strings.en-US.xaml` first, then create or
  update the matching `Strings.<locale>.xaml`. Only English and Polish
  are actively maintained; new locales are welcome.
- Translation-matcher improvements, documented with a benchmark before
  and after.
- Documentation fixes, broken-link fixes, typo fixes.

## What we are unlikely to accept without discussion first

- Refactors of `MainWindow.xaml.cs` or `SettingsWindow.xaml.cs`. These
  are large code-behind files by deliberate choice — all OCR-loop state
  is in one place. "Clean-up" PRs that extract ViewModels tend to
  reorder timer code in subtle ways. Open an issue first.
- Swapping a core dependency (PaddleOCR, ONNX Runtime, Velopack). The
  build toolchain is sensitive and the licensing audit is non-trivial.
- Features that rely on the desktop having unauthenticated internet
  access to third-party services. Kaption is intentionally conservative
  about what it phones home to.

## Build and test

Prerequisite: .NET 10 SDK 10.0.203 or newer. `global.json` pins the
feature band. `dotnet --version` from the repo root should print
`10.0.203` or a matching feature-band patch.

```
dotnet build GI-Subtitles.sln -c Release
dotnet test  GI-Test/GI-Test.csproj -c Debug
```

Expected on a clean checkout: 188 pass, 2 pre-existing data-dependent
fails (`DialoguePredictionTests`), 5 external-data skips.

For a self-contained Release build that bundles the .NET 10 Desktop
runtime (the shape end users get from the official installer):

```
dotnet publish GI-Subtitles/GI-Subtitles.csproj ^
    -c Release -r win-x64 --self-contained true
```

End-user installers and auto-update artefacts are produced by a private
release pipeline; running the published `Kaption.exe` from the build
above is enough to verify a source build locally.

## Opening an issue

A good bug report contains, in any order:

- Kaption version (Settings → About, or the first line of
  `%APPDATA%\Kaption\app.log`).
- Windows version + GPU + game version.
- What you expected to happen.
- What actually happened, with the **exact** error message or
  screenshot.
- The relevant slice of `%APPDATA%\Kaption\app.log` — the last 200
  lines is usually plenty.

For feature requests, lead with the workflow you want to enable.
"As a player in X, I want Y, so that Z" is a useful template.

## Opening a pull request

1. Fork. Create a branch off `main` with a short, kebab-case name
   (`fix-dashboard-badge`, `hsr-tuning-v33`).
2. Build the solution and run the test suite (commands above).
3. Verify user-visible changes manually. WPF overlay behaviour is
   genuinely hard to unit-test. If you changed layout code, attach
   before / after screenshots to the PR.
4. Keep commits focused. Prefer short imperative subjects with a scope
   first:
   - `Dashboard: fix disabled-state badge not clearing`
   - `HSR: bump stability window for typewriter dialogue`
5. Open the PR against `main`. Fill out the description — one
   paragraph on the change, one on verification steps. Screenshots for
   any UI change. Call out any deploy or backend impact.

## Code style

There is no linter configured. Match the surrounding style, plus:

- C# 12, `net10.0-windows`. Modern features (records, target-typed
  `new`, file-scoped namespaces) are fine.
- 4-space indentation. PascalCase for types/methods/public members,
  camelCase for locals, `_camelCase` for private fields.
- WPF code-behind paired with the `.xaml`. Do not extract a ViewModel
  unless that is the point of the PR.
- User-facing strings go in `Resources/Strings.<locale>.xaml`, accessed
  via `{DynamicResource KeyName}` or `L("KeyName", "Fallback")`.
- No emojis in code or logs.
- Comments should explain **why**, not **what**. If a comment restates
  the code, delete it.

## Configuration and secrets

Do not commit secrets or local environment files. Desktop config is
stored per-user under `%APPDATA%\Kaption\Config.json`.
`GI-Subtitles/Properties/VersionInfo.cs` is rewritten by the release
pipeline, so treat incidental diffs there carefully — check before
committing.

## Translation packs

The shipped translation packs (Genshin Impact PL, HSR PL, etc.) are
produced by a private translation pipeline and shipped from our
backend. If you want to contribute a translation for a language we do
not yet ship, open an issue first — there is significant infrastructure
on our side (LLM keys, multi-hour runs, publishing scripts) and we want
to coordinate before you spend time.

## Security

If you think you have found a security issue, **do not open a public
issue or PR**. Follow [`SECURITY.md`](./SECURITY.md) — email or file a
GitHub private advisory.

## Questions

- Build errors on Windows → open an issue with the full `dotnet build`
  output.
- Anything else → open a discussion thread, or just mention it in the
  PR.

We try to respond within a few days. If a PR has been sitting for more
than a week with no response, nudge us.
