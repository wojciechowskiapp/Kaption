using System;

namespace GI_Subtitles.Services.Games
{
    /// <summary>
    /// The desktop's view of games/&lt;id&gt;.json (schema in games/game.schema.json). Only the blocks the app reads
    /// are modelled; the rest (news) belongs to the site, backend and updater.
    /// </summary>
    public sealed class GameManifest
    {
        public const string StatusPlanned = "planned", StatusBeta = "beta", StatusLive = "live", StatusWithdrawn = "withdrawn";

        public int Schema { get; set; }
        /// <summary>Wire id: R2 keys, D1, the bundle's game. Always <see cref="GameId"/> lower-cased.</summary>
        public string Id { get; set; }
        /// <summary>Config["Game"], the %APPDATA%\Kaption\&lt;GameId&gt;\ folder and the Game_&lt;GameId&gt; string key.</summary>
        public string GameId { get; set; }
        public string Name { get; set; }
        public string ShortName { get; set; }
        public string Publisher { get; set; }
        public string Status { get; set; }
        public int Order { get; set; }
        public string[] Aliases { get; set; }
        /// <summary>Other wire ids older tools used ("hsr"); only the loose lookups read them.</summary>
        public string[] LegacyIds { get; set; }
        public long? SteamAppId { get; set; }
        public GameDetect Detect { get; set; }
        public GameDesktop Desktop { get; set; }
        public GameLookSpec Look { get; set; }
        public GameSite Site { get; set; }
        public GameSource Source { get; set; }
        public GameTranslation Translation { get; set; }

        /// <summary>Beta and live games are offered in the app; planned and withdrawn ones are not.</summary>
        public bool IsListed => Status == StatusBeta || Status == StatusLive;
    }

    public sealed class GameDetect
    {
        public string[] ProcessNames { get; set; }
        public string[] WindowTitles { get; set; }
        /// <summary>A generic engine executable: the process is the game only while one of its windows has a title from <see cref="WindowTitles"/>.</summary>
        public bool RequireTitle { get; set; }
    }

    /// <summary>A region as fractions of the 16:9 reference area inside the game window.</summary>
    public sealed class GameRect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
    }

    /// <summary>Per-game OCR pacing; null keeps the global default (GameOcrTuning).</summary>
    public sealed class GamePacing
    {
        public int? OcrIntervalMs { get; set; }
        public int? StabilityWindowFrames { get; set; }
        public int? StableFramesChain { get; set; }
        public int? StableFramesDefault { get; set; }
        public double? ForceOcrAfterSeconds { get; set; }
    }

    /// <summary>Vision knobs; null keeps the GameRegionProfile property initializer.</summary>
    public sealed class GameVision
    {
        public const string Accent = "accent", Geometry = "geometry", SpeakerRow = "speaker-row";

        public string Classifier { get; set; }
        public bool? StripsSpeakerNameDecoration { get; set; }
        public int? SubtitlePadVertical { get; set; }
        public double? RegionSplitXFraction { get; set; }
        public double? RegionMinWidthFraction { get; set; }
        public bool? StrictRegionJunkFilter { get; set; }
        public bool? OcrJunkFilter { get; set; }
        public bool? DialogueEvidence { get; set; }
        public bool? FadedReadFilter { get; set; }
    }

    public sealed class GameDesktop
    {
        public const string Passthrough = "passthrough";
        public const string MirrorGenshin = "dimbreath-genshin", MirrorStarRail = "dimbreath-starrail", MirrorNone = "none";

        public GameRect Dialogue { get; set; }
        public GameRect Answer { get; set; }
        public GamePacing Pacing { get; set; }
        public GameVision Vision { get; set; }
        public string DialogueContext { get; set; }
        public string UpstreamTextMap { get; set; }
        public bool DisplayModeTip { get; set; }
        /// <summary>The player's default name as the text spells it ("Rover"); null keeps <see cref="DefaultPlayerNameTokens"/>.</summary>
        public string[] PlayerNameTokens { get; set; }

        /// <summary>What every game replaced before manifests had the field: Genshin's "Traveler".</summary>
        public static readonly string[] DefaultPlayerNameTokens = { "Traveler", "traveler" };
    }

    public sealed class GameLookSpec
    {
        public string Accent { get; set; }
        public string AccentDark { get; set; }
        public string Glow { get; set; }
        public string Ambience { get; set; }
        /// <summary>Element colours the game draws its own way ("Physical" is yellow in ZZZ), by English name.</summary>
        public System.Collections.Generic.Dictionary<string, string> ElementHues { get; set; }
    }

    public sealed class GameSite
    {
        public string Hub { get; set; }
        public string GuideKey { get; set; }
    }

    /// <summary>Where the game's English comes from (datamine host and repository).</summary>
    public sealed class GameSource
    {
        public string Kind { get; set; }
        public string Host { get; set; }
        public string Repo { get; set; }
        /// <summary>A branch name, or "@default" to follow the repository's default branch.</summary>
        public string Branch { get; set; }
        public string KeySpace { get; set; }

    }

    /// <summary>The languages a game is translated into and by whom: "auto" (the updater), "community" (reviewed fixes).</summary>
    public sealed class GameTranslation
    {
        public const string SourceAuto = "auto", SourceCommunity = "community";

        public string[] Targets { get; set; }
        public string[] Sources { get; set; }

        /// <summary>Whether players' fixes for <paramref name="lang"/> (a wire value, lower case) go to community review.</summary>
        public bool TakesCommunityFixes(string lang) =>
            lang != null && Sources != null && Targets != null
            && Array.IndexOf(Sources, SourceCommunity) >= 0 && Array.IndexOf(Targets, lang) >= 0;
    }
}
