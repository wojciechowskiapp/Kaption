using System;

namespace GI_Subtitles.Services.Games
{
    /// <summary>
    /// The desktop's view of games/&lt;id&gt;.json (schema in games/game.schema.json). Only the blocks the app reads
    /// are modelled; of the news block only news.codes (the Start page's codes card), the rest belongs to the site,
    /// backend and updater.
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
        public GameNews News { get; set; }
        /// <summary>The game's stamina and how it refills; null for a game without one.</summary>
        public GameStamina Stamina { get; set; }
        /// <summary>When the game's day and week turn over; null while that is not confirmed on the publisher's own pages.</summary>
        public GameResets Resets { get; set; }

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

    /// <summary>How much evidence the Start card's session check needs; null keeps the GameRegionProfile property initializer.</summary>
    public sealed class GameSessionCheck
    {
        public int? BlackSeconds { get; set; }
        public int? UnmatchedReads { get; set; }
        public int? QuietSeconds { get; set; }
    }

    public sealed class GameDesktop
    {
        public const string Passthrough = "passthrough";
        public const string MirrorGenshin = "dimbreath-genshin", MirrorStarRail = "dimbreath-starrail", MirrorNone = "none";

        public GameRect Dialogue { get; set; }
        public GameRect Answer { get; set; }
        public GamePacing Pacing { get; set; }
        public GameVision Vision { get; set; }
        public GameSessionCheck SessionCheck { get; set; }
        public string DialogueContext { get; set; }
        public string UpstreamTextMap { get; set; }
        public bool DisplayModeTip { get; set; }
        /// <summary>The player's default name as the text spells it ("Rover"); null keeps <see cref="DefaultPlayerNameTokens"/>.</summary>
        public string[] PlayerNameTokens { get; set; }
        /// <summary>Where the game draws its stamina number and how to read it; null until measured on real frames.</summary>
        public GameStaminaScreen StaminaScreen { get; set; }
        /// <summary>What else the app may read off this game's screen, as data (Services/Assist); null: nothing.</summary>
        public GameAssist Assist { get; set; }

        /// <summary>What every game replaced before manifests had the field: Genshin's "Traveler".</summary>
        public static readonly string[] DefaultPlayerNameTokens = { "Traveler", "traveler" };
    }

    public sealed class GameLookSpec
    {
        public string Accent { get; set; }
        public string AccentDark { get; set; }
        public string Glow { get; set; }
        /// <summary>The site's colour for the game's small mark (look.signal); the app does not draw it.</summary>
        public string Signal { get; set; }
        public string Ambience { get; set; }
        /// <summary>Element colours the game draws its own way ("Physical" is yellow in ZZZ), by English name.</summary>
        public System.Collections.Generic.Dictionary<string, string> ElementHues { get; set; }
    }

    public sealed class GameSite
    {
        public string Hub { get; set; }
        public string GuideKey { get; set; }
        /// <summary>The daily guess game's name on the site ("Gendle", "Wuwdle"): site.guessName.</summary>
        public string GuessName { get; set; }
        /// <summary>The site's drawn mark for the game (site.mark, a path under landing/); the app does not draw it.</summary>
        public string Mark { get; set; }
        /// <summary>The site's pattern behind the game's typographic leads (site.motif); the app does not draw it.</summary>
        public string Motif { get; set; }
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

    /// <summary>The manifest's news block; the desktop reads only its codes.</summary>
    public sealed class GameNews
    {
        /// <summary>null: the game has no redeem codes (and no codes page on the site).</summary>
        public GameCodes Codes { get; set; }
    }

    /// <summary>
    /// news.codes: "hoyolab" codes are redeemed on the publisher's page (<see cref="RedeemUrl"/> with {code}),
    /// "ingame" ones inside the game (<see cref="RedeemHow"/>). Both have a codes page on kaption.one.
    /// </summary>
    public sealed class GameCodes
    {
        public const string KindHoyolab = "hoyolab", KindInGame = "ingame";
        public const string CodePlaceholder = "{code}";

        public string Kind { get; set; }
        /// <summary>An https address holding {code}; null for an in-game game.</summary>
        public string RedeemUrl { get; set; }
        public GameRedeemHow RedeemHow { get; set; }
    }

    /// <summary>The menu path to the code field in the game, and when it unlocks.</summary>
    public sealed class GameRedeemHow
    {
        public string[] Path { get; set; }
        public string Unlock { get; set; }
    }

    /// <summary>The manifest's resets block: the servers, the daily reset hour and the weekly reset day, in server time.</summary>
    public sealed class GameResets
    {
        public GameResetServer[] Servers { get; set; }
        /// <summary>The hour of the daily reset, server time.</summary>
        public int DailyHour { get; set; }
        /// <summary>The weekday of the weekly reset at <see cref="DailyHour"/>: 0 Sunday to 6 Saturday.</summary>
        public int WeeklyDay { get; set; }

        /// <summary>The server with this id, else the first one (the default); null when the block lists none.</summary>
        public GameResetServer Server(string id)
        {
            if (Servers == null) return null;
            foreach (GameResetServer s in Servers)
                if (s != null && string.Equals(s.Id, id?.Trim(), System.StringComparison.OrdinalIgnoreCase)) return s;
            return Servers.Length > 0 ? Servers[0] : null;
        }
    }

    /// <summary>A game server: a fixed offset from UTC, no daylight saving.</summary>
    public sealed class GameResetServer
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int UtcOffset { get; set; }
    }

    /// <summary>A stamina pool: the game's English name, where regeneration stops, seconds per point.</summary>
    public class GameStaminaPool
    {
        public string Name { get; set; }
        public int Max { get; set; }
        public int RegenSeconds { get; set; }
    }

    /// <summary>The manifest's stamina block (the site's timer reads the same numbers).</summary>
    public sealed class GameStamina : GameStaminaPool
    {
        /// <summary>The most a player can hold through refills; null when the data does not say.</summary>
        public int? Hold { get; set; }
        public GameStaminaPool Reserve { get; set; }
    }

    /// <summary>
    /// desktop.staminaScreen: how the app finds "160/200" beside the stamina icon. Positions are fractions of the game's
    /// client area, lengths are in icon heights, and the icon template is drawn for a 1920x1080 client.
    /// </summary>
    public sealed class GameStaminaScreen
    {
        /// <summary>Template name: Assets/Stamina/&lt;icon&gt;.png.</summary>
        public string Icon { get; set; }
        public GameRect Search { get; set; }
        /// <summary>Icon sizes tried, relative to the template (screens of one game draw it at different sizes).</summary>
        public double[] Scales { get; set; }
        public double MinIconScore { get; set; }
        public GameStaminaDigits Digits { get; set; }
        public double MinTextScore { get; set; }
        public int SampleSeconds { get; set; }
        /// <summary>Agreeing samples before a number that differs from the expected one is believed.</summary>
        public int Confirmations { get; set; }
        /// <summary>The same for a number higher than regeneration explains (a refill).</summary>
        public int RefillConfirmations { get; set; }
    }

    /// <summary>
    /// desktop.assist: the screens the app can tell apart and what it may read on them, for a player who switched a
    /// reader on. A game without the block gets nothing. Fixed places are in pixels of <see cref="Canvas"/>, measured
    /// from a window edge, and count only in a window whose shape is in <see cref="Checked"/>.
    /// </summary>
    public sealed class GameAssist
    {
        /// <summary>The size the game's interface is drawn for and how it scales; needed once anything has a fixed place.</summary>
        public GameAssistCanvas Canvas { get; set; }
        public GameAssistChecked Checked { get; set; }
        public System.Collections.Generic.Dictionary<string, GameAssistScreen> Screens { get; set; }
        public System.Collections.Generic.Dictionary<string, GameAssistReader> Readers { get; set; }
    }

    public sealed class GameAssistCanvas
    {
        public const string ScaleMin = "min", ScaleHeight = "height", ScaleWidth = "width";

        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>How the interface grows with the window: by the smaller of the two ratios, by height or by width. Measured per game.</summary>
        public string Scale { get; set; }
    }

    /// <summary>What the places were measured on: the record a game update is checked against.</summary>
    public sealed class GameAssistChecked
    {
        public string Version { get; set; }
        /// <summary>Window shapes measured ("16:9"); a window of another shape gets no fixed place.</summary>
        public string[] Aspects { get; set; }
        public string Source { get; set; }
    }

    /// <summary>A rectangle in canvas pixels: its top-left corner is <see cref="X"/>, <see cref="Y"/> away from the named point of the window.</summary>
    public class GameAssistBox
    {
        /// <summary>topLeft, top, topRight, left, center, right, bottomLeft, bottom or bottomRight.</summary>
        public string Edge { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
    }

    /// <summary>A small picture that is on a screen and nowhere else: Assets/Assist/&lt;game&gt;/&lt;template&gt;.png, at most 64x64.</summary>
    public sealed class GameAssistAnchor : GameAssistBox
    {
        public string Template { get; set; }
        /// <summary>Lowest match (normalised correlation) that counts.</summary>
        public double Threshold { get; set; }
    }

    public sealed class GameAssistScreen
    {
        /// <summary>One or two; all must match.</summary>
        public GameAssistAnchor[] Anchors { get; set; }
    }

    public sealed class GameAssistReader
    {
        public const string KindFraction = "fraction", KindCount = "count", KindNames = "names", KindPackText = "packText", KindPanel = "panel";
        public const string LocateBox = "box", LocateSearch = "search";
        public const string SpecStaminaScreen = "staminaScreen", CapStaminaMax = "stamina.max";

        public string Kind { get; set; }
        /// <summary>"box": a fixed place on <see cref="Screen"/>. "search": the reader finds its own anchor (<see cref="Spec"/>).</summary>
        public string Locate { get; set; }
        public string Screen { get; set; }
        public GameAssistBox Box { get; set; }
        /// <summary>For "search": the desktop block that describes the search ("staminaScreen").</summary>
        public string Spec { get; set; }
        /// <summary>Where the cap a fraction must show comes from ("stamina.max").</summary>
        public string Cap { get; set; }
        /// <summary>The list a name must be in.</summary>
        public string Vocabulary { get; set; }
    }

    /// <summary>The number's place to the right of the icon.</summary>
    public sealed class GameStaminaDigits
    {
        /// <summary>How far right of the icon the number can reach, in icon heights.</summary>
        public double Width { get; set; }
        /// <summary>The furthest right of the icon the number may start, in icon heights (a right-aligned number moves with its length).</summary>
        public double MaxStart { get; set; }
        /// <summary>A pixel is ink at this share of the brightest pixel in the strip beside the icon.</summary>
        public double InkLevel { get; set; }
        /// <summary>The widest gap inside the number, in digit heights; a wider one ends it (the "+" button).</summary>
        public double MaxGap { get; set; }
    }
}
