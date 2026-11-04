# Game manifests

One file per game, `games/<id>.json`, checked against `game.schema.json`. The file name is the wire id.

Who reads them:

- **Desktop**: embedded into Kaption.exe at build time (`GI-Subtitles.csproj`). `Services/Games/GameRegistry`
  builds the per-game profiles (detection, regions, pacing, vision), the game pickers, the dialogue strategy and the
  colours from them. `beta` and `live` games appear; `planned` and `withdrawn` don't.
- **Site**: `landing/src/data/games.ts` reads them during the build (`beta` and `live`), and the supported-games
  catalog lists `planned` games and `candidates.json` for the vote.
- **Backend**: `backend/src/generated/games.ts`, written by `node tools/games/generate.cjs`, serves the catalog
  and the vote (`/api/app/games`).
- **Updater**: `updater/games/<id>.toml` stays the pipeline manifest; a test checks that its display name and
  accent match.

After any change:

```bash
node tools/games/validate.cjs     # schema plus cross-checks
node tools/games/generate.cjs     # refresh the backend catalog
node --test tools/test-games.cjs
```

Rules that keep the three live games working: `id`, `gameId`, `site.hub`, `site.guideKey` and the guide paths never
change once a game ships. `id` is always `gameId` lower-cased. A key left out of `desktop.vision` or
`desktop.pacing` keeps the app's default. A candidate becomes a game by moving to `games/<key>.json` with the same
key as its id, which keeps its votes.

When the executable name is generic (Unreal's `Client-Win64-Shipping` is shared by many games), set
`"detect": { …, "requireTitle": true }`: the app then counts a process as the game only while one of its windows
has a title from `windowTitles`. Two manifests may share an executable only if both set it.
