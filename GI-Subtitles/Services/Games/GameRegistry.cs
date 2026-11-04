using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using GI_Subtitles.Services.Detection;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Games
{
    /// <summary>
    /// The games this build knows, from the embedded manifests. <see cref="Profiles"/> backs
    /// <see cref="GameRegionProfile.RegisteredProfiles"/>; the lookups below replace the per-file switch statements
    /// that used to list Genshin, Star Rail and ZZZ by hand.
    /// </summary>
    public sealed class GameRegistry
    {
        private static readonly Lazy<GameRegistry> Embedded =
            new Lazy<GameRegistry>(() => FromManifests(GameManifestLoader.LoadEmbedded()), LazyThreadSafetyMode.ExecutionAndPublication);
        private static GameRegistry _override;

        /// <summary>The registry the app runs on: the embedded manifests, unless a test installed another.</summary>
        public static GameRegistry Current => Volatile.Read(ref _override) ?? Embedded.Value;

        /// <summary>Tests: runs on <paramref name="registry"/> until the returned handle is disposed.</summary>
        public static IDisposable Use(GameRegistry registry)
        {
            GameRegistry previous = Interlocked.Exchange(ref _override, registry);
            return new Restore(previous);
        }

        private sealed class Restore : IDisposable
        {
            private readonly GameRegistry _previous;
            private int _done;
            public Restore(GameRegistry previous) => _previous = previous;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0) Volatile.Write(ref _override, _previous);
            }
        }

        private readonly Dictionary<string, int> _bySpelling = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _byWireId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _byLooseName = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Every valid manifest, whatever its status, in UI order.</summary>
        public IReadOnlyList<GameManifest> Manifests { get; }

        /// <summary>Beta and live games in UI order: what the app offers.</summary>
        public IReadOnlyList<GameManifest> Listed { get; }

        /// <summary>One profile per <see cref="Listed"/> game, same order.</summary>
        public IReadOnlyList<GameRegionProfile> Profiles { get; }

        private GameRegistry(IReadOnlyList<GameManifest> manifests)
        {
            Manifests = manifests;
            var listed = new List<GameManifest>();
            var profiles = new List<GameRegionProfile>();
            foreach (GameManifest m in manifests.Where(m => m.IsListed))
            {
                if (_byWireId.ContainsKey(m.Id))
                {
                    Logger.Log.Error($"Game manifest {m.Id} is listed twice; the first one is used.");
                    continue;
                }
                int index = listed.Count;
                listed.Add(m);
                profiles.Add(GameProfileBuilder.Build(m));
                _byWireId[m.Id] = index;
                foreach (string spelling in new[] { m.GameId }.Concat(m.Aliases ?? Array.Empty<string>()))
                    Claim(_bySpelling, spelling, index, m, "spelling");
                foreach (string name in new[] { m.Id, m.GameId, m.Name, m.ShortName }
                             .Concat(m.Aliases ?? Array.Empty<string>()).Concat(m.LegacyIds ?? Array.Empty<string>()))
                    Claim(_byLooseName, LooseKey(name), index, m, "loose name");
            }
            Listed = listed;
            Profiles = profiles;
            CommunityLanguages = new HashSet<string>(
                listed.Where(m => m.Translation?.Sources?.Contains(GameTranslation.SourceCommunity, StringComparer.Ordinal) == true)
                      .SelectMany(m => m.Translation.Targets ?? Array.Empty<string>()),
                StringComparer.Ordinal);
        }

        private static void Claim(Dictionary<string, int> map, string key, int index, GameManifest m, string what)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (map.TryGetValue(key, out int owner) && owner != index)
                Logger.Log.Error($"Game manifest {m.Id}: {what} \"{key}\" already belongs to another game; ignored.");
            else map[key] = index;
        }

        /// <summary>A registry over <paramref name="manifests"/> (any status), ordered by their order field.</summary>
        public static GameRegistry FromManifests(IEnumerable<GameManifest> manifests) =>
            new GameRegistry(manifests.Where(m => m != null).OrderBy(m => m.Order).ThenBy(m => m.Id, StringComparer.Ordinal).ToList());

        /// <summary>Lower-case letters and digits only, as tools/games/validate.cjs compares loose names.</summary>
        public static string LooseKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                char l = char.ToLowerInvariant(c);
                if ((l >= 'a' && l <= 'z') || (l >= '0' && l <= '9')) sb.Append(l);
            }
            return sb.ToString();
        }

        /// <summary>The profile for an exact GameId or alias (any case, no trimming); null when unknown.</summary>
        public GameRegionProfile ProfileOf(string gameIdOrAlias) =>
            gameIdOrAlias != null && _bySpelling.TryGetValue(gameIdOrAlias, out int i) ? Profiles[i] : null;

        /// <summary>The listed game for a Config["Game"] value: GameId or alias, any case, padding ignored.</summary>
        public GameManifest Find(string gameIdOrAlias)
        {
            string key = gameIdOrAlias?.Trim();
            return !string.IsNullOrEmpty(key) && _bySpelling.TryGetValue(key, out int i) ? Listed[i] : null;
        }

        /// <summary>The listed game with this wire id ("zzz"; any case); null when none.</summary>
        public GameManifest FindByWireId(string wireId) =>
            wireId != null && _byWireId.TryGetValue(wireId, out int i) ? Listed[i] : null;

        /// <summary>
        /// The listed game any of whose names (id, gameId, name, short name, aliases, legacy ids) matches on letters and
        /// digits: "Honkai: Star Rail", "hsr" and "StarRail" all find Star Rail. For art and colours, never for data.
        /// </summary>
        public GameManifest FindLoose(string name)
        {
            string key = LooseKey(name);
            return key.Length > 0 && _byLooseName.TryGetValue(key, out int i) ? Listed[i] : null;
        }

        /// <summary>The first listed game: what an unknown game borrows its art and colours from.</summary>
        public GameManifest Default => Listed.Count > 0 ? Listed[0] : null;

        /// <summary>Languages some listed game takes community fixes in (the backend's COMMUNITY_LANGS).</summary>
        public IReadOnlyCollection<string> CommunityLanguages { get; }

        /// <summary>
        /// Whether the listed game with exactly this wire id takes community fixes in <paramref name="lang"/>: its
        /// translation.sources lists "community" and its targets the language (the backend's COMMUNITY_TARGETS).
        /// </summary>
        public bool TakesCommunityFixes(string wireId, string lang)
        {
            GameManifest m = FindByWireId(wireId);
            return m != null && string.Equals(m.Id, wireId, StringComparison.Ordinal) && m.Translation?.TakesCommunityFixes(lang) == true;
        }

        /// <summary>Wire ids of the listed games, in UI order.</summary>
        public IEnumerable<string> WireIds => Listed.Select(m => m.Id);

        /// <summary>Whether <paramref name="game"/> shows the one-time display-mode tip before its first start.</summary>
        public static bool ShowsDisplayModeTip(string game) => Current.Find(game)?.Desktop?.DisplayModeTip == true;

        /// <summary>
        /// The spellings of the player's default name in <paramref name="game"/>'s subtitles, which the name chosen in
        /// Settings replaces; an unknown game or a manifest without the field keeps "Traveler". Never allocates.
        /// </summary>
        public static IReadOnlyList<string> PlayerNameTokens(string game)
        {
            string[] tokens = Current.Find(game)?.Desktop?.PlayerNameTokens;
            return tokens != null && tokens.Length > 0 ? tokens : GameDesktop.DefaultPlayerNameTokens;
        }

        /// <summary>Whether <paramref name="game"/>'s manifest says beta (listed, its first pack may not be out yet).</summary>
        public static bool IsBeta(string game) => Current.Find(game)?.Status == GameManifest.StatusBeta;
    }
}
