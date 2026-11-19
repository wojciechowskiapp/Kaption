using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using GI_Subtitles.Common;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Data;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Services.QuickTranslate;

namespace GI_Subtitles.Services.Hints
{
    /// <summary>
    /// The in-game hint card's Config keys. Everything stays in Config.json on this PC; the card has no server side.
    /// </summary>
    public static class HintSettings
    {
        /// <summary>The card shows on its own. An absent key means on, and nothing writes it until the player chooses.</summary>
        public const string EnabledKey = "Hints.Enabled";
        public const bool DefaultEnabled = true;
        /// <summary>The sheet stays up while translation runs and the game is in front.</summary>
        public const string PinnedKey = "Hints.Pinned";
        /// <summary>Per game (wire id): "left" or "top", the place the player moved the card to.</summary>
        public const string PlaceKeyPrefix = "Hints.Place.";
        /// <summary>Written by Kaption: <see cref="HintState"/>.</summary>
        public const string StateKey = "Hints.State";
        /// <summary>Lets the card out in the end-to-end test mode, where it is otherwise silent.</summary>
        public const string InE2EKey = "Hints.InE2E";

        /// <summary>
        /// A switch, the pin or a place was written here (on the writer's thread). The window keeps the three values
        /// and reads them again on this, so its once-a-second look never goes to Config.
        /// </summary>
        public static event Action Changed;

        public static bool Enabled
        {
            get => Resolve(Config.Has(EnabledKey) ? Config.Get(EnabledKey, DefaultEnabled) : null);
            set
            {
                if (Enabled == value) return;
                Config.Set(EnabledKey, value);
                Changed?.Invoke();
            }
        }

        public static bool Resolve(bool? saved) => saved ?? DefaultEnabled;

        public static bool Pinned
        {
            get => Config.Get(PinnedKey, false);
            set
            {
                if (Pinned == value) return;
                Config.Set(PinnedKey, value);
                Changed?.Invoke();
            }
        }

        /// <summary>The place tried first for this game; left until the player moves the card.</summary>
        public static HintPlace Place(string wireId) =>
            string.IsNullOrEmpty(wireId) ? HintPlace.Left : HintPlacement.ParsePlace(Config.Get<string>(PlaceKeyPrefix + wireId, null));

        public static void SetPlace(string wireId, HintPlace place)
        {
            if (string.IsNullOrEmpty(wireId) || Place(wireId) == place) return;
            Config.Set(PlaceKeyPrefix + wireId, HintPlacement.NameOf(place));
            Changed?.Invoke();
        }

        /// <summary>False in the end-to-end test mode unless <see cref="InE2EKey"/> is set: its windows are driven by a script.</summary>
        public static bool AllowedInThisMode => !E2EMode.IsActive || Config.Get(InE2EKey, false);

        /// <summary>
        /// What the card remembers. An install without a readable memory gets a new one, saved at once, because this
        /// is the only moment that tells a new install from a player who already had Kaption.
        /// </summary>
        /// <param name="newInstall">The setup has finished and no line has ever shown on this install.</param>
        public static HintState LoadState(bool newInstall, DateTimeOffset now)
        {
            // Read as a node: a value of the wrong kind is then nothing, not a failed read for Sentry.
            HintState state = HintState.FromJson(Config.Get<JsonNode>(StateKey, null));
            if (state != null) return state;
            state = HintState.Create(NoticeGate.DayOf(now), newInstall, KeysUsedBefore());
            SaveState(state);
            return state;
        }

        public static void SaveState(HintState state)
        {
            if (state != null) Config.Set<JsonNode>(StateKey, state.ToJson());
        }

        /// <summary>The shortcuts an older version left a trace of: a saved Ctrl+Q area, a file of the player's own fixes.</summary>
        private static List<int> KeysUsedBefore()
        {
            var used = new List<int>(2);
            if (Config.Has(QuickTranslateArea.ConfigKey)) used.Add(HintCatalog.KeyQuickTranslate);
            if (AnyOwnFixes()) used.Add(HintCatalog.KeyFixLine);
            return used;
        }

        private static bool AnyOwnFixes()
        {
            try
            {
                foreach (GameManifest game in GameRegistry.Current.Listed)
                {
                    string dir = GameDataPaths.GameDir(game.GameId);
                    if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "overrides.*.json").Any()) return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not knowing costs one tip at most.
            }
            return false;
        }
    }
}
