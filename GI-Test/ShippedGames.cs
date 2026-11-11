using System;
using System.Collections.Generic;
using GI_Subtitles.Services.Games;

namespace GI_Test
{
    /// <summary>
    /// The test run's registry: every embedded manifest, a withdrawn one (games/&lt;id&gt;.json "withdrawn") listed again.
    /// GI-Test is about each game's own profile, look, data and strategies, which stay in the app while a game is
    /// withdrawn; installed for the whole run by <see cref="TestEnvironment"/>, so a withdrawal pull request stays green.
    /// What the app lists is tested from the real embedded registry (<see cref="Embedded"/>) in GameRegistryTests.
    /// </summary>
    internal static class ShippedGames
    {
        /// <summary>Every embedded manifest, a withdrawn one with status live again.</summary>
        public static IReadOnlyList<GameManifest> Manifests()
        {
            var all = new List<GameManifest>(GameManifestLoader.LoadEmbedded());
            foreach (GameManifest m in all)
            {
                if (m.Status == GameManifest.StatusWithdrawn) m.Status = GameManifest.StatusLive;
            }
            return all;
        }

        /// <summary>The registry the app itself builds from the embedded manifests, withdrawn games left out.</summary>
        public static GameRegistry Embedded() => GameRegistry.FromManifests(GameManifestLoader.LoadEmbedded());

        /// <summary>Runs on every shipped game until disposed.</summary>
        public static IDisposable Use() => GameRegistry.Use(GameRegistry.FromManifests(Manifests()));
    }
}
