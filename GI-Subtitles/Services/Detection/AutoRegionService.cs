using System;
using System.Drawing;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Detection
{
    /// <summary>
    /// "Set automatically" and setup's game step: the game's measured dialogue block (games/&lt;id&gt;.json), placed
    /// on the game's window. Nothing is read off the screen. A scan used to box whatever text one moment showed, so a
    /// one-line talk saved an area too small for the next long line, and Kaption's own window could be in the picture.
    /// </summary>
    public static class AutoRegionService
    {
        /// <summary>The dialogue and answer areas as "x,y,w,h" screen pixels, ready for Config.</summary>
        public class Result
        {
            public bool Success { get; set; }

            /// <summary>Dialogue area in screen pixels, or null on failure.</summary>
            public string DialogueRegion { get; set; }

            /// <summary>Answer area from the manifest's ratios, or null on failure.</summary>
            public string AnswerRegion { get; set; }

            /// <summary>The game window's client size, e.g. "2560x1440".</summary>
            public string Resolution { get; set; }

            /// <summary>How the area was made, for the log.</summary>
            public string Method { get; set; }

            public string Error { get; set; }

            /// <summary>No window of the game was found: the player has to start it first.</summary>
            public bool NoGameWindow { get; set; }

            /// <summary>The game runs but is minimised: bring it back, then try again.</summary>
            public bool GameMinimised { get; set; }

            /// <summary>The game's window is smaller than a game window (a launcher, a splash).</summary>
            public bool WindowTooSmall { get; set; }

            /// <summary>The desktop would cut the game's text: the window hangs off the screen.</summary>
            public bool OffScreen { get; set; }

            /// <summary>The game's client area the block was placed on (screen pixels).</summary>
            public Rectangle Client { get; set; }

            public static Result Failed(string error) => new Result { Success = false, Error = error };

            public static Result Succeeded(string dialogue, string answer, string resolution, string method) => new Result
            {
                Success = true,
                DialogueRegion = dialogue,
                AnswerRegion = answer,
                Resolution = resolution,
                Method = method,
            };
        }

        /// <summary>
        /// The game's block on its window, clipped to the desktop. Looks only at the game's own process (never a
        /// window that just names the game) and tells a minimised game, a window too small to be the game's and one
        /// hanging off the screen apart from a closed game. Looks up windows, so call it off the UI thread.
        /// </summary>
        public static Result Detect(string gameId)
        {
            var profile = GameRegionProfile.Get(gameId);

            GameWindowState state;
            GameWindowDetector.WindowInfo? windowInfo;
            try
            {
                (state, windowInfo) = GameWindowDetector.FindGameClient(profile);
            }
            catch (Exception ex)
            {
                // The player pressed a button: the status line says it, so this is not a Sentry event.
                Logger.Log.Warn($"Dialogue area: window lookup failed for {profile.GameId}: {ex.Message}");
                return Result.Failed($"Window detection error: {ex.Message}");
            }
            if (state == GameWindowState.Minimised)
                return new Result { Success = false, Error = "The game is minimised.", GameMinimised = true };
            if (windowInfo == null)
                return new Result { Success = false, Error = "Game window not found. Make sure the game is running.", NoGameWindow = true };

            var wi = windowInfo.Value;
            var client = new Rectangle(wi.GameX, wi.GameY, wi.GameW, wi.GameH);
            PlacementResult placed = DialogueAreaPlacement.Place(profile, client,
                System.Windows.Forms.SystemInformation.VirtualScreen, DialogueAreaPlacement.MonitorBounds());
            if (placed.Status == PlacementStatus.Unusable)
                return new Result { Success = false, Error = "The game's window is too small.", WindowTooSmall = true };
            if (placed.Status == PlacementStatus.OffScreen)
                return new Result { Success = false, Error = "The game's window is mostly off the screen.", OffScreen = true };

            string dialogue = DialogueArea.Format(placed.Area);
            var (_, answer) = GameWindowDetector.CalculateFromRatios(profile, wi.GameX, wi.GameY, wi.GameW, wi.GameH);
            Logger.Log.Info($"Dialogue area for {profile.GameId}: {dialogue} on a {wi.GameW}x{wi.GameH} window (measured block).");
            var result = Result.Succeeded(dialogue, answer, $"{wi.GameW}×{wi.GameH}", "measured block");
            result.Client = client;
            return result;
        }

        /// <summary>
        /// The profile's dialogue block on a window whose client area is <paramref name="client"/> (screen pixels),
        /// clipped to <paramref name="desktop"/>; null when it cannot be placed (<see cref="DialogueAreaPlacement.Place"/>).
        /// </summary>
        public static string PlaceBlock(GameRegionProfile profile, Rectangle client, Rectangle desktop)
        {
            PlacementResult placed = DialogueAreaPlacement.Place(profile, client, desktop);
            return placed.Status == PlacementStatus.Placed ? DialogueArea.Format(placed.Area) : null;
        }
    }
}
