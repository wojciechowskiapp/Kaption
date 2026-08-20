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
            public ResolutionSource Source;
        }

        private readonly LRUCache<string, MemoEntry> _memo;

        public SubtitleResolver(int memoCapacity = 100)
        {
            _memo = new LRUCache<string, MemoEntry>(memoCapacity);
        }

        public void ClearCache() => _memo.Clear();

        public SubtitleResolution Resolve(
            string ocrText,
            string detectedNpcName,
            IGameDialogueContext contextEngine,
            OptimizedMatcher matcher,
            Dictionary<string, string> contentDict)
        {
            if (matcher == null)
            {
                Logger.Log.Warn("Matcher not loaded yet, skipping translation");
                return default;
            }

            if (_memo.TryGetValue(ocrText, out MemoEntry cached))
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

            if (source != ResolutionSource.HotCache)
            {
                if (!string.IsNullOrEmpty(detectedNpcName))
                {
                    header = "";
                    try
                    {
                        content = matcher.FindClosestMatch(ocrText, out key) ?? "";
                    }
                    catch (Exception matchEx)
                    {
                        Logger.Log.Error($"FindClosestMatch threw for \"{ocrText}\": {matchEx}");
                        content = "";
                        key = "";
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
                    }
                    catch (Exception matchEx)
                    {
                        Logger.Log.Error($"FindMatchWithHeaderSeparated threw for \"{ocrText}\": {matchEx}");
                        content = "";
                        key = "";
                    }
                    source = ResolutionSource.MatcherHeaderSeparated;
                }
            }

            if (key == null) key = "";

            string res = string.IsNullOrEmpty(header) ? content : (header + "\n\n" + content);

            bool chainAdvanced = false;
            if (!string.IsNullOrEmpty(key) && !isPartialMatch && contextEngine?.IsLoaded == true)
            {
                try
                {
                    contextEngine.OnTextMatched(key, detectedNpcName, contentDict);
                    chainAdvanced = true;
                }
                catch (Exception ctxEx)
                {
                    Logger.Log.Error($"OnTextMatched threw for key=\"{key}\" npc=\"{detectedNpcName}\" (chain prediction may be degraded): {ctxEx}");
                }
            }

            if (Logger.IsDebugEnabled)
            {
                Logger.Log.Debug($"Convert ocrResult for {ocrText}: header={header}, content={content}, key={key}");
            }

            _memo[ocrText] = new MemoEntry
            {
                Result = res,
                Key = key,
                Header = header,
                Content = content,
                Source = source,
            };

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
    }
}
