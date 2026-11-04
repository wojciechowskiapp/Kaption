using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Cards;
using GI_Subtitles.Services.Detection;

namespace GI_Subtitles.Services.Today
{
    /// <summary>
    /// The farm card's Config keys (docs/CONFIG.md, "Farm today"). The UID is game-scoped: ShowcaseUid_&lt;GameId&gt;,
    /// the profile id as the Settings pill writes it. Only a UID's shape is ever kept or sent.
    /// </summary>
    public static class FarmCardSettings
    {
        public const string EnabledKey = "FarmCardEnabled";
        public const string RegionKey = "FarmCardRegion";
        public const string LastShownDayKey = "FarmCardLastShownDay";
        public const string UidKeyPrefix = "ShowcaseUid_";

        /// <summary>Only Genshin rotates its domains by weekday.</summary>
        public static string GameId => GameRegionProfile.Get("Genshin").GameId;

        /// <summary>The selected game is Genshin (aliases folded): the tray item and the Characters block show only then.</summary>
        public static bool IsActiveGame() => IsGame(Config.Get<string>("Game", "Genshin"));

        public static bool IsGame(string game) =>
            !string.IsNullOrWhiteSpace(game) && string.Equals(GameRegionProfile.Get(game).GameId, GameId, System.StringComparison.Ordinal);

        public static bool Enabled
        {
            get => Config.Get(EnabledKey, true);
            set => Config.Set(EnabledKey, value);
        }

        public static FarmServer Region
        {
            get => ServerDay.Parse(Config.Get<string>(RegionKey, "europe"));
            set => Config.Set(RegionKey, ServerDay.Id(value));
        }

        public static string LastShownDay
        {
            get => Config.Get<string>(LastShownDayKey, null);
            set => Config.Set(LastShownDayKey, value);
        }

        public static string UidKey(string gameId) => UidKeyPrefix + GameRegionProfile.Get(gameId).GameId;

        /// <summary>The saved UID, or null (none, or not a UID's shape: an edited file is never sent).</summary>
        public static string Uid
        {
            get
            {
                string uid = Config.Get<string>(UidKey(GameId), null)?.Trim();
                return BuildCardLink.IsUid(uid) ? uid : null;
            }
        }

        /// <summary>Saves a UID; false and nothing written when it is not one.</summary>
        public static bool SaveUid(string uid)
        {
            uid = uid?.Trim();
            if (!BuildCardLink.IsUid(uid)) return false;
            Config.Set(UidKey(GameId), uid);
            return true;
        }

        public static void ForgetUid()
        {
            if (Config.Has(UidKey(GameId))) Config.Remove(UidKey(GameId));
        }

        /// <summary>A UID the build card already read from the game screen: offered in the field, saved only on the player's click.</summary>
        public static string SuggestedUid => Uid == null ? BuildCardMemory.Recall(GameId) : null;
    }
}
