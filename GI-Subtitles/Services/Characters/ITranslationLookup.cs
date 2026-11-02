using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GI_Subtitles.Services.Text;
using GI_Subtitles.Services.Translation;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>Exact English → target lookup in the active translation pack. Never fuzzy.</summary>
    public interface ITranslationLookup
    {
        bool TryTranslateExact(string english, out string translated);
    }

    /// <summary>
    /// Exact original-key lookup in the live matcher (punctuation-only variants never answer):
    /// a fuzzy hit would put another item's name on screen. Results are cached per matcher.
    /// </summary>
    public sealed class MatcherTranslationLookup : ITranslationLookup
    {
        private const int MaxCacheEntries = 20000;

        // Device variants in Genshin text: "If you {LAYOUT_MOBILE#Tap}{LAYOUT_PC#Press}{LAYOUT_PS#Press} the…".
        private static readonly Regex LayoutTag = new Regex(@"\{LAYOUT_([A-Z]+)#([^{}]*)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SpaceRuns = new Regex(@"[ \t]{2,}", RegexOptions.Compiled);
        private static readonly Regex SpaceBeforeBreak = new Regex(@"[ \t]+(?=\n)", RegexOptions.Compiled);

        private readonly Func<OptimizedMatcher> _current;
        private readonly object _gate = new object();
        private ConcurrentDictionary<string, string> _cache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private OptimizedMatcher _cacheOwner;
        // Keys with game markup or placeholders, by a hash of their plain text: the catalog stores descriptions
        // plain, so an exact lookup misses them. Hashes, not strings, keep this small; built once per matcher.
        private Dictionary<ulong, int> _plainKeys;
        private OptimizedMatcher _plainKeysOwner;

        public MatcherTranslationLookup(Func<OptimizedMatcher> current)
        {
            _current = current ?? (() => null);
        }

        public bool TryTranslateExact(string english, out string translated)
        {
            translated = null;
            if (string.IsNullOrWhiteSpace(english)) return false;

            OptimizedMatcher matcher;
            try { matcher = _current(); }
            catch { return false; }
            if (matcher == null) return false; // pack still loading; don't cache the miss

            var cache = CacheFor(matcher);
            if (cache.TryGetValue(english, out string cached))
            {
                translated = cached;
                return cached != null;
            }

            string result = null;
            try
            {
                // Catalog text has real line breaks where pack keys keep the game's literal "\n".
                if (matcher.TryGetExactValue(english, out string value) ||
                    (english.IndexOf('\n') >= 0 && matcher.TryGetExactValue(english.Replace("\n", "\\n"), out value)) ||
                    TryPlainKey(matcher, english, out value))
                    result = ResolvePlaceholders(value);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Translation lookup failed: {ex.Message}");
            }

            if (cache.Count < MaxCacheEntries) cache[english] = result;
            translated = result;
            return result != null;
        }

        /// <summary>
        /// The text with markup, Genshin's leading '#', device variants and extra spaces removed: the form the
        /// character catalog stores. With <paramref name="keepPcVariant"/> the PC wording stays instead.
        /// </summary>
        internal static string PlainText(string s, bool keepPcVariant = false)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (HasPlaceholderMark(s)) s = s.Substring(1);
            if (s.IndexOf("{LAYOUT_", StringComparison.Ordinal) >= 0)
                s = LayoutTag.Replace(s, m => keepPcVariant && m.Groups[1].Value == "PC" ? m.Groups[2].Value : "");
            s = GameTextFormatter.StripMarkup(s.Replace("\\n", "\n"));
            s = SpaceBeforeBreak.Replace(SpaceRuns.Replace(s, " "), "");
            return s.Trim();
        }

        /// <summary>A pack value as the screen wants it: PC wording, real line breaks, no '#' marker.</summary>
        internal static string ResolvePlaceholders(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (HasPlaceholderMark(value)) value = value.Substring(1);
            if (value.IndexOf("{LAYOUT_", StringComparison.Ordinal) >= 0)
                value = LayoutTag.Replace(value, m => m.Groups[1].Value == "PC" ? m.Groups[2].Value : "");
            return value.Replace("\\n", "\n");
        }

        // Genshin prefixes text that carries {placeholders} with '#'; "#1[i]%" (a number token) is not that.
        private static bool HasPlaceholderMark(string s) =>
            s.Length > 1 && s[0] == '#' && !char.IsDigit(s[1]) && s.IndexOf('{') > 0;

        private static bool NeedsPlainForm(string key) =>
            key.IndexOf('<') >= 0 || key.IndexOf('{') >= 0 || key.IndexOf("  ", StringComparison.Ordinal) >= 0;

        private bool TryPlainKey(OptimizedMatcher matcher, string english, out string value)
        {
            value = null;
            Dictionary<ulong, int> keys;
            lock (_gate)
            {
                if (!ReferenceEquals(_plainKeysOwner, matcher))
                {
                    _plainKeysOwner = matcher;
                    _plainKeys = BuildPlainKeys(matcher);
                }
                keys = _plainKeys;
            }
            string plain = PlainText(english);
            if (plain.Length == 0 || !keys.TryGetValue(Hash(plain), out int slot)) return false;
            // A hash hit is confirmed against the slot's own key, so a collision can never answer.
            string key = matcher.OriginalKeyAt(slot);
            if (key == null || (PlainText(key) != plain && PlainText(key, keepPcVariant: true) != plain)) return false;
            value = matcher.ValueAt(slot);
            return !string.IsNullOrEmpty(value);
        }

        internal static Dictionary<ulong, int> BuildPlainKeys(OptimizedMatcher matcher)
        {
            var keys = new Dictionary<ulong, int>();
            int n = matcher.EntryCount;
            for (int slot = 0; slot < n; slot++)
            {
                string key = matcher.OriginalKeyAt(slot);
                if (string.IsNullOrEmpty(key) || !NeedsPlainForm(key)) continue;
                string plain = PlainText(key);
                if (plain.Length > 0) keys.TryAdd(Hash(plain), slot);
                // The catalog may drop the device wording or keep the PC one; both forms find the key.
                if (key.IndexOf("{LAYOUT_", StringComparison.Ordinal) >= 0)
                {
                    string pc = PlainText(key, keepPcVariant: true);
                    if (pc.Length > 0) keys.TryAdd(Hash(pc), slot);
                }
            }
            return keys;
        }

        // 64-bit FNV-1a: stable, cheap, and wide enough that the confirmation above almost never rejects.
        private static ulong Hash(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s)
            {
                h = (h ^ c) * 1099511628211UL;
            }
            return h;
        }

        private ConcurrentDictionary<string, string> CacheFor(OptimizedMatcher matcher)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_cacheOwner, matcher))
                {
                    _cacheOwner = matcher;
                    _cache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
                }
                return _cache;
            }
        }
    }
}
