using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Services.Games
{
    /// <summary>
    /// Reads the game manifests embedded at build time (games/*.json, see GI-Subtitles.csproj). A manifest that does
    /// not parse or misses what the app needs is skipped with an Error: it is a packaging bug, not a user problem.
    /// </summary>
    public static class GameManifestLoader
    {
        /// <summary>LogicalName prefix of the embedded manifests.</summary>
        public const string ResourcePrefix = "Kaption.Games.";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static IReadOnlyList<GameManifest> LoadEmbedded()
        {
            var asm = typeof(GameManifestLoader).Assembly;
            var manifests = new List<GameManifest>();
            foreach (string name in asm.GetManifestResourceNames()
                         .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                using Stream stream = asm.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream);
                GameManifest m = TryParse(reader.ReadToEnd(), name);
                if (m != null) manifests.Add(m);
            }
            if (manifests.Count == 0)
                Logger.Log.Error("No game manifests are embedded in this build; every game falls back to the generic profile.");
            return manifests;
        }

        /// <summary>The manifest, or null (logged) when it doesn't parse or misses what the app reads.</summary>
        public static GameManifest TryParse(string json, string source)
        {
            GameManifest m;
            try
            {
                m = Parse(json);
            }
            catch (Exception ex) when (ex is JsonException || ex is NotSupportedException || ex is ArgumentException)
            {
                Logger.Log.Error($"Game manifest {source} does not parse: {ex.Message}");
                return null;
            }
            var problems = Problems(m);
            if (problems.Count == 0) return m;
            Logger.Log.Error($"Game manifest {source} skipped: {string.Join("; ", problems)}");
            return null;
        }

        public static GameManifest Parse(string json) =>
            JsonSerializer.Deserialize<GameManifest>(json, Options) ?? throw new JsonException("empty manifest");

        /// <summary>What the desktop can't do without; the full contract is tools/games/validate.cjs.</summary>
        public static List<string> Problems(GameManifest m)
        {
            var problems = new List<string>();
            if (m.Schema != 1) problems.Add($"schema {m.Schema} is not supported by this build");
            if (string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.GameId) || string.IsNullOrWhiteSpace(m.Name))
                problems.Add("id, gameId and name are required");
            else if (!string.Equals(m.Id, m.GameId.ToLowerInvariant(), StringComparison.Ordinal))
                problems.Add($"id \"{m.Id}\" is not gameId \"{m.GameId}\" lower-cased");
            if (string.IsNullOrWhiteSpace(m.Status)) problems.Add("status is required");
            if (m.Look == null || string.IsNullOrWhiteSpace(m.Look.Accent)) problems.Add("look.accent is required");
            if (m.IsListed)
            {
                if (m.Detect == null) problems.Add("detect is required for a beta or live game");
                else if (m.Detect.RequireTitle && (m.Detect.WindowTitles == null || m.Detect.WindowTitles.Length == 0))
                    problems.Add("detect.requireTitle needs at least one windowTitles entry");
                if (m.Desktop?.Dialogue == null || m.Desktop.Answer == null) problems.Add("desktop.dialogue and desktop.answer are required for a beta or live game");
            }
            return problems;
        }
    }
}
