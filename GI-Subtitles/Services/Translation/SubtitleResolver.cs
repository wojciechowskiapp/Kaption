using System;
using System.Collections.Generic;
using GI_Subtitles.Core.Cache;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Which stage produced a resolution. Ordered cheapest-first, matching the
    /// order the cascade tries them.
    /// </summary>
    public enum ResolutionSource
    {
        None = 0,
        MemoCache,
        HotCache,
        Matcher,
        MatcherHeaderSeparated,
        /// <summary>The matcher found a key, but nothing says the read is dialogue (map, menu, item text).</summary>
        NotDialogue,
    }

    /// <summary>
    /// What should be displayed for one OCR read, and how it was arrived at.
    /// </summary>
    public struct SubtitleResolution
    {
        public string Result;
        public string Key;
        public string Header;
        public string Content;
        public ResolutionSource Source;
        public bool IsPartial;
        public bool ChainAdvanced;

        public bool HasContent => !string.IsNullOrEmpty(Content);
    }

    /// <summary>
    /// Turns an OCR read into the text to display, via the hot-cache then
    /// matcher cascade, and advances dialogue chain state on a confirmed match.
    ///
    /// <para>Lifted out of <c>MainWindow.UpdateText</c> so the decision is
    /// separable from the rendering. The benchmark harness drives this class
    /// directly over recorded OCR output, so prediction quality and hot-cache
    /// behaviour are measurable without a running game or a WPF window.</para>
    ///
    /// <para>Not thread-safe: it owns a memo cache and mutates engine chain
    /// state. The app calls it on the UI thread; the harness calls it serially.</para>
    /// </summary>
    public sealed class SubtitleResolver
    {
        private sealed class MemoEntry
        {
            public string Result;
            public string Key;
            public string Header;
            public string Content;
        }

        private readonly LRUCache<(string Text, string Npc), MemoEntry> _memo;
        private OptimizedMatcher _memoMatcher;
        private IGameDialogueContext _memoContext;
        private Dictionary<string, string> _memoDictionary;
        private string _lastConfirmedKey;
        private string _lastConfirmedNpc;

        /// <summary>
        /// Paint a matcher hit only when the dialogue data vouches for it: the key is a graph line, or a speaker the
        /// data knows says something that reads as speech. Map labels, menus and item text are TextMap keys too.
        /// Per game (<c>GameRegionProfile.RequiresDialogueEvidence</c>); without a loaded graph nothing is filtered.
        /// </summary>
        public bool RequireDialogueEvidence { get; set; }

        public SubtitleResolver(int memoCapacity = 100)
        {
            _memo = new LRUCache<(string Text, string Npc), MemoEntry>(memoCapacity);
        }

        public void ClearCache()
        {
            _memo.Clear();
            _memoMatcher = null;
            _memoContext = null;
            _memoDictionary = null;
            _lastConfirmedKey = null;
            _lastConfirmedNpc = null;
        }

        public SubtitleResolution Resolve(
            string ocrText,
            string detectedNpcName,
            IGameDialogueContext contextEngine,
            OptimizedMatcher matcher,
            Dictionary<string, string> contentDict)
        {
            // A language/game/pack reload replaces these objects. Memoized text
            // belongs to that translation stack, not to the lifetime of the UI.
            if (!ReferenceEquals(matcher, _memoMatcher) ||
                !ReferenceEquals(contextEngine, _memoContext) ||
                !ReferenceEquals(contentDict, _memoDictionary))
            {
                ClearCache();
                _memoMatcher = matcher;
                _memoContext = contextEngine;
                _memoDictionary = contentDict;
            }

            if (matcher == null)
            {
                Logger.Log.Warn("Matcher not loaded yet, skipping translation");
                return default;
            }

            detectedNpcName ??= "";
            var memoKey = (ocrText ?? "", detectedNpcName);
            if (_memo.TryGetValue(memoKey, out MemoEntry cached) && !string.IsNullOrEmpty(cached.Content))
            {
                return new SubtitleResolution
                {
                    Result = cached.Result,
                    Key = cached.Key,
                    Header = cached.Header,
                    Content = cached.Content,
                    Source = ResolutionSource.MemoCache,
                    ChainAdvanced = ConfirmMatch(cached.Key, detectedNpcName, contextEngine, contentDict),
                };
            }

            if (!string.IsNullOrEmpty(detectedNpcName) && contextEngine?.IsLoaded == true)
            {
                try
                {
                    contextEngine.PreloadForNpc(detectedNpcName, contentDict);
                }
                catch (Exception preEx)
                {
                    Logger.Log.Error($"PreloadForNpc threw for \"{detectedNpcName}\": {preEx}");
                }
            }

            string key = "";
            string header = "";
            string content = "";
            bool isPartialMatch = false;
            bool matcherFailed = false;
            var source = ResolutionSource.None;

            if (contextEngine?.IsLoaded == true)
            {
                try
                {
                    string normalized = OptimizedMatcher.NormalizeInput(ocrText, matcher.isEng);
                    string hotResult = contextEngine.TryHotCacheMatch(normalized, out string hotKey, out isPartialMatch);
                    if (hotResult != null)
                    {
                        header = "";
                        content = hotResult;
                        key = hotKey;
                        source = ResolutionSource.HotCache;
                        if (Logger.IsDebugEnabled)
                        {
                            Logger.Log.Debug($"HOT CACHE {(isPartialMatch ? "PREFIX" : "HIT")} for \"{ocrText}\": \"{content}\"");
                        }
                    }
                }
                catch (Exception hotEx)
                {
                    Logger.Log.Error($"TryHotCacheMatch threw for \"{ocrText}\": {hotEx}");
                    source = ResolutionSource.None;
                }
            }

            // A previous global miss is still valid for this immutable matcher,
            // but the hot cache above may have acquired new conversation data.
            if (source != ResolutionSource.HotCache && cached != null)
            {
                return new SubtitleResolution
                {
                    Result = cached.Result,
                    Key = cached.Key,
                    Header = cached.Header,
                    Content = cached.Content,
                    Source = ResolutionSource.MemoCache,
                };
            }

            if (source != ResolutionSource.HotCache)
            {
                if (!string.IsNullOrEmpty(detectedNpcName))
                {
                    header = "";
                    try
                    {
                        content = matcher.FindClosestMatch(ocrText, out key, out isPartialMatch) ?? "";
                    }
                    catch (Exception matchEx)
                    {
                        Logger.Log.Error($"FindClosestMatch threw for \"{ocrText}\": {matchEx}");
                        content = "";
                        key = "";
                        matcherFailed = true;
                    }
                    source = ResolutionSource.Matcher;
                    if (Logger.IsDebugEnabled)
                    {
                        Logger.Log.Debug($"Color-detected NPC=\"{detectedNpcName}\" (discarded), body match for \"{ocrText}\": content=\"{content}\"");
                    }
                }
                else
                {
                    try
                    {
                        var matchResult = matcher.FindMatchWithHeaderSeparated(ocrText, out key);
                        header = "";
                        content = matchResult.Content ?? "";
                        isPartialMatch = matchResult.IsPartial;
                    }
                    catch (Exception matchEx)
                    {
                        Logger.Log.Error($"FindMatchWithHeaderSeparated threw for \"{ocrText}\": {matchEx}");
                        content = "";
                        key = "";
                        matcherFailed = true;
                    }
                    source = ResolutionSource.MatcherHeaderSeparated;
                }
            }

            if (key == null) key = "";

            if (RequireDialogueEvidence && source != ResolutionSource.HotCache && content.Length > 0 &&
                contextEngine?.IsLoaded == true && !HasDialogueEvidence(key, detectedNpcName, contextEngine))
            {
                if (Logger.IsDebugEnabled)
                    Logger.Log.Debug($"Not dialogue, not painted: \"{ocrText}\" -> \"{key}\"");
                content = "";
                header = "";
                key = "";
                isPartialMatch = false;
                source = ResolutionSource.NotDialogue;
            }

            string res = string.IsNullOrEmpty(header) ? content : (header + "\n\n" + content);

            bool chainAdvanced = !isPartialMatch &&
                ConfirmMatch(key, detectedNpcName, contextEngine, contentDict);

            if (Logger.IsDebugEnabled)
            {
                Logger.Log.Debug($"Convert ocrResult for {ocrText}: header={header}, content={content}, key={key}");
            }

            // Predictions must be reconsidered against the live context. Cache
            // successful full matches and global misses; retry failures. A miss
            // is only consulted AFTER the hot cache on subsequent UI ticks.
            if (!isPartialMatch && !matcherFailed)
            {
                _memo[memoKey] = new MemoEntry
                {
                    Result = res,
                    Key = key,
                    Header = header,
                    Content = content,
                };
            }

            return new SubtitleResolution
            {
                Result = res,
                Key = key,
                Header = header,
                Content = content,
                Source = source,
                IsPartial = isPartialMatch,
                ChainAdvanced = chainAdvanced,
            };
        }

        private static bool HasDialogueEvidence(string key, string npc, IGameDialogueContext context)
        {
            try
            {
                if (!context.CanVouchForLines) return true;
                return context.IsDialogueLine(key) ||
                       (context.IsKnownSpeaker(npc) && ReadsAsSpeech(key) &&
                        !string.Equals(key.Trim(), npc.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Dialogue evidence check failed for \"{key}\": {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// A sentence ending, or four words or more: a line someone says, not a name or a label ("Max HP",
        /// "Talk to Illuga"). In the Genshin graph only option labels fail it (0.2% of lines).
        /// </summary>
        public static bool ReadsAsSpeech(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            char last = t[t.Length - 1];
            if (last is '.' or '!' or '?' or '…' or '~' or '—' or '-' or '"' or '”' or '」' or '』' or ')' or '*' or '♪')
                return true;
            int words = 0;
            bool inWord = false;
            foreach (char c in t)
            {
                bool letter = char.IsLetterOrDigit(c);
                if (letter && !inWord) words++;
                inWord = letter || (inWord && c is '\'' or '’' or '-');
            }
            return words >= 4;
        }

        private bool ConfirmMatch(string key, string npc, IGameDialogueContext context,
            Dictionary<string, string> dictionary)
        {
            if (string.IsNullOrEmpty(key) || context?.IsLoaded != true ||
                (key == _lastConfirmedKey && npc == _lastConfirmedNpc))
                return false;

            try
            {
                context.OnTextMatched(key, npc, dictionary);
                _lastConfirmedKey = key;
                _lastConfirmedNpc = npc;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log.Error($"OnTextMatched threw for key=\"{key}\" npc=\"{npc}\" (chain prediction may be degraded): {ex}");
                return false;
            }
        }
    }
}
