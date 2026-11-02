using System;
using System.IO;
using System.Linq;
using System.Text;
using GI_Subtitles.Services.Characters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class CharacterCatalogTests
    {
        internal const string MinimalJson = @"{
  ""schema"": 1, ""game"": ""starrail"", ""game_version"": ""4.6"",
  ""stats"": { ""CriticalChanceBase"": ""CRIT Rate"", ""SpeedDelta"": ""SPD"" },
  ""sets"": {
    ""103"": { ""name"": ""Hunter of Glacial Forest"", ""kind"": ""relic"",
               ""bonuses"": [ { ""pieces"": 2, ""text"": { ""t"": ""Increases Ice DMG by 1[i]%."", ""p"": [0.1] } } ] }
  },
  ""weapons"": {
    ""23004"": { ""name"": ""In the Name of the World"", ""rarity"": 5, ""type"": ""Nihility"",
                 ""passive_name"": ""Inheritor"", ""passive"": { ""t"": ""DMG +1[i]%"", ""p"": [0.24] } }
  },
  ""characters"": [
    { ""id"": ""1001"", ""name"": ""March 7th"", ""rarity"": 4, ""element"": ""Ice"", ""weapon_type"": ""Preservation"",
      ""role"": { ""t"": ""Shields allies"", ""p"": [] },
      ""skills"": [
        { ""kind"": ""basic"", ""name"": ""Frigid Cold Arrow"",
          ""short"": { ""t"": ""Deals minor Ice DMG to one enemy."", ""p"": [] },
          ""desc"": { ""t"": ""Deals Ice DMG equal to 1[i]% of March 7th's ATK."", ""p"": [1.0] } }
      ],
      ""build"": { ""sets"": [ { ""pieces"": 4, ""ids"": [""103""] } ],
                   ""main_stats"": [ { ""slot"": ""body"", ""stats"": [""CriticalChanceBase""] } ],
                   ""sub_stats"": [""SpeedDelta""], ""weapons"": [""23004""], ""teammates"": [] } },
    { ""id"": ""1002"", ""name"": ""Dan Heng"", ""rarity"": 4, ""element"": ""Wind"", ""weapon_type"": ""The Hunt"",
      ""role"": null, ""skills"": [], ""build"": null }
  ]
}";

        private static Stream S(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

        [TestMethod]
        public void Load_ValidMinimal()
        {
            var c = CharacterCatalog.Load(S(MinimalJson), "StarRail");

            Assert.AreEqual(2, c.Characters.Count);
            Assert.AreEqual("4.6", c.GameVersion);
            var march = c.Find("1001");
            Assert.AreEqual("March 7th", march.Name);
            Assert.AreEqual(4, march.Rarity);
            Assert.AreEqual("Shields allies", march.Role.Template);
            Assert.AreEqual(1.0, march.Skills[0].Description.Params[0]);
            Assert.AreEqual(4, march.Build.Sets[0].Pieces);
            Assert.AreEqual("body", march.Build.MainStats[0].Slot);
            Assert.AreEqual("Hunter of Glacial Forest", c.Sets["103"].Name);
            Assert.AreEqual(0.1, c.Sets["103"].Bonuses[0].Text.Params[0]);
            Assert.AreEqual("Inheritor", c.Weapons["23004"].PassiveName);
            Assert.AreEqual("CRIT Rate", c.Stats["CriticalChanceBase"]);
            Assert.IsNull(c.Find("1002").Build);
            Assert.IsNull(c.Find("1002").Role);
        }

        [TestMethod]
        public void Load_WrongSchema_Empty()
        {
            var c = CharacterCatalog.Load(S(MinimalJson.Replace(@"""schema"": 1", @"""schema"": 2")), "starrail");
            Assert.IsTrue(c.IsEmpty);
        }

        [TestMethod]
        public void Load_GameMismatch_Empty()
        {
            Assert.IsTrue(CharacterCatalog.Load(S(MinimalJson), "Genshin").IsEmpty);
        }

        [TestMethod]
        public void Load_SkipsMalformedRecord()
        {
            string json = MinimalJson.Replace(@"{ ""id"": ""1002"", ""name"": ""Dan Heng""", @"{ ""id"": 5, ""nope"": ""x""");
            var c = CharacterCatalog.Load(S(json), "starrail");

            Assert.AreEqual(1, c.Characters.Count);
            Assert.AreEqual("1001", c.Characters[0].Id);
        }

        [TestMethod]
        public void Load_WrongValueTypes_Tolerated()
        {
            string json = MinimalJson.Replace(@"""rarity"": 4, ""element"": ""Ice""", @"""rarity"": ""four"", ""element"": 7");
            var c = CharacterCatalog.Load(S(json), "starrail");
            var march = c.Find("1001");
            Assert.IsNotNull(march);
            Assert.AreEqual(0, march.Rarity);
            Assert.AreEqual("", march.Element);
        }

        [TestMethod]
        public void Load_TruncatedJson_Empty()
        {
            Assert.IsTrue(CharacterCatalog.Load(S(MinimalJson.Substring(0, MinimalJson.Length / 2)), "starrail").IsEmpty);
        }

        [TestMethod]
        public void Load_NullOrEmptyStream_Empty()
        {
            Assert.IsTrue(CharacterCatalog.Load(null, "starrail").IsEmpty);
            Assert.IsTrue(CharacterCatalog.Load(S(""), "starrail").IsEmpty);
            Assert.IsTrue(CharacterCatalog.Load(S("[]"), "starrail").IsEmpty);
        }

        [TestMethod]
        public void Load_TooLarge_Empty()
        {
            using var big = new LengthOnlyStream(CharacterCatalog.MaxBytes + 1);
            Assert.IsTrue(CharacterCatalog.Load(big, "starrail").IsEmpty);
        }

        [TestMethod]
        public void Find_UnknownId_Null()
        {
            Assert.IsNull(CharacterCatalog.Load(S(MinimalJson), "starrail").Find("nope"));
            Assert.IsNull(CharacterCatalog.Empty.Find(null));
        }

        [TestMethod]
        public void Load_RealOutputs_WhenPresent()
        {
            var files = RealOutputFiles();
            if (files.Length == 0) Assert.Inconclusive("No tools/out/<game>/characters-*.json present.");

            foreach (string f in files)
            {
                string game = new DirectoryInfo(Path.GetDirectoryName(f)).Name;
                using var fs = File.OpenRead(f);
                var c = CharacterCatalog.Load(fs, game);
                Assert.IsFalse(c.IsEmpty, f);
                Assert.IsTrue(c.Characters.Count >= 40, $"{f}: {c.Characters.Count} characters");
                Assert.IsTrue(c.Characters.All(ch => !string.IsNullOrEmpty(ch.Id) && !string.IsNullOrEmpty(ch.Name)), f);
            }
        }

        [TestMethod]
        public void RealOutputs_DetectorRecognisesCharactersFromTheirSkills()
        {
            var files = RealOutputFiles();
            if (files.Length == 0) Assert.Inconclusive("No tools/out/<game>/characters-*.json present.");

            foreach (string f in files)
            {
                string game = new DirectoryInfo(Path.GetDirectoryName(f)).Name;
                using var fs = File.OpenRead(f);
                var c = CharacterCatalog.Load(fs, game);
                var detector = new CharacterDetector(c);
                var withSkills = c.Characters.Where(ch => ch.Skills.Count >= 3).ToList();
                int ok = withSkills.Count(ch => detector.Detect(ch.Skills.Select(s => s.Name).ToList())?.Id == ch.Id);
                double rate = (double)ok / withSkills.Count;
                Assert.IsTrue(rate >= 0.9, $"{f}: detector recognised {ok}/{withSkills.Count}");
            }
        }

        // tools/out is generated and gitignored: a fresh checkout (the release runner) has none.
        private static string[] RealOutputFiles()
        {
            string root = FindRepoRoot();
            string dir = root == null ? null : Path.Combine(root, "tools", "out");
            return dir != null && Directory.Exists(dir)
                ? Directory.GetFiles(dir, "characters-*.json", SearchOption.AllDirectories)
                : Array.Empty<string>();
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GI-Subtitles.sln"))) dir = dir.Parent;
            return dir?.FullName;
        }

        /// <summary>Reports a huge Length without allocating it.</summary>
        private sealed class LengthOnlyStream : Stream
        {
            private readonly long _length;
            public LengthOnlyStream(long length) => _length = length;
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get; set; }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("must not read");
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void SetLength(long value) { }
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }
}
