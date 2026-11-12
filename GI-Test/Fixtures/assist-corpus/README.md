# Frames for the in-game helper

The readers of the in-game helper (`GI-Subtitles/Services/Assist`) are tested on real frames. The frames are footage
and are never committed; what is committed is one small JSON per frame saying where it comes from and what must be
read on it. `benchmark/tools/assist_frames.py` fetches the frames (one frame per sample, by HTTP seek, no full
download) into a folder on your PC, and `GI-Test/AssistCorpusTests.cs` runs every reader of the sample's game over
them, through the same watch the app uses.

```bash
# What is committed, checked (no network):
python -I benchmark/tools/assist_frames.py check

# A new sample: writes GI-Test/Fixtures/assist-corpus/<game>/<id>.json and fetches its frame.
python -I benchmark/tools/assist_frames.py add --game wuwa --video <video id> --at 754.5 --aspect 16:9 \
    --expect stamina=240 --note "the map, pool full" --frames F:/kaption-work/scratch/assist-frames
python -I benchmark/tools/assist_frames.py add --game genshin --video <video id> --at 20 --aspect 16:9 --none \
    --frames F:/kaption-work/scratch/assist-frames

# The frames this PC does not have yet:
python -I benchmark/tools/assist_frames.py fetch --frames F:/kaption-work/scratch/assist-frames

# The readers over them (inconclusive without samples or without the folder):
KAPTION_ASSIST_FRAMES=F:/kaption-work/scratch/assist-frames \
  bash tools/dev/heavy.sh dotnet test GI-Test/GI-Test.csproj -c Debug --filter "FullyQualifiedName~AssistCorpus"
```

## A sample

`GI-Test/Fixtures/assist-corpus/<game>/<id>.json`:

| Key | Meaning |
|---|---|
| `id` | The file's name; lower-case words joined by hyphens. |
| `game` | The game's wire id (the folder). |
| `video`, `at` | The video's id and the moment in it, in seconds. |
| `format` | Optional yt-dlp format; the default takes the best video up to 1080 lines. |
| `aspect` | The window shape the frame shows (`16:9`, `16:10`, `21:9`). |
| `client` | Optional `[x, y, width, height]` of the game picture inside the frame, when the video is letterboxed. |
| `expect` | Reader id to the value it must read. A reader that is not named must read nothing, so `{}` is a frame on which nothing may be read. |
| `note` | Optional: which screen it is. |

A frame that is not on the PC is reported as "not fetched", not as a failure. A reading where none is expected, a
wrong value or a missed one fails the test.

## What is here today

No sample yet. The stamina reader was measured on 94 frames whose videos and moments were not written down
(`.plan/in-progress/STAMINA-FROM-SCREEN.md`); they are still read by `StaminaFootageTests` from a local folder
(`KAPTION_STAMINA_FOOTAGE`). The next reader brings its frames as samples here, and recutting the stamina frames
from known videos is the way to move that test over.

Footage is for internal testing only. Never commit a frame or a crop of one.
