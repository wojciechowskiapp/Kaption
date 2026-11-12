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

`site.guessName` (optional) is the daily guessing game's own name on the site: Gendle, Stardle, Zendle, Wuwdle. It
titles the game's guess hub ("Gendle – daily Genshin Impact character guessing game") and starts every shared result
("Gendle #12"). One capitalised word, unique across games; without it the hub keeps its plain title.

`stamina` (optional) is the game's stamina and how it refills: its English name, `max` (where natural refilling stops),
`regenSeconds` per point, `hold` (the most a player can hold through refills, where the data says) and `reserve` (a
second pool that fills only while the first is full). The site's stamina timers read it
(`/tools/stamina-timer/<hub>/`); a game without it gets no timer. Every value is checked against the game data and
`checked` says in which version and where (`version`, `ref`): change the numbers only together with that line.

`resets` (optional) is when the game's day and week turn over: `servers` (id, English name, fixed `utcOffset` in
whole hours, the default first), `dailyHour` and `weeklyDay` (0 Sunday to 6 Saturday), both in server time. The
desktop's Today card and its "once per game day" read it, and so does the site's today page, through
`landing/src/data/generated/resets.ts` (`node tools/games/generate.cjs`; a test fails when it is stale). Nothing else
may hard-code a reset. `checked` names the publisher's own page (`at`, `ref`) and says what could not be confirmed.
A game whose reset is not confirmed on its publisher's pages leaves the block out: it then shows no reset countdown
and counts local days (Wuthering Waves today).

`desktop.assist` (optional) is what else the desktop may read off the game's screen for a player who switched a
reader on (the in-game helper, `GI-Subtitles/Services/Assist`). A game without the block gets nothing.

- `checked` is the record: the game version, the window shapes measured (`aspects`, like `"16:9"`) and the source.
  A window of a shape that is not listed gets no fixed place. That is the refusal rule, as data.
- `canvas` is the size the game draws its interface for and how it scales (`min`, `height` or `width`). Measure
  the rule for each game; it is required as soon as anything has a fixed place. Wuthering Waves draws for
  1920x1080 and scales by `min`, and pins HUD pieces to the window's own edges, so on a 21:9 window they sit
  outside the centred 16:9 band. That is why a place here is "this far from that corner" and never a fraction.
- `screens` maps a screen id to one or two anchors: a template (`GI-Subtitles/Assets/Assist/<game>/<template>.png`,
  exactly `w` x `h`, at most 64x64), the `edge` it is measured from (`topLeft` … `bottomRight`, `center`), `x` and
  `y` in canvas pixels from that point to the box's top-left corner (negative: left of it, above it), and a
  `threshold`. Pick pictures that do not change with the input device: a controller player sees other button
  glyphs than a keyboard player.
- `readers` maps a reader id to its `kind` (`fraction`, `count`, `names`, `packText`, `panel`) and how it is
  located: `"locate": "box"` with a `screen` and a `box`, or `"locate": "search"` with a `spec` naming the desktop
  block that describes the search. A fraction names its `cap` (`"stamina.max"`), names their `vocabulary`. A
  reader the desktop build has no code for stays off. Each reader id needs its words in both Strings dictionaries
  (`Assist_Reader_<id>_Title`, `Assist_Reader_<id>_Body`: one sentence saying exactly what is read); a test checks.

The one reader today is `stamina`: `fraction`, located by `search` with `spec: "staminaScreen"`. It finds its own
icon in the top band (`desktop.staminaScreen`), so it is not bound to `checked.aspects`; where the icon is not
found nothing is read. A `desktop.staminaScreen` without that reader fails validation, because the screen watch
runs only what `desktop.assist.readers` lists.

When the executable name is generic (Unreal's `Client-Win64-Shipping` is shared by many games), set
`"detect": { …, "requireTitle": true }`: the app then counts a process as the game only while one of its windows
has a title from `windowTitles`. Two manifests may share an executable only if both set it.

`site.upcoming` (optional) says how a character that is not out yet appears on the site (its pages, search, the
guess game, share cards), in the content API (`/api/content/characters/<game>`), in the translation explorer's kit
text, in the Discord bot's `/postac` and in the desktop bundle's `characters` section. Every game leaves it out today,
which means `full`.

| Value | Before the character is out |
|---|---|
| `full` (default) | Announced with the whole kit: skills, numbers, ranks, build, materials, guide text, videos, story lines. |
| `teaser` | Name, portrait, element, weapon type and the day only (plus rarity and the version). No video, story line, share card or guess answer. A character without a known day is not shown at all. Other characters' pages keep it in their plain teammate lists (a name and a portrait), but a team that includes it, any guide, build or role line that names it, and any video whose title names it go: a program cannot tell a line that only names it from one that says what its kit does. A released character's official skills stay whole ("Story lines and official skills stay" below). |
| `hide` | Not shown anywhere: no page, no search or guess entry, no Discord card, no teammate entry, no team or prose that names it, no other character's video whose title names it. |

**When the kit opens.** At 00:00 UTC of the character's own day, in every mode; a future own day holds the kit back
even when the data already calls the character live. Only a day from the roster seed or the wiki
(`release_date_source` `seed` or `wiki`) is a character's own. A day that is only the version's first day (`version`,
from `roster_meta` when neither knows the banner), or one with no or an unknown source, never opens a kit: such a
character stays a teaser (or hidden) until the next version is out, when every banner of its version has ended. Add
its own day to the roster seed (`updater/content/roster/<game>.json`) to open it on the day. A version past the one
out never opens either, whatever its day says (a year typo in the seed). "The version out" is the section's
`game_version` once its first day has come, else `game_live_version`, so a version's first-half characters open on its
day even when the bundle was built the day before (a cached answer expires at that day too). Wuthering Waves dates a
resonator by its own day only, so under `teaser` or `hide` a resonator without one is not shown; its extractor marks
the banner days of `tools/characters/wuwa-release-days.json` as `seed` while the game withholds kits, and a WuWa day
without that mark opens nothing. 00:00 UTC is usually some hours before the character is
playable (a banner starts later that day, by server), so a kit shows a little early by design. A teaser record
carries the day it shows as `teaser_date`.

**Fail closed.** Under `teaser` or `hide` a section that cannot say who is out is never served or shipped whole: a
game that is not own-day-only needs the live version and the date sources of the updater's release block
(`release_date_sources`), and an own-day-only game (WuWa) needs at least one record with a day of its own
(`release_date_source` `seed` or `wiki`; a bundle built while the game was on `full` has none). Without them the bundle builder stops (the updater job holds), the Worker answers 503 (and
reports it to Sentry), the card API draws from no catalog, the explorer withholds every kit, and the site keeps its
committed copy. A site build that would show nobody of a game in full (the live section refused and a committed copy
that withholds everyone) stops instead of dropping the game's pages, so Cloudflare keeps the deployment from before the
flip; `tools/discord/generate.cjs` refuses such a copy too. The updater builds that block on its own when the optional character notes fail or are off (a
release-only `--character-ai` file), so a failed notes step never holds a patch; it holds only when `roster_meta`
cannot say.

**The committed copy.** The site falls back to `landing/src/data/characters/<game>.json`, and the Discord index is
made from it. It is a snapshot, so under `teaser` or `hide` it shows only what it has evidence for: it withholds a
record it marked upcoming or as a teaser, one whose day is still ahead, one without release fields (unless the copy
recorded the live rule's `status: "live"` for it, a Traveler), an undated one of a version it cannot place before its
own, one whose day it cannot vouch for as the character's own (a copy written under a switch
keeps `release.source` and `liveVersion`; one written under `full` has neither, so every record of its own version, or
dated within a version's length, is withheld), and one whose version is past the copy's live version. When the live
section was read in the same build, the live rule's verdict is applied to the copy too (the stricter view wins), and a
page the live rule withholds never counts as one the live section would take down. A refresh
(`refresh-live.cjs`) applies the same, so it can write a copy under `hide`.

**Where it is applied.** The rule is `tools/characters/release.cjs`, ported to `backend/src/lib/character-release.ts`
(one table of cases holds them together: `tools/fixtures/character-release/cases.json`); landing data that is
already built goes through `tools/landing-characters/withhold.cjs`. `node tools/games/generate.cjs` writes the
per-game value into `tools/characters/upcoming-modes.cjs` and `backend/src/generated/games.ts`. The site applies it
at build time (`landing/src/data/characters/load.ts` through `live.cjs` `siteGame`, the live section and the committed
copy alike; the material planner's tables, its picker and the Today data file through
`landing/src/lib/tools-withhold.mjs`), the content API per request (never cached past the midnight that opens the next
kit), the card API's catalog per UTC day (`backend/src/cards/catalog.ts` `cardsCatalog`), the Discord index when it is
generated (and the bot again per request), and the desktop bundle when it is built. The site copy of the section
(`gamedata.characters.json`, uploaded by the updater) stays whole so the Worker can release a kit on its day. A hidden
character is named nowhere: the others' teammates, AI build teammates, teams and the prose that names it (a build
note, a guide paragraph, a skill's AI one-liner, the role line, a video title) go in every output; a teaser loses the
same, except the plain teammate lists. A name is matched as it is written and in capitals (its display, English and
Polish names and gloss, with any spacing or hyphen between the words) and by a multi-word slug as written
("dr-ratio"), never in lower case: "a glowing pearl" is not Pearl. A name that is also an ordinary word still takes a
line that opens with it ("Seed of ..."), so `flip-check` prints every line a flip would take, per character and field,
before the flip.
`backend/test/official-only.test.mjs` builds every output for every game under both modes and searches it.
`tools/landing-characters/official-only-readers.cjs` lists every file that reads unfiltered character data (the
committed copies, the stored section or its key helper, the planner and Today tables, the Discord index, the story
lines, the guess answers and origins, the extractor's output); the Backend, Tools and Landing jobs all scan for them, so
a new reader fails its own pull request until it has a row. `tools/test-official-only.cjs` checks the site data paths
in more detail and holds the output with every switch off to a hash recorded from the builders before the switch
existed (`tools/fixtures/official-only/switch-off.json`).

**The switch-off pin.** The hash covers every tools output over the official-only fixtures with no switch and with
every game on `full`. When the fixtures or the builders change on purpose, check the new hash against a checkout from
before the switch, then record it:

```bash
mkdir -p "$SCRATCH/pre-switch" && git archive aef41afc tools | tar -x -C "$SCRATCH/pre-switch"
node tools/fixtures/official-only/switch-off.cjs --tools "$SCRATCH/pre-switch/tools"   # prints none/full and "legacy": true
node tools/fixtures/official-only/switch-off.cjs                                       # this checkout: the same none/full
node tools/fixtures/official-only/switch-off.cjs --record                              # writes switch-off.json
```

`aef41afc` is master just before the switch. `switch-off.cjs` reads a checkout without `withhold.cjs`, `siteGame` or
`releasedSection` the way its site worked (the live section, else the committed copy as it was; the section served
whole) and says `"legacy": true`. The fixtures always come from this checkout. Two different hashes mean the switch
changed something with every switch off: find it before recording. `--record` with `--tools` writes nothing.

**Who hears about it.** Every bundle built under a switch tells the ops channel how many characters it held back and
who (`characters_withheld`); it warns when records say upcoming and nothing was held back, or more than `[content]
characters_withheld_max` (12) were, and the job holds when a character of a version older than the live one is held
back (the release data is wrong). A section without release information is a Sentry error from the Worker.
`kaption-updater doctor` warns when the VPS's `upcoming-modes.cjs` differs from the manifests (a flip pushed without
the generator). The note names who was held back, not what the others' pages lost: run `flip-check` again after a patch
that brings a new upcoming name (open: the note does not print that reach yet, nor hold over a cap of it).

**Discord.** The index (`backend/src/generated/discord-characters.ts`) is cut when it is generated, so after a
character's day its card says the build arrives with the next update until the index is regenerated (`refresh-live.cjs`
does it, or `node tools/discord/generate.cjs`) and the Worker is deployed. Under a switch, do that after each release
day (with the copy refresh).

**Flipping a switch.** Run every step in this order; the commands read the generated modes, so the manifest change
comes first, on the flip's branch.

1. **The live data can back it.** The game's live bundle comes from an updater with this change (`kaption-updater
   rebuild --game <game>` on the VPS builds one without waiting for a patch). Genshin, Star Rail and ZZZ: the
   character notes bring the release block under any mode, so `curl -s
   https://api.kaption.one/api/content/characters/<game> | jq .release_date_sources` prints `true`. Wuthering Waves is
   different: its extractor dates the records itself and stamps them `seed` only while the switch is on, so today's
   bundle has no stamps and a flip against it is refused everywhere (step 3 covers it).
2. **On the flip branch:** set `site.upcoming` in `games/<game>.json` and run `node tools/games/generate.cjs` (it
   writes `tools/characters/upcoming-modes.cjs` and the Worker's table).
3. **Wuthering Waves only, before anything else reads the mode:** from that branch, `updater/deploy/push.sh oracle`
   (the VPS gets the new `upcoming-modes.cjs`), then `ssh oracle kaption-updater rebuild --game wuwa`. That bundle
   already holds the kits back on the desktop and carries the stamps, while the Worker, still on `full`, serves the
   whole section: `curl -s https://api.kaption.one/api/content/characters/wuwa | jq '[.characters[].release_date_source]
   | unique'` holds `"seed"`.
4. **Who it withholds:** `node tools/characters/flip-check.cjs <game> <teaser|hide>` lists the teasers and hidden
   characters against the live section. It refuses (exit 1) a flip that would withhold everyone or that the section
   cannot be judged under, and flags (exit 2) one that would withhold nobody (`--allow-none` when that is right).
   Read the rest of what it prints before going on: what the others' pages would lose (per page, each team by name and
   each other line with its field), the loose mentions the switch leaves (below), and the released text that keeps
   the name on purpose (official skills, story lines).
5. **The rest of the pull request:** `node tools/landing-characters/refresh-live.cjs` (the committed copies, refreshed
   under the new mode, carry `liveVersion` and `release.source`) and `node tools/discord/generate.cjs`. CI builds the
   site under the new mode, so a flip the data cannot back fails there.
6. **After the merge:** deploy the Worker, let the site build (the push follows the merge), `updater/deploy/push.sh
   oracle` from master (for WuWa this puts the VPS back on master), then run the share-cards workflow. The site's
   build log says `[characters] <game>: live section ...`, not "using the committed copy" and not "withholds nobody".
7. **After each release day under a switch:** refresh the copies, regenerate the Discord index, deploy the Worker.

**A later banner day with no patch in between.** Nothing has to happen for a character to open, and nothing opens
early:

- The content API opens the kit at 00:00 UTC of the character's own day (its cached answer expires then).
- The site rebuilds at 00:20 UTC on any day a character of the live section has that day as its `release_date`
  (`.github/workflows/site-release-day.yml`); the build then reads the opened kit from the content API. A Genshin
  character dated only by its version's first day opens with the next version, whose first day is also a release day.
  If that rebuild failed, run the workflow by hand or push to master.
- The desktop bundle is built per patch, so it keeps withholding the kit until the next bundle: late, never early.
  For the kit sooner on the desktop, `ssh oracle kaption-updater rebuild --game <game>` builds and ships one now.
- The committed copies and the Discord index still say "upcoming" until step 7 is run; the site does not read the
  copy while the live section answers, and the bot says the build arrives with the next update.

**What a flip does not reach.** The live desktop bundle on R2 and the copies players have keep a kit until the next
bundle. Old Cloudflare Pages deployments keep the earlier pages. A restrictive game shows no share cards or card kits
until the share-cards workflow has rendered a manifest under the new mode (it records the modes; nightly, or run
`share-cards.yml` by hand), and card files already on `files.kaption.one` stay reachable by their URL (immutable for a
year, pruned from R2 after a week). The committed images under `landing/public/img/characters/` (portraits and
signature weapons, by the game's file names) stay deployed. Responses cached before the flip live up to about an hour.
News, update pages and feature articles, the translation packs and the names in the explorer are outside this switch.
**Story lines and official skills stay.** A released character's story lines (`lines`, curated from the game's own
released dialogue, shown on its page and in the guess game's quote mode) and its official skills (names and text: the
game's own released kit, which every owner of that character reads in the game) are not scrubbed, so they may name a
withheld character under `teaser` or `hide`. They never describe that character's own kit: they say what the released
character does. A skill also keeps its place, because its place numbers the ranks (the site's Constellation, Eidolon,
Mindscape and Sequence numbers, the share cards and the desktop all count them): dropping one would renumber every rank
after it. Star Rail's Yanqing, for one, has E1 "Svelte Saber", and a Saber flip leaves it as it is. In practice: in today's data no line names Genshin's Mitya or Valeriy (the two a Genshin flip would withhold now), and
with Star Rail's Pearl withheld in a test, two lines name her: Jade's "The Ten
Stonehearts await a metamorphosis. May you find your own "Pearl" in this strange and dazzling sea." and Ashveil's
"Before we proceed, I need to confirm something. What was your assignment from Pearl?". They give a name, never a kit.
A flip that must not name a character anywhere needs those lines taken out by hand, from the lines file
(`tools/out/lines/<game>.json`) and from the committed copy.
**Short and declined names.** The switch matches a name as it is written (see "Where it is applied"). It does not
match a short form ("Raiden" for "Raiden Shogun", "Itto", "Kuki") or a declined Polish form ("Rosarię" for
"Rosaria"): telling those from ordinary words takes grammar and judgement a program would guess at. In today's data
that is 18 lines with a short form (mostly stat-goal notes such as "with another Anemo character or Raiden") and one
declined form (Amber's guide: "Citlali albo Rosarię"), none of them about the withheld character's own kit.
`flip-check` lists every such loose mention for the characters a flip withholds (a word of a multi-word name, alone or
with up to four more letters, and a one-word name's stem with up to four more letters), so the owner reads them and
removes the ones that matter by hand (the updater's character notes, then a rebuild) before the flip.
