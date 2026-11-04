using System;
using System.Collections.Generic;
using GI_Subtitles.Services.Community;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>One subtitle tick after the resolver: the text to paint and what "fix this line" records.</summary>
    public readonly struct ComposedSubtitle
    {
        public ComposedSubtitle(string key, string packText, string layered, string header, string content, bool isPrediction)
        {
            Key = key;
            PackText = packText;
            Layered = layered;
            Header = header;
            Content = content;
            IsPrediction = isPrediction;
        }

        /// <summary>The dictionary key that matched; "" for a prediction or no match.</summary>
        public string Key { get; }
        /// <summary>The pack's own text for <see cref="Key"/>.</summary>
        public string PackText { get; }
        /// <summary>With community and own fixes applied, before the player's name goes in.</summary>
        public string Layered { get; }
        public string Header { get; }
        /// <summary>The text to paint.</summary>
        public string Content { get; }
        /// <summary>A chain prediction shown before the line was read.</summary>
        public bool IsPrediction { get; }

        /// <summary>A line the matcher found: the last-shown record takes it, a prediction never.</summary>
        public bool Records => !IsPrediction && !string.IsNullOrEmpty(Key) && !string.IsNullOrEmpty(Layered);
    }

    /// <summary>
    /// The text decisions of <c>MainWindow.UpdateText</c>, pure so they are testable: fixes over the pack, the
    /// prediction while nothing is read, and the player's name last. No allocation unless a name is filled in.
    /// </summary>
    public static class SubtitleComposer
    {
        /// <param name="resolution">The resolver's answer; default when nothing was looked up.</param>
        /// <param name="hasOcrText">The tick read text (the resolver ran, or the text was game UI).</param>
        /// <param name="predicted">The chain prediction waiting to be shown, fixes already applied.</param>
        /// <param name="nameTokens">The game's default player name as the text spells it (GameRegistry.PlayerNameTokens).</param>
        public static ComposedSubtitle Compose(in SubtitleResolution resolution, bool hasOcrText, string predicted,
                                               TranslationLayers layers, string playerName, IReadOnlyList<string> nameTokens = null)
        {
            string key = resolution.Key ?? "";
            string header = resolution.Header ?? "";
            string pack = resolution.Content ?? "";
            string content = pack;
            if (key.Length > 0 && content.Length > 0 && layers != null) content = layers.Resolve(key, content);

            bool prediction = false;
            if (content.Length == 0)
            {
                header = "";
                if (!hasOcrText && !string.IsNullOrEmpty(predicted))
                {
                    content = predicted;
                    prediction = true;
                }
            }
            return new ComposedSubtitle(key, pack, content, header, WithPlayerName(content, playerName, nameTokens), prediction);
        }

        /// <summary>The prediction as it would show once read: the same fixes over the pack.</summary>
        public static string Prediction(TranslationLayers layers, string enText, string translation) =>
            layers == null ? translation : layers.Resolve(enText, translation);

        /// <summary>
        /// The game's default player name in the line ("Traveler", WuWa's "Rover") becomes the player's chosen name.
        /// Without <paramref name="tokens"/> it is Genshin's "Traveler", as before manifests named it.
        /// </summary>
        public static string WithPlayerName(string content, string playerName, IReadOnlyList<string> tokens = null)
        {
            if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(content)) return content;
            foreach (string token in tokens ?? GameDesktop.DefaultPlayerNameTokens)
                if (!string.IsNullOrEmpty(token)) content = content.Replace(token, playerName, StringComparison.Ordinal);
            return content;
        }
    }
}
