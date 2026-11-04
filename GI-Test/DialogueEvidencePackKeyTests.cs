using System;
using System.Collections.Generic;
using System.IO;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The matcher hands the resolver pack keys, which merge-textmap cleaned (gender branch, colour tags,
    /// placeholders); the dialogue graph holds raw TextMap text. Dialogue evidence has to recognise both.
    /// </summary>
    [TestClass]
    public class DialogueEvidencePackKeyTests
    {
        private readonly List<string> _tempDirs = new List<string>();

        [TestCleanup]
        public void Cleanup()
        {
            foreach (string dir in _tempDirs)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { /* best-effort */ }
            }
        }

        // Expected values are tools/merge-textmap.cjs cleanString(pickGender(s)).trim(), run on these inputs.
        [TestMethod]
        [DataRow("Give my {M#sister}{F#brother} back!", "Give my sister back!")]
        [DataRow("You can <color=#00E1FFFF>swim</color> right over!", "You can swim right over!")]
        [DataRow("Hello, {NICKNAME}. {PLAYERAVATAR#SEXPRO[INFO_MALE_PRONOUN_HE|INFO_FEMALE_PRONOUN_SHE]} is here.", "Hello, {NICKNAME}.  is here.")]
        [DataRow("First line.\\nSecond line.", "First line.\nSecond line.")]
        [DataRow("<unbreak>Mora</unbreak> #1 only", "Mora 1 only")]
        [DataRow("{F#She}{M#He} said so.", "He said so.")]
        [DataRow("{M#Brother} only", "Brother only")]
        [DataRow("  padded  ", "padded")]
        public void Clean_MatchesMergeTextmap(string raw, string expected)
        {
            Assert.AreEqual(expected, PackKeyText.Clean(raw));
        }

        [TestMethod]
        public void MayDiffer_OnlyForMarkup()
        {
            Assert.IsFalse(PackKeyText.MayDiffer("Plain line, nothing to clean."));
            Assert.IsFalse(PackKeyText.MayDiffer("Line #1"), "'#' is not a letter, so normalizing ignores it anyway");
            Assert.IsTrue(PackKeyText.MayDiffer("A {M#x}{F#y}"));
            Assert.IsTrue(PackKeyText.MayDiffer("A <color=#fff>b</color>"));
            Assert.IsTrue(PackKeyText.MayDiffer("A\\nB"));
        }

        [TestMethod]
        public void GraphLinesWithMarkup_AreVouchedFor_ByTheirPackKey()
        {
            var engine = LoadEngine(
                "Give my {M#sister}{F#brother} back!",
                "You can <color=#00E1FFFF>swim</color> right over!",
                "Over here.\\nQuick!",
                "Nothing to clean here.");

            Assert.IsTrue(engine.CanVouchForLines);
            Assert.IsTrue(engine.IsDialogueLine("Give my sister back!"));
            Assert.IsTrue(engine.IsDialogueLine("You can swim right over!"));
            Assert.IsTrue(engine.IsDialogueLine("Over here.\nQuick!"));
            Assert.IsTrue(engine.IsDialogueLine("Nothing to clean here."));
            Assert.IsTrue(engine.IsDialogueLine("Give my {M#sister}{F#brother} back!"), "the raw text still answers");
            Assert.IsFalse(engine.IsDialogueLine("Give my brother back!"), "the pack keeps the male branch only");
            Assert.IsFalse(engine.IsDialogueLine("Max HP"));
        }

        [TestMethod]
        public void Resolver_PaintsAGenderedGraphLine_WithNoKnownSpeaker()
        {
            const string key = "Give my sister back!";
            var engine = LoadEngine("Give my {M#sister}{F#brother} back!");
            var corpus = new Dictionary<string, string> { [key] = "Oddaj mi siostrę!", ["Max HP"] = "Maks. HP" };
            using var matcher = new OptimizedMatcher(corpus, "EN");
            var resolver = new SubtitleResolver { RequireDialogueEvidence = true };

            Assert.AreEqual("Oddaj mi siostrę!", resolver.Resolve(key, "Aether", engine, matcher, corpus).Content);
            Assert.AreEqual(ResolutionSource.NotDialogue, resolver.Resolve("Max HP", "", engine, matcher, corpus).Source);
        }

        private NormalizedDialogueContext LoadEngine(params string[] lines)
        {
            string dir = Path.Combine(Path.GetTempPath(), "DialogueEvidencePackKeyTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _tempDirs.Add(dir);

            var graph = new Dictionary<string, object>();
            var textMap = new Dictionary<string, string>();
            for (int i = 0; i < lines.Length; i++)
            {
                graph[(i + 1).ToString()] = new { h = i + 1, nh = 0, n = Array.Empty<long>(), rt = "TALK_ROLE_NPC", ri = "npc-a" };
                textMap[(i + 1).ToString()] = lines[i];
            }
            File.WriteAllText(Path.Combine(dir, "DialogGraph.json"), System.Text.Json.JsonSerializer.Serialize(graph));
            File.WriteAllText(Path.Combine(dir, "TextMapEN.json"), System.Text.Json.JsonSerializer.Serialize(textMap));

            var engine = new NormalizedDialogueContext();
            engine.Load(dir, Path.Combine(dir, "TextMapEN.json"));
            return engine;
        }
    }
}
