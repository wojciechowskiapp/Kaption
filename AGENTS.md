# Repository Guidelines

> **Who this is for:** new contributors who want a one-page checklist.
> Read [`README.md`](./README.md) first for what Kaption is and how to
> install it. Read [`CONTRIBUTING.md`](./CONTRIBUTING.md) for the full
> pull-request flow. For security reports, [`SECURITY.md`](./SECURITY.md).
> For every runtime config key, [`CONFIG.md`](./CONFIG.md). For
> trademarks, [`TRADEMARKS.md`](./TRADEMARKS.md).

## Project layout

- `GI-Subtitles/` — main WPF desktop app, `net10.0-windows`. UI in
  `Views/`, models in `Models/`, services grouped by area in `Services/`
  (OCR, Translation, Rendering, Security, Capture, Network, Update,
  Observability, Data, Detection, Video).
- `PaddleOCRSharp/` — ONNX-Runtime wrapper around PaddleOCR.
- `Screenshot/` — screen capture and region-selection UI.
- `GI-Test/` — MSTest coverage for matching, OCR, and dialogue
  prediction.

The folder names retain the historical `GI-Subtitles` prefix as a
project-structure detail; the product is **Kaption**.

## Build, test, and development

**Prerequisite:** .NET 10 SDK 10.0.203 or newer. `global.json` pins the
feature band. `dotnet --version` from the repo root should print
`10.0.203` or a matching patch.

```bash
dotnet build GI-Subtitles.sln -c Release
dotnet test GI-Test/GI-Test.csproj -c Debug
```

Expected test result: 190 pass, 2 pre-existing data-dependent fails
(`DialoguePredictionTests`), 5 external-data skips.

For a self-contained Release build that bundles the .NET 10 Desktop
runtime (the same shape end users get from the official installer):

```bash
dotnet publish GI-Subtitles/GI-Subtitles.csproj -c Release -r win-x64 --self-contained true
```

End-user installers and auto-update artefacts are produced by a
private release pipeline; running the published `Kaption.exe` from
the build above is enough to verify a source build locally.

## Coding style and naming

- C# 12, target framework `net10.0-windows`.
- 4-space indentation. PascalCase for types/methods/properties,
  camelCase for locals, `_camelCase` for private fields.
- WPF code-behind paired with the `.xaml`. Do not extract a ViewModel
  unless the PR is specifically about that.
- Add new desktop UI strings to
  `GI-Subtitles/Resources/Strings.en-US.xaml`, and mirror them into
  `Strings.pl-PL.xaml`. The `zh-CN` and `ja-JP` resources are legacy
  compatibility and should not block a PR if a key is missing there.
- No linter is configured; match surrounding style.
- Comments explain **why**, not **what**.
- No emojis in code or logs.

## Testing

Desktop tests use MSTest in `GI-Test/`. Name new test files after the
feature under test and use `[TestMethod]` for cases. Some tests depend
on external fixtures and may report `Assert.Inconclusive`; keep pure
logic tests deterministic when you can.

## Commits and pull requests

Recent history favours short, imperative subjects with a scope first,
for example `Dashboard refactor` or `HSR: bump stability window for
typewriter dialogue`. Keep commits focused. Pull requests should
summarise affected modules, list verification steps, link issues when
relevant, and include screenshots for any WPF change.

By opening a PR you license your contribution under both the AGPL-3.0
and the commercial-licence option (inbound = outbound). See
[`CONTRIBUTING.md`](./CONTRIBUTING.md).

## Configuration and secrets

Do not commit secrets or local environment files. Desktop config is
stored per-user under `%APPDATA%\Kaption\Config.json`.
`GI-Subtitles/Properties/VersionInfo.cs` is rewritten by the release
pipeline, so treat incidental diffs there carefully — check before
committing.

## Licence

This repository is dual-licensed under AGPL-3.0 and a commercial
option. See [`LICENSE`](./LICENSE).
