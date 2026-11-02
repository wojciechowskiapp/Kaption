using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;

namespace GI_Subtitles.Services.Rendering
{
    /// <summary>
    /// The colour of the speaker's name tag: the character's element when the app already knows the speaker (the game's
    /// character data is loaded and the name on screen is one of its characters), otherwise none, and the tag stays
    /// neutral. Never loads anything itself; looking a name up is a dictionary hit.
    /// </summary>
    public static class SpeakerHue
    {
        private static readonly ConditionalWeakTable<CharacterCatalog, Dictionary<string, string>> Elements = new();

        public static Color? For(string gameTag, string speaker)
        {
            if (string.IsNullOrWhiteSpace(gameTag) || string.IsNullOrWhiteSpace(speaker)) return null;
            try
            {
                if (CharacterCatalogService.StateOf(gameTag) != CatalogLoadState.Loaded) return null;
                var task = CharacterCatalogService.GetAsync(gameTag);
                if (!task.IsCompletedSuccessfully || task.Result == null || task.Result.IsEmpty) return null;
                var catalog = task.Result;
                var byName = Elements.GetValue(catalog, Index);
                if (!byName.TryGetValue(Normalize(speaker), out string element) || string.IsNullOrWhiteSpace(element)) return null;
                return ElementPalette.For(catalog.Game, element);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>"Traveler (Hydro)" and "  Chiori " both reduce to the bare name.</summary>
        internal static string Normalize(string name)
        {
            string n = name.Trim();
            int paren = n.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) n = n[..paren];
            return n.Trim().ToLowerInvariant();
        }

        private static Dictionary<string, string> Index(CharacterCatalog catalog)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in catalog.Characters)
            {
                if (string.IsNullOrWhiteSpace(c?.Name)) continue;
                map.TryAdd(Normalize(c.Name), c.Element);
            }
            return map;
        }
    }
}
