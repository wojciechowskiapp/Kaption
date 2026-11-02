using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GI_Subtitles.Services.Characters;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test;

/// <summary>
/// Character names are proper names: the pack's machine translations ("Brzytwa" for Razor,
/// "Świetlik" for Firefly) must never replace them. Only the protagonist title gets a Polish gloss.
/// </summary>
[TestClass]
public class CharacterNamesTests
{
    private sealed class MapLookup(Dictionary<string, string> map) : ITranslationLookup
    {
        public bool TryTranslateExact(string english, out string translated) => map.TryGetValue(english ?? "", out translated);
    }

    private static string Exact(Dictionary<string, string> map, string english) =>
        map.TryGetValue(english, out string t) ? t : null;

    [TestMethod]
    [DataRow("Traveler (Hydro)", "Podróżnik")]
    [DataRow("Traveler", "Podróżnik")]
    [DataRow("Trailblazer (Destruction)", "Pionier")]
    [DataRow("Razor", "")]
    [DataRow("Firefly", "")]
    [DataRow("Wanderer", "")]
    [DataRow("", "")]
    [DataRow(null, "")]
    public void Gloss_OnlyForTheProtagonistTitle(string name, string expected)
    {
        var pack = new Dictionary<string, string>
        {
            ["Traveler"] = "Podróżnik", ["Trailblazer"] = "Pionier",
            ["Razor"] = "Brzytwa", ["Firefly"] = "Świetlik", ["Wanderer"] = "Wędrowiec",
        };
        Assert.AreEqual(expected, CharacterNames.Gloss(name, e => Exact(pack, e)));
    }

    [TestMethod]
    public void Gloss_IsEmpty_WhenThePackHasNoOrTheSameWord()
    {
        Assert.AreEqual("", CharacterNames.Gloss("Traveler (Anemo)", _ => null));
        Assert.AreEqual("", CharacterNames.Gloss("Traveler (Anemo)", _ => "Traveler"));
        Assert.AreEqual("", CharacterNames.Gloss("Traveler (Anemo)", null));
    }

    [TestMethod]
    public void Overview_KeepsEnglishNames_ForTheCharacterAndTeammates()
    {
        string json = CharacterCatalogTests.MinimalJson
            .Replace(@"""teammates"": []", @"""teammates"": [""1002""]")
            .Replace(@"""name"": ""Dan Heng""", @"""name"": ""Firefly""");
        var catalog = CharacterCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), "starrail");
        var pack = new MapLookup(new Dictionary<string, string>
        {
            ["Firefly"] = "Świetlik",
            [catalog.Find("1001").Name] = "Machine name",
        });

        var model = CharacterOverviewBuilder.Build(catalog.Find("1001"), catalog, pack);

        Assert.AreEqual(catalog.Find("1001").Name, model.Name);
        Assert.AreEqual("", model.NameGloss);
        Assert.AreEqual("Firefly", model.TeammateCards.Single().Name);
        CollectionAssert.AreEqual(new[] { "Firefly" }, model.Teammates.ToArray());
    }
}
