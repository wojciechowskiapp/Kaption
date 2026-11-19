using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GI_Subtitles.Models;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Themes;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Border = System.Windows.Controls.Border;
using static GI_Test.RenderTestKit;

namespace GI_Test
{
    /// <summary>
    /// What the windows we publish pictures of are filled with, in one place: the games, the version label and the
    /// scene behind the region preview. The site's guide and showcase screenshots (and the shell's review renders) read
    /// it, so a game that goes live or a release that ships changes the pictures without anyone editing a literal.
    /// </summary>
    internal static class RenderFixtures
    {
        /// <summary>The games a player is offered as live: the manifests' status, beta ones left out.</summary>
        public static IReadOnlyList<GameRegionProfile> LiveProfiles =>
            GameRegionProfile.RegisteredProfiles.Where(p => !GameRegistry.IsBeta(p.GameId)).ToList();

        /// <summary>
        /// The version the pictures say: the newest release the What's new entries name, which is the one the
        /// next build of the app carries. It moves with the entries file, not with a literal here.
        /// </summary>
        public static string PreviewVersion
        {
            get
            {
                string path = Path.Combine(RepoRoot(), "GI-Subtitles", "Resources", "WhatsNew", "entries.json");
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var versions = new List<Version>();
                foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
                    if (entry.TryGetProperty("since", out var since) && Version.TryParse(since.GetString(), out var v)) versions.Add(v);
                Assert.IsTrue(versions.Count > 0, "the What's new entries name a release");
                return "v" + versions.Max().ToString(3);
            }
        }

        /// <summary>
        /// Pack versions as the live feed reported them on 19 November 2026 (api.kaption.one/api/app/supported-versions):
        /// the first game installed and current, Star Rail one revision behind, the others not downloaded yet.
        /// A game the table does not know shows as not downloaded, so a new live game still gets a row.
        /// </summary>
        private static readonly Dictionary<string, (string Installed, string Latest)> PackVersions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Genshin"] = ("7.2.0-r12", "7.2.0-r12"),
            ["StarRail"] = ("4.7.0-r2", "4.7.0-r3"),
            ["ZZZ"] = (null, "3.3.0-r8"),
            ["WuWa"] = (null, "3.8.0"),
        };

        /// <summary>One row per live game on Start's packs card, in the picker's order; the first game is the active one.</summary>
        public static SettingsWindow.PackCardRow[] PackRows(Border host, CultureInfo culture, bool downloadingLast = false)
        {
            string L(string key, string fallback) => host.TryFindResource(key) as string ?? fallback;
            var profiles = LiveProfiles;
            return profiles.Select((profile, i) =>
            {
                PackVersions.TryGetValue(profile.GameId, out var v);
                bool downloading = downloadingLast && i == profiles.Count - 1;
                string installed = downloading ? "3.3.0-r2" : v.Installed, latest = v.Latest ?? "1.0.0";
                var (text, brush, isDownload, sub) = SettingsWindow.PackRowStatus(PackStatus.Compare(installed, latest), downloading, i == 0, installed, latest, L, culture);
                return new SettingsWindow.PackCardRow
                {
                    Name = profile.DisplayName, Version = sub, Status = text, StatusBrush = host.TryFindResource(brush) as Brush,
                    Accent = host.TryFindResource(GameLook.AccentBrushKey(profile.GameId)) as Brush ?? new SolidColorBrush(GameLook.AccentOf(profile.GameId)),
                    IsDownload = isDownload, Tooltip = isDownload ? L("shell_Packs_Download_Tip", "") : null,
                };
            }).ToArray();
        }

        /// <summary>The first two live games on the Game and subtitles page's pack list: the first installed, the second with a newer pack.</summary>
        public static List<TranslationPackInfo> PackList()
        {
            var packs = new List<TranslationPackInfo>();
            foreach (var profile in LiveProfiles.Take(2))
            {
                PackVersions.TryGetValue(profile.GameId, out var v);
                bool installed = v.Installed != null && v.Installed == v.Latest;
                packs.Add(new TranslationPackInfo
                {
                    Game = profile.GameId.ToLowerInvariant(), Language = "pl", GameDisplayName = profile.DisplayName, LanguageDisplayName = "Polski",
                    DirectionLabel = "English → Polski", IsActiveTarget = packs.Count == 0, RemoteVersion = v.Latest,
                    Source = installed ? TranslationPackSource.PaidCached : TranslationPackSource.RemoteAvailable,
                    LocalPath = installed ? $@"C:\Users\Gracz\AppData\Roaming\Kaption\{profile.GameId}\TextMapPL.gisub" : null,
                    LocalSize = installed ? 92_901_376 : 0, LocalModifiedAt = DateTime.Now.AddHours(-3), LocalVersion = installed ? v.Installed : null,
                    RemoteSize = 64_487_424, RemoteUnlocked = true,
                });
            }
            return packs;
        }

        /// <summary>
        /// A real game scene from GI-Test/Fixtures/scenes (copies of the pictures the site shows, so the desktop tests read
        /// only their own folder). A missing file fails the render instead of leaving a preview empty.
        /// </summary>
        public static string Scene(string file)
        {
            string path = Path.Combine(RepoRoot(), "GI-Test", "Fixtures", "scenes", file);
            Assert.IsTrue(File.Exists(path), "the scene fixture is in GI-Test/Fixtures/scenes: " + path);
            return path;
        }

        /// <summary>The picture behind the region preview: a real Genshin dialogue with the Polish line above it.</summary>
        public static string DialogueScene() => Scene("en-pl-npc-dialog.jpg");

        /// <summary>
        /// Leaves only the live games in the window's pickers (setup's radios, the game pills): a published picture
        /// shows what a player is offered when the site says four games, never a beta game with no pack yet.
        /// </summary>
        public static void OfferLiveGamesOnly(Window window)
        {
            foreach (string panelName in new[] { "GamePills", "GameRadios" })
            {
                if (window.FindName(panelName) is not Panel panel) continue;
                foreach (var child in panel.Children.OfType<FrameworkElement>().ToList())
                    if (child.Tag is string tag && GameRegistry.IsBeta(tag)) panel.Children.Remove(child);
            }
        }
    }
}
