using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Core.Input;
using GI_Subtitles.Models;
using GI_Subtitles.Services.Community;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.QuickTranslate;
using GI_Subtitles.Services.Translation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>A fresh folder under %TEMP% per test, so nothing touches a real %APPDATA%\Kaption.</summary>
    internal sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kaption-tests", "fixline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        /// <summary>Temp files the atomic writer may have left next to its targets.</summary>
        public string[] Leftovers() => Directory.GetFiles(Path, "*.tmp");

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public TranslationLayers Layers(string game = "Genshin", string wire = "genshin", string lang = "pl") =>
            new TranslationLayers(game, wire, lang, File($"overrides.{lang}.json"), File($"community.{lang}.json"));
    }

    internal sealed class FakeCommunityApi : ICommunityApi
    {
        public readonly List<CommunitySuggestionRequest> Submitted = new List<CommunitySuggestionRequest>();
        public readonly List<string> SinceAsked = new List<string>();
        public Func<CommunityOverlayPage> Overlay = () => CommunityOverlayPage.Empty;
        public Exception SubmitError;

        public Task SubmitSuggestionAsync(string sessionJwt, CommunitySuggestionRequest body, CancellationToken ct)
        {
            if (SubmitError != null) throw SubmitError;
            Submitted.Add(body);
            return Task.CompletedTask;
        }

        public Task<CommunityOverlayPage> GetCommunityOverlayAsync(string sessionJwt, string game, string lang, string since, CancellationToken ct)
        {
            SinceAsked.Add(since);
            return Task.FromResult(Overlay());
        }
    }

    internal sealed class ScriptedSender : ISuggestionSender
    {
        public readonly Queue<SendResult> Results = new Queue<SendResult>();
        public readonly List<PendingSuggestion> Calls = new List<PendingSuggestion>();

        public Task<SendResult> SendAsync(PendingSuggestion suggestion, CancellationToken ct)
        {
            Calls.Add(suggestion);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : SendResult.Sent);
        }
    }

    // ═══ Layers: pack, then community, then the player's own fix ═══════════════════════════════════════════════

    [TestClass]
    public class TranslationLayersTests
    {
        private static CommunityEntry Accepted(string en, string target, string at) => new CommunityEntry(en, target, at);

        private static UserFix Fix(string en, string text, int minute = 0) =>
            new UserFix(en, text, "was", "Paimon", "", new DateTimeOffset(2026, 11, 3, 12, minute, 0, TimeSpan.Zero));

        [TestMethod]
        public void Precedence_PackThenCommunityThenUser()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            layers.EnsureLoaded();
            const string en = "Let's go!";
            Assert.AreEqual("Ruszajmy!", layers.Resolve(en, "Ruszajmy!"), "nothing loaded: the pack");

            layers.MergeCommunity(new[] { Accepted(en, "Chodźmy!", "2026-11-01T10:00:00Z") });
            Assert.AreEqual("Chodźmy!", layers.Resolve(en, "Ruszajmy!"), "community over the pack");
            Assert.AreEqual("Chodźmy!", layers.ResolveShared(en, "Ruszajmy!"));

            layers.SaveUserFix(Fix(en, "No to w drogę!"));
            Assert.AreEqual("No to w drogę!", layers.Resolve(en, "Ruszajmy!"), "the player's fix over both");
            Assert.AreEqual("Chodźmy!", layers.ResolveShared(en, "Ruszajmy!"), "shared text ignores the player's own fix");

            Assert.IsTrue(layers.RemoveUserFix(en));
            Assert.AreEqual("Chodźmy!", layers.Resolve(en, "Ruszajmy!"), "removing the fix brings the community text back");
            Assert.IsFalse(layers.RemoveUserFix(en), "nothing left to remove");

            layers.MergeCommunity(new[] { Accepted(en, "", "2026-11-02T10:00:00Z") });
            Assert.AreEqual("Ruszajmy!", layers.Resolve(en, "Ruszajmy!"), "an empty target takes the line out of the overlay");
        }

        [TestMethod]
        public void UserFix_AppliesWithoutCommunity_AndOtherLinesKeepThePack()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            layers.SaveUserFix(Fix("Hello.", "Cześć."));
            Assert.AreEqual("Cześć.", layers.Resolve("Hello.", "Witaj."));
            Assert.AreEqual("Do zobaczenia.", layers.Resolve("Bye.", "Do zobaczenia."));
            Assert.AreEqual("pack", layers.Resolve("", "pack"));
            Assert.AreEqual("pack", layers.Resolve(null, "pack"));
        }

        [TestMethod]
        public void Keys_MatchExactly_OrTrimmed_ButNeverFuzzy()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            layers.MergeCommunity(new[] { Accepted("Hello there.", "No hej.", "2026-11-01T10:00:00Z") });
            layers.SaveUserFix(Fix("  Watch out!", "Uważaj!"));
            Assert.AreEqual("No hej.", layers.Resolve("Hello there. ", "Witaj."), "a pack key with a trailing space still finds the trimmed entry");
            Assert.AreEqual("Uważaj!", layers.Resolve("Watch out!", "Ostrożnie!"), "stored trimmed");
            Assert.AreEqual("Uważaj!", layers.Resolve("Watch out!\n", "Ostrożnie!"));
            Assert.AreEqual("Witaj.", layers.Resolve("Hello there", "Witaj."), "no fuzzy matching");
            Assert.AreEqual("Witaj.", layers.Resolve("hello there.", "Witaj."), "case matters");
        }

        [TestMethod]
        public void TrimmedLookup_WorksWithOnlyOneLayerFilled()
        {
            using var dir = new TempFolder();
            var community = dir.Layers();
            community.MergeCommunity(new[] { Accepted("Only community.", "Tylko społeczność.", "2026-11-01T10:00:00Z") });
            Assert.AreEqual("Tylko społeczność.", community.Resolve(" Only community.", "pack"));

            using var other = new TempFolder();
            var user = other.Layers();
            user.SaveUserFix(Fix("Only mine.", "Tylko moja."));
            Assert.AreEqual("Tylko moja.", user.Resolve("Only mine.\t", "pack"));
        }

        [TestMethod]
        public void Merge_OnAFreshInstance_AlsoPublishesTheCachedOverlay()
        {
            using var dir = new TempFolder();
            dir.Layers().MergeCommunity(new[] { Accepted("E.", "E po polsku.", "2026-11-03T07:00:00Z") });
            var fresh = dir.Layers();
            Assert.AreEqual(0, fresh.MergeCommunity(new[] { Accepted("E.", "E po polsku.", "2026-11-03T07:00:00Z") }), "nothing new");
            Assert.AreEqual("E po polsku.", fresh.Resolve("E.", "pack"), "but the cache it read is in use");
        }

        [TestMethod]
        public void Resolve_AllocatesNothing_OnHitMissOrTrimmedHit()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            layers.MergeCommunity(new[] { Accepted("Community line.", "Linia społeczności.", "2026-11-01T10:00:00Z") });
            layers.SaveUserFix(Fix("Own line.", "Moja linia."));
            string[] keys = { "Own line.", "Community line.", "Pack line.", "Community line. " };
            for (int i = 0; i < 200; i++) layers.Resolve(keys[i % keys.Length], "pack"); // JIT and first-call caches

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20_000; i++) layers.Resolve(keys[i % keys.Length], "pack");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated, "the subtitle tick looks fixes up without allocating");
        }

        [TestMethod]
        public void MergeCommunity_NewestWins_AndSinceIsTheNewestAcceptedAt()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            int changed = layers.MergeCommunity(new[]
            {
                Accepted("A.", "Nowsze A.", "2026-11-02T08:00:00Z"),
                Accepted("A.", "Starsze A.", "2026-11-01T08:00:00Z"),
                Accepted("B.", "B po polsku.", "2026-11-01T09:00:00Z"),
            });
            Assert.AreEqual(2, layers.CommunityCount);
            Assert.IsTrue(changed >= 2);
            Assert.AreEqual("Nowsze A.", layers.Resolve("A.", "pack"), "applied oldest first, so the newer acceptance wins");
            Assert.AreEqual("2026-11-02T08:00:00Z", layers.CommunitySince);

            Assert.AreEqual(0, layers.MergeCommunity(new[] { Accepted("B.", "B po polsku.", "2026-11-01T09:00:00Z") }), "same text: no change");
            Assert.AreEqual("2026-11-02T08:00:00Z", layers.CommunitySince, "an older timestamp never moves the cursor back");
        }

        [TestMethod]
        public void SavedLayers_LoadIntoANewInstance()
        {
            using var dir = new TempFolder();
            var first = dir.Layers();
            first.MergeCommunity(new[] { Accepted("C.", "C po polsku.", "2026-11-03T07:00:00Z") });
            first.SaveUserFix(Fix("D.", "D po mojemu."));

            var second = dir.Layers();
            Assert.AreEqual("D.", second.Resolve("D.", "D."), "not loaded yet: the pack");
            second.EnsureLoaded();
            Assert.AreEqual("D po mojemu.", second.Resolve("D.", "D."));
            Assert.AreEqual("C po polsku.", second.Resolve("C.", "C."));
            Assert.AreEqual("2026-11-03T07:00:00Z", second.CommunitySince);
            var fix = second.UserFixes.Single();
            Assert.AreEqual("Paimon", fix.Speaker);
            Assert.AreEqual("was", fix.Was);
        }

        [TestMethod]
        public void UserFixes_NewestFirst_AndChangedFiresOnEveryWrite()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            int changed = 0;
            layers.Changed += (s, e) => changed++;
            layers.SaveUserFix(Fix("Old.", "Stare.", minute: 1));
            layers.SaveUserFix(Fix("New.", "Nowe.", minute: 2));
            CollectionAssert.AreEqual(new[] { "New.", "Old." }, layers.UserFixes.Select(f => f.En).ToArray());
            layers.RemoveUserFix("Old.");
            Assert.AreEqual(3, changed);
            Assert.AreEqual(1, layers.UserFixCount);
        }

        [TestMethod]
        public void QuickTranslate_FixGoesThroughTheSameCleanupAsThePack()
        {
            const string hsr = "Deals Ice DMG equal to 1[i]% of March 7th's ATK to a single target enemy.";
            var matcher = new GI_Subtitles.Services.Translation.OptimizedMatcher(new Dictionary<string, string>
            {
                ["Elemental Skill"] = "Umiejętność żywiołu",
                [hsr] = "Zadaje obrażenia Lodowe równe 1[i]% ATK March 7th jednemu wrogowi.",
            }, "EN");
            var probe = QuickTranslateSegmenter.ProbeWith(matcher);
            string[] screen = { "Elemental Skill", "Deals Ice DMG equal to 54% of March 7th's ATK to a single target enemy." };

            using var dir = new TempFolder();
            var layers = dir.Layers();
            var plain = QuickTranslateSegmenter.Segment(screen, layers.Over(probe));
            Assert.AreEqual("Zadaje obrażenia Lodowe równe 54% ATK March 7th jednemu wrogowi.", plain[1].Translation, "no fixes: the pack");

            layers.MergeCommunity(new[] { Accepted(hsr, "Zadaje <i>lodowe</i> obrażenia równe 1[i]% ATK March 7th\\njednemu wrogowi.", "2026-11-01T10:00:00Z") });
            var fixedSegs = QuickTranslateSegmenter.Segment(screen, layers.Over(probe));
            Assert.AreEqual("Umiejętność żywiołu", fixedSegs[0].Translation);
            Assert.AreEqual("Zadaje lodowe obrażenia równe 54% ATK March 7th\njednemu wrogowi.", fixedSegs[1].Translation,
                "markup stripped, the escaped break made real, the number filled from the screen");
            Assert.AreEqual(hsr, fixedSegs[1].EnglishKey);
            Assert.IsNull(layers.Over(null));
        }

        [TestMethod]
        public void Registry_WireIdsAndLanguagesAreNormalised()
        {
            Assert.AreEqual("genshin", TranslationLayerRegistry.WireGameOf("Genshin"));
            Assert.AreEqual("starrail", TranslationLayerRegistry.WireGameOf("StarRail"));
            Assert.AreEqual("zzz", TranslationLayerRegistry.WireGameOf("ZZZ"));
            Assert.AreEqual("zzz", TranslationLayerRegistry.WireGameOf("Zenless"), "an alias maps to the game's wire id");
            Assert.AreEqual("pl", TranslationLayerRegistry.LangOf("PL"));
            Assert.AreEqual("pl", TranslationLayerRegistry.LangOf(" pl "));
            Assert.AreEqual("pl", TranslationLayerRegistry.LangOf("..\\pl"), "never a path");
            Assert.AreEqual("pl", TranslationLayerRegistry.LangOf(null));
        }
    }

    // ═══ Files: atomic writes, damage tolerated ════════════════════════════════════════════════════════════════

    [TestClass]
    public class FixLayerFileTests
    {
        private static UserFix Fix(string en, string text) => new UserFix(en, text, "", "", "", DateTimeOffset.UtcNow);

        [TestMethod]
        public void AtomicWrite_CreatesAndReplaces_WithoutLeavingTempFiles()
        {
            using var dir = new TempFolder();
            string path = dir.File("sub/overrides.pl.json");
            AtomicFile.WriteAllText(path, "{\"a\":1}");
            Assert.AreEqual("{\"a\":1}", File.ReadAllText(path), "creates the folder and the file");
            AtomicFile.WriteAllText(path, "{\"a\":2,\"pl\":\"żółć\"}");
            Assert.AreEqual("{\"a\":2,\"pl\":\"żółć\"}", File.ReadAllText(path), "replaces, UTF-8");
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length);
            CollectionAssert.AreEqual(new byte[] { (byte)'{' }, File.ReadAllBytes(path).Take(1).ToArray(), "no BOM");
        }

        [TestMethod]
        public void AtomicWrite_WhenTheTargetIsHeld_KeepsTheOldFileAndCleansUp()
        {
            using var dir = new TempFolder();
            string path = dir.File("overrides.pl.json");
            File.WriteAllText(path, "old");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var error = Assert.Throws<Exception>(() => AtomicFile.WriteAllText(path, "new"));
                Assert.IsTrue(error is IOException || error is UnauthorizedAccessException, error.GetType().Name);
            }
            Assert.AreEqual("old", File.ReadAllText(path), "the previous file survives a failed replace");
            Assert.AreEqual(0, dir.Leftovers().Length, "the temp file is removed");
        }

        [TestMethod]
        public void CorruptOverrides_LoadEmpty_KeepABackup_AndTheNextSaveWritesValidJson()
        {
            using var dir = new TempFolder();
            string path = dir.File("overrides.pl.json");
            File.WriteAllText(path, "{ \"overrides\": [ { \"en\": \"Hi\", \"text\": ");
            var layers = dir.Layers();
            layers.EnsureLoaded();
            Assert.AreEqual(0, layers.UserFixCount);
            Assert.IsTrue(File.Exists(path + ".bad"), "the damaged file is kept aside");
            StringAssert.StartsWith(File.ReadAllText(path + ".bad"), "{ \"overrides\"");

            layers.SaveUserFix(Fix("Hi", "Hej"));
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual(1, doc.RootElement.GetProperty("schema").GetInt32());
            Assert.AreEqual("Hej", doc.RootElement.GetProperty("overrides")[0].GetProperty("text").GetString());
            StringAssert.StartsWith(File.ReadAllText(path + ".bad"), "{ \"overrides\"", "the backup is not overwritten by a good save");
        }

        [TestMethod]
        public void AFileSavedWithABom_StillLoads()
        {
            using var dir = new TempFolder();
            string path = dir.File("overrides.pl.json");
            File.WriteAllText(path, "{\"overrides\":[{\"en\":\"Hi.\",\"text\":\"Hej.\"}]}", new System.Text.UTF8Encoding(true));
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path).Take(3).ToArray(), "the BOM is there");
            var layers = dir.Layers();
            layers.EnsureLoaded();
            Assert.AreEqual("Hej.", layers.Resolve("Hi.", "pack"));
            Assert.IsFalse(File.Exists(path + ".bad"), "not treated as damaged");
        }

        [TestMethod]
        public void ASecondDamagedFile_DoesNotOverwriteTheFirstBackup()
        {
            using var dir = new TempFolder();
            string path = dir.File("community.pl.json");
            File.WriteAllText(path, "first damage {");
            dir.Layers().EnsureLoaded();
            File.WriteAllText(path, "second damage {");
            dir.Layers().EnsureLoaded();
            Assert.AreEqual("first damage {", File.ReadAllText(path + ".bad"));
            Assert.AreEqual(1, Directory.GetFiles(dir.Path, "community.pl.json.*.bad").Length, "the later one is kept beside it");
        }

        [TestMethod]
        public void DamagedEntries_AreSkipped_TheRestLoads()
        {
            using var dir = new TempFolder();
            File.WriteAllText(dir.File("overrides.pl.json"),
                "{\"schema\":1,\"overrides\":[{\"en\":\"Good.\",\"text\":\"Dobrze.\"},{\"en\":\"No text.\"},{\"en\":5,\"text\":\"x\"},\"junk\",{\"en\":\"Also good.\",\"text\":\"Też dobrze.\",\"savedAt\":\"not a date\"}]}");
            File.WriteAllText(dir.File("community.pl.json"),
                "{\"since\":\"2026-11-01T00:00:00Z\",\"entries\":[{\"en\":\"C.\",\"target\":\"C po polsku.\"},{\"en\":\"\",\"target\":\"x\"},{\"target\":\"y\"}]}");
            var layers = dir.Layers();
            layers.EnsureLoaded();
            Assert.AreEqual(2, layers.UserFixCount);
            Assert.AreEqual("Też dobrze.", layers.Resolve("Also good.", "pack"));
            Assert.AreEqual(1, layers.CommunityCount);
            Assert.AreEqual("2026-11-01T00:00:00Z", layers.CommunitySince);
        }

        [TestMethod]
        public void AFileSavedInAnAnsiEditor_IsSetAside_AndFixesStillSave()
        {
            using var dir = new TempFolder();
            string path = dir.File("overrides.pl.json");
            // "Dziękuję" as Notepad saves it in the Polish ANSI code page (cp1250 'ę' is 0xEA, as Latin-1 'ê').
            byte[] ansi = System.Text.Encoding.Latin1.GetBytes("{\"overrides\":[{\"en\":\"Thanks.\",\"text\":\"Dzi\u00EAkuj\u00EA.\"}]}");
            File.WriteAllBytes(path, ansi);
            using (var parsed = JsonDocument.Parse(ansi))
                Assert.AreEqual(JsonValueKind.Object, parsed.RootElement.ValueKind, "the parser alone would accept it");

            var layers = dir.Layers();
            layers.EnsureLoaded();
            Assert.IsTrue(layers.IsLoaded);
            Assert.AreEqual(0, layers.UserFixCount, "unreadable text is damage, not fixes");
            CollectionAssert.AreEqual(ansi, File.ReadAllBytes(path + ".bad"), "kept byte for byte");
            Assert.IsFalse(File.Exists(path), "moved aside, so the next launch does not find it again");

            layers.SaveUserFix(Fix("Thanks.", "Dziękuję."));
            Assert.AreEqual("Dziękuję.", dir.Layers().UserFixesAfterLoad().Single().Text);
        }

        [TestMethod]
        public void ABrokenEscape_SkipsOnlyWhatItBreaks()
        {
            // Half a surrogate pair parses as JSON, but reading the string throws, and so does a lookup that has to
            // decode such a property name (one that starts like the name asked for).
            using (var raw = JsonDocument.Parse("{\"text\":\"a \\ud83d\",\"en\":\"Hi.\",\"e\\ud83d\":1}"))
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => raw.RootElement.GetProperty("text").GetString());
                Assert.ThrowsExactly<InvalidOperationException>(() => raw.RootElement.TryGetProperty("en", out _));
            }

            using var dir = new TempFolder();
            File.WriteAllText(dir.File("overrides.pl.json"),
                "{\"overrides\":[{\"en\":\"Broken.\",\"text\":\"Zepsute \\ud83d.\"}," +
                "{\"x\\ud83d\":1,\"en\":\"Odd name.\",\"text\":\"Dziwna nazwa.\"}," +
                "{\"en\":\"Clashing name.\",\"text\":\"Kolizja.\",\"e\\ud83d\":1}," +
                "{\"en\":\"Good.\",\"text\":\"Dobre.\"}]}");
            File.WriteAllText(dir.File("community-queue.json"),
                "{\"items\":[{\"id\":\"a\",\"game\":\"genshin\",\"lang\":\"pl\",\"enText\":\"\\udc00\",\"suggestedText\":\"x\",\"createdAt\":\"2026-11-03T18:00:00Z\"}," +
                "{\"id\":\"b\",\"game\":\"genshin\",\"lang\":\"pl\",\"enText\":\"Fine.\",\"suggestedText\":\"Dobrze.\",\"createdAt\":\"2026-11-03T18:00:00Z\"}]}");

            var layers = dir.Layers();
            layers.EnsureLoaded();
            Assert.IsTrue(layers.IsLoaded, "the file is not damage as a whole");
            Assert.AreEqual(2, layers.UserFixCount, "only the entries that cannot be read are skipped");
            Assert.AreEqual("Dziwna nazwa.", layers.Resolve("Odd name.", "pack"), "a broken name nobody asks for is harmless");
            Assert.AreEqual("pack", layers.Resolve("Clashing name.", "pack"));
            Assert.AreEqual("Dobre.", layers.Resolve("Good.", "pack"));
            layers.SaveUserFix(Fix("New.", "Nowe."));
            Assert.AreEqual(3, dir.Layers().UserFixesAfterLoad().Count);

            var queue = new SuggestionQueue(dir.File("community-queue.json"));
            Assert.AreEqual("Fine.", queue.Items.Single().EnText, "the queue reads past it too");
        }

        [TestMethod]
        public void ADamagedFile_IsSetAsideOnce_NotOnEveryLaunch()
        {
            using var dir = new TempFolder();
            File.WriteAllText(dir.File("overrides.pl.json"), "{ broken");
            for (int launch = 0; launch < 3; launch++) dir.Layers().EnsureLoaded();
            Assert.AreEqual(1, Directory.GetFiles(dir.Path, "overrides.pl.json*.bad").Length);
            Assert.AreEqual("{ broken", File.ReadAllText(dir.File("overrides.pl.json.bad")));
        }

        [TestMethod]
        public void ADamagedFileThatCannotBeMovedYet_IsNotOverwritten()
        {
            using var dir = new TempFolder();
            string path = dir.File("overrides.pl.json");
            File.WriteAllText(path, "{ hand edited, not finished");
            var layers = dir.Layers();
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                layers.EnsureLoaded();
                Assert.IsFalse(layers.IsLoaded, "readable but not movable: left for later");
                Assert.ThrowsExactly<IOException>(() => layers.SaveUserFix(Fix("Hi.", "Hej.")));
                Assert.AreEqual("{ hand edited, not finished", File.ReadAllText(path));
            }
            layers.SaveUserFix(Fix("Hi.", "Hej."));
            Assert.AreEqual("{ hand edited, not finished", File.ReadAllText(path + ".bad"), "set aside first, then saved");
            Assert.AreEqual("Hej.", dir.Layers().UserFixesAfterLoad().Single().Text);
        }

        [TestMethod]
        public void AReplaceThatLosesTheTarget_KeepsTheNewCopy_AndTheNextLoadRestoresIt()
        {
            using var dir = new TempFolder();
            string path = dir.File("overrides.pl.json");
            byte[] next = LayerFiles.Build(w =>
            {
                w.WriteStartArray("overrides");
                w.WriteStartObject();
                w.WriteString("en", "Kept.");
                w.WriteString("text", "Zachowane.");
                w.WriteEndObject();
                w.WriteEndArray();
            });
            // As after ERROR_UNABLE_TO_MOVE_REPLACEMENT: no file at the target, and the new copy cannot be moved there.
            Directory.CreateDirectory(path);
            var error = Assert.Throws<Exception>(() => AtomicFile.WriteAllBytes(path, next));
            Assert.IsTrue(error is IOException || error is UnauthorizedAccessException, error.GetType().Name);
            string kept = dir.Leftovers().Single();
            CollectionAssert.AreEqual(next, File.ReadAllBytes(kept), "the only copy is not deleted");

            Directory.Delete(path);
            File.WriteAllText(path + ".0badf00d.tmp", "older unfinished save");
            File.SetLastWriteTimeUtc(path + ".0badf00d.tmp", DateTime.UtcNow.AddMinutes(-5));
            Assert.AreEqual("Zachowane.", dir.Layers().UserFixesAfterLoad().Single().Text, "restored on the next load");
            Assert.AreEqual(0, dir.Leftovers().Length, "the older leftover is cleared too");
        }

        [TestMethod]
        public void ASuccessfulSave_ClearsTempFilesOfEarlierSavesThatNeverFinished()
        {
            using var dir = new TempFolder();
            string path = dir.File("community.pl.json");
            File.WriteAllText(path, "{}");
            File.WriteAllText(path + ".1234abcd.tmp", "crashed mid-save");
            File.WriteAllText(dir.File("overrides.pl.json.1234abcd.tmp"), "another file's");
            AtomicFile.WriteAllText(path, "{\"a\":1}");
            Assert.IsFalse(File.Exists(path + ".1234abcd.tmp"));
            Assert.IsTrue(File.Exists(dir.File("overrides.pl.json.1234abcd.tmp")), "only this file's leftovers");
        }

        [TestMethod]
        public void AFileThatCannotBeRead_IsNeverOverwritten()
        {
            using var dir = new TempFolder();
            string path = dir.File("overrides.pl.json");
            var original = dir.Layers();
            original.SaveUserFix(Fix("Kept.", "Zachowane."));

            var layers = dir.Layers();
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                layers.EnsureLoaded();
                Assert.AreEqual(0, layers.UserFixCount, "could not read it yet");
                Assert.ThrowsExactly<IOException>(() => layers.SaveUserFix(Fix("New.", "Nowe.")));
            }
            layers.SaveUserFix(Fix("New.", "Nowe."));
            Assert.AreEqual("Zachowane.", layers.Resolve("Kept.", "pack"), "the earlier fix was read back before the write");
            Assert.AreEqual("Nowe.", layers.Resolve("New.", "pack"));
            Assert.AreEqual(2, dir.Layers().UserFixesAfterLoad().Count);
        }
    }

    internal static class LayersTestExtensions
    {
        public static IReadOnlyList<UserFix> UserFixesAfterLoad(this TranslationLayers layers)
        {
            layers.EnsureLoaded();
            return layers.UserFixes;
        }
    }

    // ═══ Community overlay: fetched with the dictionary sync, a failure keeps the cache ═══════════════════════════

    [TestClass]
    public class CommunityOverlaySyncTests
    {
        private static CommunityOverlayPage Page(bool full, string until, params CommunityEntry[] entries) =>
            new CommunityOverlayPage(entries, until, full);

        [TestMethod]
        public async Task Refresh_FirstFull_ThenDeltasFromTheServersUntil()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            var api = new FakeCommunityApi
            {
                Overlay = () => Page(true, "2026-11-03T08:58:00.000Z",
                    new CommunityEntry("Hi.", "Hej.", "2026-11-03T09:00:00Z"),
                    new CommunityEntry("Old.", "Stare.", "2026-10-01T09:00:00Z")),
            };
            Assert.AreEqual(2, await CommunitySync.RefreshOverlayAsync(api, "jwt", layers, CancellationToken.None));
            Assert.IsNull(api.SinceAsked[0], "first fetch: everything");
            Assert.AreEqual("Hej.", layers.Resolve("Hi.", "Cześć."));
            Assert.AreEqual("2026-11-03T08:58:00.000Z", layers.CommunitySince, "the server's until, not the newest acceptance");

            api.Overlay = () => Page(false, "2026-11-03T10:58:00.000Z",
                new CommunityEntry("Bye.", "Na razie.", "2026-11-03T10:00:00Z"),
                new CommunityEntry("Old.", "", "2026-11-03T10:30:00Z"));
            Assert.AreEqual(2, await CommunitySync.RefreshOverlayAsync(api, "jwt", layers, CancellationToken.None));
            Assert.AreEqual("2026-11-03T08:58:00.000Z", api.SinceAsked[1], "then only what changed since");
            Assert.AreEqual("Stare?", layers.Resolve("Old.", "Stare?"), "a withdrawn line leaves the overlay");
            Assert.AreEqual(2, dir.Layers().CommunityAfterLoad(), "Hi. and Bye., on disk too");
            Assert.AreEqual("2026-11-03T10:58:00.000Z", dir.Layers().SinceAfterLoad());
        }

        [TestMethod]
        public void AFullPage_ReplacesTheWholeCache()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            layers.ApplyOverlay(Page(false, null, new CommunityEntry("Gone.", "Było.", "2026-11-01T00:00:00Z")));
            int changed = layers.ApplyOverlay(Page(true, "2026-11-03T00:00:00.000Z", new CommunityEntry("Kept.", "Jest.", "2026-11-02T00:00:00Z")));
            Assert.AreEqual(2, changed, "one line added, one dropped");
            Assert.AreEqual("pack", layers.Resolve("Gone.", "pack"));
            Assert.AreEqual("Jest.", layers.Resolve("Kept.", "pack"));
        }

        [TestMethod]
        public async Task Refresh_Failure_KeepsTheCache()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            layers.MergeCommunity(new[] { new CommunityEntry("Hi.", "Hej.", "2026-11-03T09:00:00Z") });
            string before = File.ReadAllText(layers.CommunityFilePath);

            foreach (var error in new Exception[]
            {
                new ApiUnavailableException("offline", new System.Net.Http.HttpRequestException("dns")),
                new ApiValidationException(HttpStatusCode.NotFound, new ApiError()),
                new ApiValidationException(HttpStatusCode.BadRequest, new ApiError { Error = "invalid_request" }),
                new UnauthorizedException("expired"),
                new ApiException("bad body"),
            })
            {
                var api = new FakeCommunityApi { Overlay = () => throw error };
                Assert.AreEqual(0, await CommunitySync.RefreshOverlayAsync(api, "jwt", layers, CancellationToken.None), error.GetType().Name);
                Assert.AreEqual("Hej.", layers.Resolve("Hi.", "Cześć."), "the cached fix still applies");
                Assert.AreEqual(before, File.ReadAllText(layers.CommunityFilePath), "and the cache file is untouched");
            }
        }

        [TestMethod]
        public async Task Refresh_WithoutASession_DoesNotCallTheServer()
        {
            using var dir = new TempFolder();
            var api = new FakeCommunityApi();
            Assert.AreEqual(0, await CommunitySync.RefreshOverlayAsync(api, null, dir.Layers(), CancellationToken.None));
            Assert.AreEqual(0, api.SinceAsked.Count);
        }

        [TestMethod]
        public void ParseOverlay_ReadsTheServersShape()
        {
            static CommunityOverlayPage Parse(string json, bool requestedFull = false) =>
                KaptionApiClient.ParseOverlay(JsonDocument.Parse(json).RootElement.Clone(), requestedFull);

            // backend lib/community.ts readLines: {game, lang, full, until, items, removed}
            var page = Parse("{\"game\":\"genshin\",\"lang\":\"pl\",\"full\":false,\"until\":\"2026-11-03T08:58:00.000Z\"," +
                             "\"items\":[{\"en\":\"A.\",\"target\":\"A pl.\",\"acceptedAt\":\"2026-11-03T09:00:00Z\"},{\"target\":\"no en\"}]," +
                             "\"removed\":[{\"en\":\"B.\",\"removedAt\":\"2026-11-03T09:30:00Z\"}]}");
            Assert.IsFalse(page.Full);
            Assert.AreEqual("2026-11-03T08:58:00.000Z", page.Until);
            Assert.AreEqual(2, page.Entries.Count);
            Assert.AreEqual("A pl.", page.Entries[0].Target);
            Assert.AreEqual("2026-11-03T09:00:00Z", page.Entries[0].AcceptedAt);
            Assert.AreEqual("B.", page.Entries[1].En);
            Assert.AreEqual("", page.Entries[1].Target, "a withdrawn line is a removal");
            Assert.AreEqual("2026-11-03T09:30:00Z", page.Entries[1].AcceptedAt);

            Assert.IsTrue(Parse("{\"items\":[]}", requestedFull: true).Full, "no flag: full when no since was sent");
            Assert.IsNull(Parse("{\"items\":[]}").Until);
            Assert.AreEqual(0, Parse("{\"items\":null}").Entries.Count);
            Assert.AreEqual(0, Parse("[{\"en\":\"C.\",\"target\":\"C pl.\"}]").Entries.Count, "not the server's shape");
            Assert.AreEqual(0, KaptionApiClient.ParseOverlay(default, true).Entries.Count, "an empty body");
        }

        [TestMethod]
        public async Task ABrokenItem_DropsOnlyThatLine_AndTheCursorMovesOn()
        {
            // An escape for half a surrogate pair (a web suggestion the server stored as sent) parses but cannot be read.
            const string json = "{\"full\":false,\"until\":\"2026-11-03T10:58:00.000Z\",\"items\":[" +
                                "{\"en\":\"Broken \\ud83d\",\"target\":\"x\",\"acceptedAt\":\"2026-11-03T10:00:00Z\"}," +
                                "{\"en\":\"Hi.\",\"target\":\"Zepsute \\udc00\",\"acceptedAt\":\"2026-11-03T10:01:00Z\"}," +
                                "{\"x\\ud83d\":0,\"en\":\"Bye.\",\"target\":\"Na razie.\",\"acceptedAt\":\"2026-11-03T10:02:00Z\"}," +
                                "{\"en\":\"Clash.\",\"target\":\"Kolizja.\",\"acceptedAt\":\"2026-11-03T10:02:30Z\",\"t\\ud83d\":0}]," +
                                "\"removed\":[{\"en\":\"\\ud83d\",\"removedAt\":\"2026-11-03T10:03:00Z\"}]}";
            using var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetBytes(json));
            var page = KaptionApiClient.ParseOverlay(doc.RootElement.Clone(), requestedFull: false);
            Assert.AreEqual("Bye.", page.Entries.Single().En, "the readable line survives, nothing else is guessed");
            Assert.AreEqual("2026-11-03T10:58:00.000Z", page.Until);

            using var dir = new TempFolder();
            var layers = dir.Layers();
            layers.ApplyOverlay(new CommunityOverlayPage(new[] { new CommunityEntry("Hi.", "Hej.", "2026-11-01T00:00:00Z") }, "2026-11-01T00:00:00.000Z", true));
            var api = new FakeCommunityApi { Overlay = () => page };
            Assert.AreEqual(1, await CommunitySync.RefreshOverlayAsync(api, "jwt", layers, CancellationToken.None));
            Assert.AreEqual("Na razie.", layers.Resolve("Bye.", "Pa."));
            Assert.AreEqual("Hej.", layers.Resolve("Hi.", "Cześć."), "an unreadable replacement does not remove the line");
            Assert.AreEqual("2026-11-03T10:58:00.000Z", layers.CommunitySince, "the next fetch asks for what came after");
        }

        [TestMethod]
        public void OverlayUrl_EscapesAndOmitsAnEmptySince()
        {
            Assert.AreEqual("/api/community/overlay?game=genshin&lang=pl", KaptionApiClient.BuildOverlayUrl("genshin", "pl", null));
            Assert.AreEqual("/api/community/overlay?game=zzz&lang=pl&since=2026-11-03T09%3A00%3A00%2B01%3A00",
                KaptionApiClient.BuildOverlayUrl("zzz", "pl", "2026-11-03T09:00:00+01:00"));
        }
    }

    internal static class OverlayTestExtensions
    {
        public static int CommunityAfterLoad(this TranslationLayers layers)
        {
            layers.EnsureLoaded();
            return layers.CommunityCount;
        }

        public static string SinceAfterLoad(this TranslationLayers layers)
        {
            layers.EnsureLoaded();
            return layers.CommunitySince;
        }
    }

    // ═══ Queue: offline or failing sends wait on disk and retry later ════════════════════════════════════════════

    [TestClass]
    public class SuggestionQueueTests
    {
        private DateTimeOffset _now = new DateTimeOffset(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);

        private SuggestionQueue Queue(TempFolder dir) => new SuggestionQueue(dir.File("community-queue.json"), () => _now);

        private SuggestionSubmitter Submitter(SuggestionQueue queue, ISuggestionSender sender) =>
            new SuggestionSubmitter(queue, sender, scheduleRetries: false, clock: () => _now);

        private static PendingSuggestion Suggestion(string en = "Let's go!", string text = "No to w drogę!") => new PendingSuggestion
        {
            Game = "genshin",
            Lang = "pl",
            EnText = en,
            CurrentText = "Ruszajmy!",
            SuggestedText = text,
            Speaker = "Paimon",
            ContextPrev = "Wait for me!",
            Note = "brzmi naturalniej",
        };

        [TestMethod]
        public async Task Sent_LeavesTheQueue_AndTheFile()
        {
            using var dir = new TempFolder();
            var queue = Queue(dir);
            var sender = new ScriptedSender();
            var submitter = Submitter(queue, sender);
            queue.Enqueue(Suggestion());
            Assert.IsTrue(File.ReadAllText(queue.FilePath).Contains("No to w drogę!"), "on disk before any send");

            Assert.AreEqual(1, await submitter.FlushAsync(CancellationToken.None));
            Assert.AreEqual(0, queue.Count);
            Assert.AreEqual(0, Queue(dir).Count, "the file says so too");
        }

        [TestMethod]
        public async Task RetryLater_WaitsWithBackoff_ThenSends()
        {
            using var dir = new TempFolder();
            var queue = Queue(dir);
            var sender = new ScriptedSender();
            sender.Results.Enqueue(new SendResult(SendOutcome.RetryLater, "offline or server error"));
            var submitter = Submitter(queue, sender);
            queue.Enqueue(Suggestion());

            Assert.AreEqual(0, await submitter.FlushAsync(CancellationToken.None));
            var waiting = queue.Items.Single();
            Assert.AreEqual(1, waiting.Attempts);
            Assert.AreEqual(_now + TimeSpan.FromMinutes(1), waiting.NextAttemptAt);
            Assert.AreEqual("offline or server error", waiting.LastError);

            await submitter.FlushAsync(CancellationToken.None);
            Assert.AreEqual(1, sender.Calls.Count, "not due yet: no second attempt");

            var reloaded = Queue(dir).Items.Single();
            Assert.AreEqual(1, reloaded.Attempts, "the retry state survives a restart");
            Assert.AreEqual(waiting.NextAttemptAt, reloaded.NextAttemptAt);

            _now += TimeSpan.FromSeconds(61);
            Assert.AreEqual(1, await submitter.FlushAsync(CancellationToken.None));
            Assert.AreEqual(2, sender.Calls.Count);
            Assert.AreEqual(0, queue.Count);
        }

        [TestMethod]
        public async Task OneOfflineFailure_PausesTheWholeQueue()
        {
            using var dir = new TempFolder();
            var queue = Queue(dir);
            var sender = new ScriptedSender();
            sender.Results.Enqueue(new SendResult(SendOutcome.RetryLater, "offline"));
            var submitter = Submitter(queue, sender);
            queue.Enqueue(Suggestion("First."));
            _now += TimeSpan.FromSeconds(1);
            queue.Enqueue(Suggestion("Second."));

            await submitter.FlushAsync(CancellationToken.None);
            Assert.AreEqual(1, sender.Calls.Count, "the second item is not tried while the first one failed");
            Assert.IsTrue(queue.Items.All(i => i.NextAttemptAt >= _now + TimeSpan.FromSeconds(59)), "both wait for the back-off");
            Assert.AreEqual(0, queue.Items.Single(i => i.EnText == "Second.").Attempts, "a skipped item is not counted as failed");
        }

        [TestMethod]
        public async Task Rejected_IsDropped()
        {
            using var dir = new TempFolder();
            var queue = Queue(dir);
            var sender = new ScriptedSender();
            sender.Results.Enqueue(new SendResult(SendOutcome.Rejected, "HTTP 422"));
            queue.Enqueue(Suggestion());
            await Submitter(queue, sender).FlushAsync(CancellationToken.None);
            Assert.AreEqual(0, queue.Count);
        }

        [TestMethod]
        public void NewerSuggestionForTheSameLine_ReplacesTheWaitingOne()
        {
            using var dir = new TempFolder();
            var queue = Queue(dir);
            queue.Enqueue(Suggestion(text: "Pierwsza wersja."));
            queue.Enqueue(Suggestion(text: "Druga wersja."));
            queue.Enqueue(Suggestion(en: "Other line."));
            Assert.AreEqual(2, queue.Count);
            Assert.AreEqual("Druga wersja.", queue.Items.Single(i => i.EnText == "Let's go!").SuggestedText);
        }

        [TestMethod]
        public void Withdraw_RemovesTheWaitingSuggestionForALine()
        {
            using var dir = new TempFolder();
            var queue = Queue(dir);
            queue.Enqueue(Suggestion());
            Assert.IsTrue(queue.Withdraw("GENSHIN", "PL", " Let's go! "));
            Assert.IsFalse(queue.Withdraw("genshin", "pl", "Let's go!"));
            Assert.AreEqual(0, Queue(dir).Count);
        }

        [TestMethod]
        public void OldItemsExpire_AndTheQueueIsCapped()
        {
            using var dir = new TempFolder();
            var queue = Queue(dir);
            queue.Enqueue(Suggestion("Old."));
            _now += SuggestionQueue.MaxAge + TimeSpan.FromMinutes(1);
            Assert.AreEqual(0, queue.Due().Count);
            Assert.AreEqual(0, queue.Count, "older than 30 days: dropped");

            for (int i = 0; i < SuggestionQueue.MaxItems + 5; i++) queue.Enqueue(Suggestion("Line " + i));
            Assert.AreEqual(SuggestionQueue.MaxItems, queue.Count);
            Assert.IsFalse(queue.Items.Any(i => i.EnText == "Line 0"), "the oldest goes first");
        }

        [TestMethod]
        public void CorruptQueueFile_IsSetAside_AndTheQueueStillWorks()
        {
            using var dir = new TempFolder();
            File.WriteAllText(dir.File("community-queue.json"), "not json at all");
            var queue = Queue(dir);
            Assert.AreEqual(0, queue.Count);
            Assert.IsTrue(File.Exists(dir.File("community-queue.json.bad")));
            queue.Enqueue(Suggestion());
            Assert.AreEqual(1, Queue(dir).Count);
        }

        [TestMethod]
        public void QueuedWhileTheFileWasLocked_TheNewerSuggestionWins()
        {
            using var dir = new TempFolder();
            Queue(dir).Enqueue(Suggestion(text: "Stara wersja."));
            var queue = Queue(dir);
            using (new FileStream(queue.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                queue.Enqueue(Suggestion(text: "Nowa wersja."));
            }
            Assert.AreEqual("Nowa wersja.", queue.Items.Single().SuggestedText, "merged once the file could be read");
            Assert.AreEqual("Nowa wersja.", Queue(dir).Items.Single().SuggestedText, "and written back");
        }

        [TestMethod]
        public void Backoff_Doubles_UpToSixHours()
        {
            Assert.AreEqual(TimeSpan.FromMinutes(1), SuggestionQueue.Backoff(1));
            Assert.AreEqual(TimeSpan.FromMinutes(2), SuggestionQueue.Backoff(2));
            Assert.AreEqual(TimeSpan.FromMinutes(8), SuggestionQueue.Backoff(4));
            Assert.AreEqual(TimeSpan.FromHours(6), SuggestionQueue.Backoff(30));
        }

        [TestMethod]
        public void Classify_RetriesWhatCanSucceedLater_DropsWhatCannot()
        {
            static SendOutcome Of(Exception ex) => ApiSuggestionSender.Classify(ex).Outcome;
            Assert.AreEqual(SendOutcome.Sent, Of(new ApiValidationException(HttpStatusCode.Conflict, new ApiError())), "409: already there");
            Assert.AreEqual(SendOutcome.Rejected, Of(new ApiValidationException(HttpStatusCode.BadRequest, new ApiError())));
            Assert.AreEqual(SendOutcome.Rejected, Of(new ApiValidationException((HttpStatusCode)422, new ApiError())));
            Assert.AreEqual(SendOutcome.Rejected, Of(new ApiValidationException(HttpStatusCode.RequestEntityTooLarge, new ApiError())));
            Assert.AreEqual(SendOutcome.Rejected, Of(new ForbiddenException("no")));
            Assert.AreEqual(SendOutcome.RetryLater, Of(new ApiValidationException(HttpStatusCode.NotFound, new ApiError())), "route not live yet");
            Assert.AreEqual(SendOutcome.RetryLater, Of(new UnauthorizedException("expired")));
            Assert.AreEqual(SendOutcome.RetryLater, Of(new ApiUnavailableException("offline", new Exception())));
            Assert.AreEqual(SendOutcome.RetryLater, Of(new TaskCanceledException()));
            var limited = ApiSuggestionSender.Classify(new ApiValidationException((HttpStatusCode)429, new ApiError()));
            Assert.AreEqual(SendOutcome.RetryLater, limited.Outcome);
            Assert.AreEqual(TimeSpan.FromMinutes(15), limited.NotBefore);
        }

        [TestMethod]
        public async Task RateLimit_WaitsAtLeastWhatTheServerAsked()
        {
            using var dir = new TempFolder();
            var queue = Queue(dir);
            var sender = new ScriptedSender();
            sender.Results.Enqueue(new SendResult(SendOutcome.RetryLater, "HTTP 429", TimeSpan.FromMinutes(15)));
            queue.Enqueue(Suggestion());
            await Submitter(queue, sender).FlushAsync(CancellationToken.None);
            Assert.AreEqual(_now + TimeSpan.FromMinutes(15), queue.Items.Single().NextAttemptAt);
        }

        [TestMethod]
        public async Task ApiSender_SendsTheDesignedBody_AndWaitsWhenSignedOut()
        {
            var api = new FakeCommunityApi();
            string session = null;
            var sender = new ApiSuggestionSender(api, () => session);
            Assert.AreEqual(SendOutcome.RetryLater, (await sender.SendAsync(Suggestion(), CancellationToken.None)).Outcome);
            Assert.AreEqual(0, api.Submitted.Count, "no session: nothing sent");

            session = "jwt";
            var bare = Suggestion() with { Speaker = "", ContextPrev = " ", Note = "" };
            Assert.AreEqual(SendOutcome.Sent, (await sender.SendAsync(bare, CancellationToken.None)).Outcome);
            var body = api.Submitted.Single();
            Assert.AreEqual("genshin", body.Game);
            Assert.AreEqual("pl", body.Lang);
            Assert.AreEqual("Let's go!", body.EnText);
            Assert.AreEqual("Ruszajmy!", body.CurrentText);
            Assert.AreEqual("No to w drogę!", body.SuggestedText);
            Assert.IsNull(body.Speaker, "empty optional fields stay out of the body");
            Assert.IsNull(body.ContextPrev);
            Assert.IsNull(body.Note);

            string json = JsonSerializer.Serialize(CommunitySuggestionRequest.From(Suggestion()), GI_Subtitles.Common.JsonDefaults.IgnoreNulls);
            using var doc = JsonDocument.Parse(json);
            CollectionAssert.AreEquivalent(
                new[] { "game", "lang", "enText", "currentText", "suggestedText", "speaker", "contextPrev", "note" },
                doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray(), "the field names of design §4.2");

            api.SubmitError = new ApiValidationException((HttpStatusCode)422, new ApiError { Message = "too long" });
            Assert.AreEqual(SendOutcome.Rejected, (await sender.SendAsync(Suggestion(), CancellationToken.None)).Outcome);
        }
    }

    // ═══ The last shown line ═════════════════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class LastShownLineTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void Record_KeepsTheFields_AndThePreviousKey()
        {
            var tracker = new LastShownLineTracker();
            Assert.IsFalse(tracker.TryGet(out _), "nothing shown yet");

            tracker.Record("Genshin", "pl", "Hello.", "Cześć.", "Witaj.", "Paimon", T0);
            tracker.Record("Genshin", "pl", "Let's go!", "Chodźmy!", "Chodźmy!", "Traveler", T0.AddSeconds(3));
            Assert.IsTrue(tracker.TryGet(out var line));
            Assert.AreEqual("Genshin", line.Game);
            Assert.AreEqual("pl", line.Lang);
            Assert.AreEqual("Let's go!", line.EnKey);
            Assert.AreEqual("Chodźmy!", line.Pl);
            Assert.AreEqual("Chodźmy!", line.PackPl);
            Assert.AreEqual("Traveler", line.Speaker);
            Assert.AreEqual("Hello.", line.PrevEn);
            Assert.AreEqual(T0.AddSeconds(3), line.At);
        }

        [TestMethod]
        public void SameLine_KeepsItsTime_ButTakesANewTextOrALateName()
        {
            var tracker = new LastShownLineTracker();
            tracker.Record("Genshin", "pl", "Prev.", "Poprz.", "Poprz.", "", T0);
            tracker.Record("Genshin", "pl", "Hello.", "Cześć.", "Cześć.", "", T0.AddSeconds(1));
            tracker.Record("Genshin", "pl", "Hello.", "Cześć.", "Cześć.", "Paimon", T0.AddSeconds(2));
            tracker.Record("Genshin", "pl", "Hello.", "Hej.", "Cześć.", "", T0.AddSeconds(3));
            tracker.TryGet(out var line);
            Assert.AreEqual("Hej.", line.Pl, "a fix applied while the line is up");
            Assert.AreEqual("Paimon", line.Speaker, "the name read a frame later is kept, an empty read does not erase it");
            Assert.AreEqual(T0.AddSeconds(1), line.At);
            Assert.AreEqual("Prev.", line.PrevEn, "a repeat is not its own predecessor");
        }

        [TestMethod]
        public void Record_IgnoresUnmatchedFrames()
        {
            var tracker = new LastShownLineTracker();
            tracker.Record("Genshin", "pl", "Hello.", "Cześć.", "Cześć.", "", T0);
            tracker.Record("Genshin", "pl", "", "Cześć.", "Cześć.", "", T0);
            tracker.Record("Genshin", "pl", null, "x", "x", "", T0);
            tracker.Record("Genshin", "pl", "Other.", "", "", "", T0);
            tracker.TryGet(out var line);
            Assert.AreEqual("Hello.", line.EnKey);
            Assert.IsFalse(new LastShownLineTracker().TryGet(out _), "a fresh tracker has nothing");
        }

        [TestMethod]
        public void ASnapshot_IsACopy()
        {
            var tracker = new LastShownLineTracker();
            tracker.Record("Genshin", "pl", "A.", "A pl.", "A pl.", "", T0);
            tracker.TryGet(out var snapshot);
            tracker.Record("Genshin", "pl", "B.", "B pl.", "B pl.", "", T0);
            Assert.AreEqual("A.", snapshot.EnKey, "the fix window keeps the line it opened with");
        }

        [TestMethod]
        public void Record_AllocatesNothing_OnTheSubtitleTick()
        {
            var tracker = new LastShownLineTracker();
            string[] keys = { "First line.", "Second line." };
            string[] pl = { "Pierwsza linia.", "Druga linia." };
            DateTime at = T0;
            for (int i = 0; i < 200; i++) tracker.Record("Genshin", "pl", keys[i % 2], pl[i % 2], pl[i % 2], "Paimon", at);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20_000; i++)
            {
                // A held line (most ticks) and a line change, both on the same strings the matcher hands over.
                tracker.Record("Genshin", "pl", keys[(i / 4) % 2], pl[(i / 4) % 2], pl[(i / 4) % 2], "Paimon", at);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0L, allocated);
        }
    }

    // ═══ The server's text rules, checked before saving ════════════════════════════════════════════════════════

    [TestClass]
    public class SuggestionRulesTests
    {
        [TestMethod]
        public void Clean_DropsWhatTheServerDrops()
        {
            Assert.AreEqual("Hej, Traveler.", SuggestionRules.Clean("Hi, Traveler.", "  Hej, #Traveler.\t "));
            Assert.AreEqual("Raz\ndwa", SuggestionRules.Clean("One\ntwo", "Raz  \r\n  dwa"), "spaces around a break go");
            Assert.AreEqual("Cześć", SuggestionRules.Clean("Hello", "Cze" + (char)0x200B + "ść"), "invisible characters go");
            Assert.AreEqual("Hej", SuggestionRules.Clean("Hi", "<b>Hej</b>"), "markup the English lacks goes");
            Assert.AreEqual("<i>Hej</i> {NICKNAME}", SuggestionRules.Clean("<i>Hi</i> {NICKNAME}", "<i>Hej</i> {NICKNAME} {EXTRA}"));
        }

        [TestMethod]
        public void Check_MatchesTheServersMarkupAndLengthRules()
        {
            Assert.AreEqual(SuggestionProblem.None, SuggestionRules.Check("Deals 1[i]% DMG.", "Zadaje 1[i]% obrażeń.", out _));
            Assert.AreEqual(SuggestionProblem.Empty, SuggestionRules.Check("Hi.", " # ", out _));

            Assert.AreEqual(SuggestionProblem.LineBreaks, SuggestionRules.Check("One\ntwo", "Raz dwa", out string breaks));
            Assert.AreEqual("1", breaks);

            Assert.AreEqual(SuggestionProblem.Markup, SuggestionRules.Check("Deals 1[i]% DMG to {TARGET}.", "Zadaje obrażenia.", out string markup));
            StringAssert.Contains(markup, "1[i]");
            StringAssert.Contains(markup, "{TARGET}");

            Assert.AreEqual(SuggestionProblem.ExtraMarkup, SuggestionRules.Check("Deals DMG.", "Zadaje 5[i] obrażeń.", out string extra));
            Assert.AreEqual("5[i]", extra);

            Assert.AreEqual(SuggestionProblem.None, SuggestionRules.Check("Hi.", new string('a', 3 * 3 + 40), out _), "3 × English + 40 is allowed");
            Assert.AreEqual(SuggestionProblem.TooLong, SuggestionRules.Check("Hi.", new string('a', 3 * 3 + 41), out _));
        }

        [TestMethod]
        public void MarkupTokens_SortLikeJavaScript()
        {
            CollectionAssert.AreEqual(new[] { "1[i]", "<i>", "\\n", "{A}" },
                SuggestionRules.MarkupTokens("{A} 1[i]\n<i>").ToArray());
        }

        [TestMethod]
        public void SameProposal_IgnoresSpacing()
        {
            Assert.IsTrue(SuggestionRules.SameProposal("Raz  dwa", " Raz\ndwa "));
            Assert.IsFalse(SuggestionRules.SameProposal("Raz dwa", "Raz trzy"));
        }

        [TestMethod]
        public void ALoneSurrogate_DoesNotThrow()
        {
            string broken = "Ha" + (char)0xD800 + "ha";
            Assert.AreEqual(broken, SuggestionRules.NormaliseNewlines(broken), "left as it is, as JavaScript does");
            Assert.AreEqual(SuggestionProblem.None, SuggestionRules.Check("Haha.", broken, out _));
        }

        [TestMethod]
        public void OnlyWhatTheServerReviews_IsSent()
        {
            foreach (string game in new[] { "genshin", "starrail", "zzz", "wuwa" })
                Assert.IsTrue(SuggestionRules.TakesSuggestions(game, "pl"), game);
            Assert.IsFalse(SuggestionRules.TakesSuggestions("genshin", "de"), "mirrored languages are not reviewed");
            Assert.IsFalse(SuggestionRules.TakesSuggestions("genshin", "en"));
            Assert.IsFalse(SuggestionRules.TakesSuggestions("genshin", "PL"), "the wire values are lower case, as the server compares them");
            Assert.IsFalse(SuggestionRules.TakesSuggestions("hsr", "pl"), "the wire id is starrail");
            Assert.IsFalse(SuggestionRules.TakesSuggestions(null, null));
            Assert.IsTrue(SuggestionRules.CanSuggest("genshin", "pl", "Hello."));
            Assert.IsFalse(SuggestionRules.CanSuggest("genshin", "pl", new string('a', SuggestionRules.MaxEnglishLength + 1)));
            Assert.IsFalse(SuggestionRules.CanSuggest("genshin", "de", "Hello."));
        }

        [TestMethod]
        public void EnglishAccepted_MirrorsTheServersEnTextRule()
        {
            Assert.IsTrue(SuggestionRules.EnglishAccepted("Hello.\nThere."));
            Assert.IsTrue(SuggestionRules.EnglishAccepted(new string('a', SuggestionRules.MaxEnglishLength)));
            Assert.IsFalse(SuggestionRules.EnglishAccepted(new string('a', SuggestionRules.MaxEnglishLength + 1)));
            Assert.IsFalse(SuggestionRules.EnglishAccepted("Bell" + (char)0x07));
            Assert.IsFalse(SuggestionRules.EnglishAccepted("   "));
        }
    }

    [TestClass]
    public class OverlayRefreshThrottleTests
    {
        private static readonly TimeSpan Interval = CommunitySync.RefreshInterval;

        [TestMethod]
        public void OneFetchAtATime_ThenWaitUntilTheNextAllowedTime()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            var t0 = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc);
            Assert.IsTrue(layers.TryBeginOverlayFetch(t0, force: false, Interval), "never fetched");
            Assert.IsFalse(layers.TryBeginOverlayFetch(t0, force: true, Interval), "never two at once, even forced");
            Assert.AreEqual(0, layers.EndOverlayFetch(t0, failed: false, Interval));

            Assert.IsFalse(layers.TryBeginOverlayFetch(t0.AddMinutes(10), force: false, Interval), "a recent fetch covers a trigger");
            Assert.IsTrue(layers.TryBeginOverlayFetch(t0.AddMinutes(10), force: true, Interval), "the dictionary sync always fetches");
            layers.EndOverlayFetch(t0.AddMinutes(10), failed: false, Interval);
            Assert.IsTrue(layers.TryBeginOverlayFetch(t0.AddMinutes(41), force: false, Interval));
            layers.EndOverlayFetch(t0.AddMinutes(41), failed: false, Interval);

            Assert.IsTrue(layers.TryBeginOverlayFetch(t0.AddHours(-3), force: false, Interval),
                "a next time hours ahead means the clock went back; it does not block");
        }

        [TestMethod]
        public void FailuresCount_AndBackOff()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            var t0 = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc);
            layers.TryBeginOverlayFetch(t0, false, Interval);
            Assert.AreEqual(1, layers.EndOverlayFetch(t0, failed: true, CommunitySync.RetryDelay(1)));
            Assert.IsFalse(layers.TryBeginOverlayFetch(t0.AddMinutes(4), false, Interval), "waits out the retry delay");
            Assert.IsTrue(layers.TryBeginOverlayFetch(t0.AddMinutes(5), false, Interval), "then a failed fetch may be retried");
            Assert.AreEqual(2, layers.EndOverlayFetch(t0.AddMinutes(5), failed: true, CommunitySync.RetryDelay(1)));
            layers.TryBeginOverlayFetch(t0.AddMinutes(11), false, Interval);
            Assert.AreEqual(0, layers.EndOverlayFetch(t0.AddMinutes(11), failed: false, Interval), "a success resets the count");

            Assert.AreEqual(TimeSpan.FromMinutes(5), CommunitySync.RetryDelay(1));
            Assert.AreEqual(TimeSpan.FromMinutes(10), CommunitySync.RetryDelay(2));
            Assert.AreEqual(TimeSpan.FromMinutes(20), CommunitySync.RetryDelay(3));
            Assert.AreEqual(TimeSpan.FromHours(2), CommunitySync.RetryDelay(20));
        }

        [TestMethod]
        public async Task RefreshOverlay_DoesNotRunBesideAFetchInFlight()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            Assert.IsTrue(layers.TryBeginOverlayFetch(DateTime.UtcNow, false, Interval), "a refresh is running");
            var api = new FakeCommunityApi();
            Assert.AreEqual(0, await CommunitySync.RefreshOverlayAsync(api, "jwt", layers, CancellationToken.None));
            Assert.AreEqual(0, api.SinceAsked.Count, "no second request with the same cursor");
            layers.EndOverlayFetch(DateTime.UtcNow, false, Interval);
            await CommunitySync.RefreshOverlayAsync(api, "jwt", layers, CancellationToken.None);
            Assert.AreEqual(1, api.SinceAsked.Count);
        }

        [TestMethod]
        public void RefreshIfStale_WithoutASession_DoesNothing()
        {
            using var dir = new TempFolder();
            var layers = dir.Layers();
            CommunitySync.RefreshIfStale(layers, () => null);
            Assert.IsTrue(layers.TryBeginOverlayFetch(DateTime.UtcNow, false, Interval), "no fetch was claimed");
        }

        [TestMethod]
        public async Task ALanguageTheServerDoesNotReview_IsNeverAskedFor()
        {
            using var dir = new TempFolder();
            var german = dir.Layers(lang: "de");
            var api = new FakeCommunityApi();
            Assert.AreEqual(0, await CommunitySync.RefreshOverlayAsync(api, "jwt", german, CancellationToken.None));
            bool asked = false;
            CommunitySync.RefreshIfStale(german, () => { asked = true; return "jwt"; });
            Assert.AreEqual(0, api.SinceAsked.Count, "no request that can only answer 400");
            Assert.IsFalse(asked, "the session is not even read");
            Assert.IsTrue(german.TryBeginOverlayFetch(DateTime.UtcNow, false, Interval), "and nothing was claimed");
        }
    }

    // ═══ One subtitle tick: what is painted, what "fix this line" records ═══════════════════════════════════════

    [TestClass]
    public class SubtitleComposerTests
    {
        private static SubtitleResolution Matched(string key, string content, string header = "") =>
            new SubtitleResolution { Key = key, Content = content, Header = header, Result = content };

        private static TranslationLayers WithFix(TempFolder dir, string en, string text)
        {
            var layers = dir.Layers();
            layers.SaveUserFix(new UserFix(en, text, "", "", "", DateTimeOffset.UtcNow));
            return layers;
        }

        [TestMethod]
        public void AMatchedLine_ShowsTheFix_AndRecordsBothTexts()
        {
            using var dir = new TempFolder();
            var layers = WithFix(dir, "Let's go!", "No to w drogę!");
            var shown = SubtitleComposer.Compose(Matched("Let's go!", "Ruszajmy!", "Paimon"), true, null, layers, "");
            Assert.AreEqual("No to w drogę!", shown.Content);
            Assert.AreEqual("Paimon", shown.Header);
            Assert.AreEqual("Let's go!", shown.Key);
            Assert.AreEqual("Ruszajmy!", shown.PackText, "the pack's own text, for the fix window's shared reading");
            Assert.IsTrue(shown.Records);
            Assert.IsFalse(shown.IsPrediction);

            var other = SubtitleComposer.Compose(Matched("Hello.", "Cześć."), true, null, layers, "");
            Assert.AreEqual("Cześć.", other.Content, "a line without a fix keeps the pack");
            Assert.AreEqual("Cześć.", SubtitleComposer.Compose(Matched("Let's go!", "Cześć."), true, null, null, "").Content, "no layers yet");
        }

        [TestMethod]
        public void ThePlayersName_IsPainted_ButNotRecorded()
        {
            using var dir = new TempFolder();
            var layers = WithFix(dir, "Traveler, wait.", "Traveler, zaczekaj.");
            var shown = SubtitleComposer.Compose(Matched("Traveler, wait.", "Podróżniku, czekaj."), true, null, layers, "Lumine");
            Assert.AreEqual("Lumine, zaczekaj.", shown.Content);
            Assert.AreEqual("Traveler, zaczekaj.", shown.Layered, "the fix window edits the line as the pack and the server know it");
            Assert.AreEqual("Podróżniku, czekaj.", shown.PackText);
            Assert.AreEqual("about a traveler", SubtitleComposer.WithPlayerName("about a traveler", ""));
            Assert.AreEqual("about a Aether", SubtitleComposer.WithPlayerName("about a traveler", "Aether"));
        }

        [TestMethod]
        public void APrediction_ShowsWhileNothingIsRead_AndIsNeverRecorded()
        {
            using var dir = new TempFolder();
            var layers = WithFix(dir, "Next line.", "Następna, poprawiona.");
            string predicted = SubtitleComposer.Prediction(layers, "Next line.", "Następna linia.");
            Assert.AreEqual("Następna, poprawiona.", predicted, "the prediction gets the same fixes as the line it predicts");
            Assert.AreEqual("Inna.", SubtitleComposer.Prediction(null, "Other.", "Inna."));

            var shown = SubtitleComposer.Compose(default, hasOcrText: false, predicted, layers, "");
            Assert.AreEqual(predicted, shown.Content);
            Assert.IsTrue(shown.IsPrediction);
            Assert.IsFalse(shown.Records, "a guess is not a line the player saw read");
            Assert.AreEqual("", shown.Header);
        }

        [TestMethod]
        public void ReadTextWithoutAMatch_ShowsNothing_EvenWithAPredictionWaiting()
        {
            using var dir = new TempFolder();
            var shown = SubtitleComposer.Compose(default, hasOcrText: true, "Następna linia.", dir.Layers(), "Lumine");
            Assert.AreEqual("", shown.Content, "game UI or an unknown line: the prediction is not shown over it");
            Assert.IsFalse(shown.IsPrediction);
            Assert.IsFalse(shown.Records);

            var headerOnly = SubtitleComposer.Compose(new SubtitleResolution { Key = "", Header = "Paimon", Content = "" }, true, null, dir.Layers(), "");
            Assert.AreEqual("", headerOnly.Header, "no line, no name tag");
            Assert.IsFalse(SubtitleComposer.Compose(Matched("", "Ruszajmy!"), true, null, null, "").Records, "a text without a key is not recorded");
        }

        [TestMethod]
        public void Compose_AllocatesNothing_WithoutAPlayerName()
        {
            using var dir = new TempFolder();
            var layers = WithFix(dir, "Let's go!", "No to w drogę!");
            var hit = Matched("Let's go!", "Ruszajmy!");
            var miss = Matched("Hello.", "Cześć.");
            for (int i = 0; i < 100; i++) SubtitleComposer.Compose(hit, true, null, layers, "");

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                SubtitleComposer.Compose(hit, true, null, layers, "");
                SubtitleComposer.Compose(miss, true, null, layers, "");
                SubtitleComposer.Compose(default, false, "Następna.", layers, "");
            }
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }
}
