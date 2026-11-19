using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using GI_Subtitles.Services.Hints;
using GI_Subtitles.Services.QuickTranslate;
using Microsoft.VisualStudio.TestTools.UnitTesting;
// GI_Test has a Config of its own; the app's is the one the settings live in.
using AppConfig = GI_Subtitles.Core.Config.Config;

namespace GI_Test
{
    /// <summary>
    /// What the hint card keeps in Config.json and what it says: a memory that survives a hand-edited file, the
    /// switch that is on until the player chooses, the one moment that tells a new install from an old one, and a
    /// catalog whose every line exists in both languages.
    /// </summary>
    [TestClass]
    public class HintStateTests
    {
        private static readonly DateTimeOffset Noon = new(new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Local));
        private static readonly string[] Keys = { HintSettings.EnabledKey, HintSettings.StateKey, QuickTranslateArea.ConfigKey };

        /// <summary>Runs <paramref name="test"/> with the keys cleared, then puts back what was saved.</summary>
        private static void WithKeysCleared(Action test)
        {
            var saved = Keys.Where(AppConfig.Has).ToDictionary(k => k, k => AppConfig.Get<JsonNode>(k, null)?.ToJsonString());
            foreach (string k in Keys) AppConfig.Remove(k);
            try { test(); }
            finally
            {
                foreach (string k in Keys) AppConfig.Remove(k);
                foreach (var kv in saved)
                    if (kv.Value != null) AppConfig.Set(kv.Key, JsonNode.Parse(kv.Value));
            }
        }

        [TestMethod]
        public void TheMemory_ComesBackAsItWasSaved()
        {
            var at = Noon.AddHours(3);
            HintState state = HintState.Create("2026-11-18", newInstall: true)
                .WithWelcomeOn("2026-11-18").WithWelcomeOn("2026-11-19")
                .WithKeyUsed(HintCatalog.KeyQuickTranslate)
                .WithTipShown(HintCatalog.TipFixLine.Id, at).WithTipShown(HintCatalog.TipFixLine.Id, at.AddDays(1));

            // Through text, as Config.json holds it.
            HintState back = HintState.FromJson(JsonNode.Parse(state.ToJson().ToJsonString()));
            Assert.AreEqual("2026-11-18", back.Since);
            Assert.AreEqual(HintState.NewInstallWelcomeDays, back.WelcomeLimit);
            CollectionAssert.AreEqual(new[] { "2026-11-18", "2026-11-19" }, back.WelcomeDays.ToArray());
            Assert.IsTrue(back.KeyUsed(HintCatalog.KeyQuickTranslate));
            Assert.IsFalse(back.KeyUsed(HintCatalog.KeyFixLine));
            Assert.AreEqual(new HintTipRecord(2, at.AddDays(1)), back.Tip(HintCatalog.TipFixLine.Id));
            Assert.AreEqual(default, back.Tip(HintCatalog.TipQuickTranslate.Id));

            // Nothing new to remember gives the same object back, so the caller does not write the file again.
            Assert.AreSame(back, back.WithWelcomeOn("2026-11-19"));
            Assert.AreSame(back, back.WithKeyUsed(HintCatalog.KeyQuickTranslate));
        }

        [TestMethod]
        public void ABrokenValue_ReadsAsNoMemory_AndABrokenField_IsSkippedOnItsOwn()
        {
            foreach (string broken in new[]
            {
                "null", "\"left\"", "42", "true", "[]", "{}",
                "{\"since\":20261118}", "{\"since\":\"yesterday\"}", "{\"welcomeDays\":[\"2026-11-18\"]}",
            })
                Assert.IsNull(HintState.FromJson(JsonNode.Parse(broken)), broken);

            // Every field but the birth day is of the wrong kind: the memory is kept, those fields start empty.
            HintState state = HintState.FromJson(JsonNode.Parse(
                "{\"since\":\"2026-11-18\",\"welcomeLimit\":\"three\",\"welcomeDays\":{\"a\":1},\"keysUsed\":\"9005\",\"tips\":[1,2],\"later\":{\"x\":1}}"));
            Assert.IsNotNull(state);
            Assert.AreEqual(HintState.ExistingInstallWelcomeDays, state.WelcomeLimit, "the fewer unasked sheets, when it cannot be told");
            Assert.AreEqual(0, state.WelcomeDays.Count + state.KeysUsed.Count + state.Tips.Count);

            // Bad entries inside good fields are dropped one by one; a count nobody could have reached is cut.
            state = HintState.FromJson(JsonNode.Parse(
                "{\"since\":\"2026-11-18\",\"welcomeLimit\":99,\"welcomeDays\":[\"2026-11-18\",7,\"2026-13-40\",\"2026-11-18\"]," +
                "\"keysUsed\":[9005,\"9007\",-3,9005.5],\"tips\":{\"fix-line\":{\"count\":1,\"last\":\"soon\"},\"quick-translate\":7,\"x\":{\"count\":\"2\"},\"y\":{\"count\":-5}}}"));
            Assert.AreEqual(HintState.NewInstallWelcomeDays, state.WelcomeLimit);
            CollectionAssert.AreEqual(new[] { "2026-11-18" }, state.WelcomeDays.ToArray());
            CollectionAssert.AreEqual(new[] { 9005 }, state.KeysUsed.ToArray());
            Assert.AreEqual(new HintTipRecord(1, null), state.Tip("fix-line"));
            Assert.AreEqual(new HintTipRecord(0, null), state.Tip("y"));
            Assert.AreEqual(2, state.Tips.Count);
        }

        [TestMethod]
        public void Hints_AreOnUntilThePlayerChooses_AndReadingNeverWritesTheKey()
        {
            WithKeysCleared(() =>
            {
                Assert.IsTrue(HintSettings.Enabled);
                HintSettings.Enabled = true;
                Assert.IsFalse(AppConfig.Has(HintSettings.EnabledKey), "on is the absent key: nothing to write");

                HintSettings.Enabled = false; // "Don't show again"
                Assert.IsFalse(HintSettings.Enabled);
                HintSettings.Enabled = true;
                Assert.IsTrue(HintSettings.Enabled);
            });
        }

        [TestMethod]
        public void TheFirstLoad_DecidesNewOrOldInstall_Once_AndKnowsWhatAnOldOneAlreadyUses()
        {
            WithKeysCleared(() =>
            {
                // A new install: three welcome days, decided now and written down.
                HintState first = HintSettings.LoadState(newInstall: true, Noon);
                Assert.AreEqual(HintState.NewInstallWelcomeDays, first.WelcomeLimit);
                Assert.IsTrue(AppConfig.Has(HintSettings.StateKey));
                // Its first line has shown since ("not a new install" by the same test): the decision stands.
                HintState second = HintSettings.LoadState(newInstall: false, Noon.AddDays(1));
                Assert.AreEqual(HintState.NewInstallWelcomeDays, second.WelcomeLimit);
                Assert.AreEqual(first.Since, second.Since);

                HintSettings.SaveState(second.WithKeyUsed(HintCatalog.KeyFixLine));
                Assert.IsTrue(HintSettings.LoadState(newInstall: false, Noon.AddDays(1)).KeyUsed(HintCatalog.KeyFixLine));
            });

            WithKeysCleared(() =>
            {
                // A player from before the card, who has used Ctrl+Q: one welcome sheet, and no Ctrl+Q lesson.
                AppConfig.Set(QuickTranslateArea.ConfigKey, "10,10,300,80");
                HintState state = HintSettings.LoadState(newInstall: false, Noon);
                Assert.AreEqual(HintState.ExistingInstallWelcomeDays, state.WelcomeLimit);
                Assert.IsTrue(state.KeyUsed(HintCatalog.KeyQuickTranslate));
            });

            WithKeysCleared(() =>
            {
                // A value someone broke by hand is replaced by a fresh memory, without a failed read.
                AppConfig.Set(HintSettings.StateKey, "oops");
                Assert.AreEqual(Noon.ToLocalTime().ToString("yyyy-MM-dd"), HintSettings.LoadState(newInstall: false, Noon).Since);
                Assert.IsNotNull(HintState.FromJson(AppConfig.Get<JsonNode>(HintSettings.StateKey, null)));
            });
        }

        [TestMethod]
        public void EveryLineOfTheCard_ExistsInEnglishAndPolish_WithItsShortcutWhereOneIsNamed()
        {
            // ResourceParityTests sees a key only once a dictionary holds it, so a mistyped key here would show
            // English in a Polish app, and a line without its {0} would show the braces to the player.
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            Dictionary<string, string> Load(string file) =>
                XDocument.Load(Path.Combine(RenderTestKit.RepoRoot(), "GI-Subtitles", "Resources", file)).Root.Elements()
                    .Where(el => el.Attribute(x + "Key") != null).ToDictionary(el => (string)el.Attribute(x + "Key"), el => el.Value);

            // A sheet row draws its shortcut beside the label; every other line that names one holds it in its text.
            static bool InText(HintEntry entry) => entry.KeyId != 0 && entry.Kind != HintEntryKind.SheetRow;

            var problems = new List<string>();
            foreach (string file in new[] { "Strings.en-US.xaml", "Strings.pl-PL.xaml" })
            {
                var strings = Load(file);
                void Check(string key, bool namesShortcut)
                {
                    if (!strings.TryGetValue(key, out string text)) problems.Add($"{file}: {key} is missing");
                    else if (text.Contains("{0}") != namesShortcut || text.Contains("{1}"))
                        problems.Add($"{file}: {key} {(namesShortcut ? "must hold {0} for the shortcut" : "names no shortcut and must hold no placeholder")}");
                }
                foreach (HintEntry entry in HintCatalog.Entries)
                {
                    Check(entry.TextKey, InText(entry));
                    if (entry.LeadKey != null) Check(entry.LeadKey, false);
                }
                foreach (HintText label in HintCatalog.Labels) Check(label.Key, false);
            }
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));

            foreach (HintEntry entry in HintCatalog.Entries)
            {
                Assert.AreEqual(InText(entry), entry.Fallback.Contains("{0}"), $"{entry.Id}: the English fallback names the shortcut exactly when the line has one");
                Assert.IsTrue(entry.KeyId == 0 || HintCatalog.KeyIds.Contains(entry.KeyId), $"{entry.Id}: its shortcut is one the bindings are read for");
            }
        }
    }
}
