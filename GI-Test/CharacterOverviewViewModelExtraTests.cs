using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Services.Characters;
using GI_Subtitles.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>The view-model prefers the structured model fields over its own guesses.</summary>
    [TestClass]
    public class CharacterOverviewViewModelExtraTests
    {
        [TestMethod]
        public void GameFromModel_BeatsInferenceFromKinds()
        {
            var model = new CharacterOverviewModel
            {
                Name = "Anby",
                Game = "zzz",
                Skills = new[] { new OverviewSkill { Kind = "constellation", Name = "looks like Genshin" } },
            };
            Assert.AreEqual("zzz", new CharacterOverviewViewModel(model).Game);

            model.Game = "";
            Assert.AreEqual("genshin", new CharacterOverviewViewModel(model).Game, "no game on the model: infer");
        }

        [TestMethod]
        public void TypedBonuses_UsedWhenPresent_StringsOtherwise()
        {
            var typed = new OverviewSet
            {
                Name = "Woodpecker Electro",
                Bonuses = new[] { "2: CRIT Rate +8%." },
                BonusItems = new[] { new OverviewSetBonus { Pieces = 2, Text = "CRIT Rate +8%." } },
            };
            var legacy = new OverviewSet { Name = "Old", Bonuses = new[] { "4: Legacy text" } };
            var model = new CharacterOverviewModel
            {
                Name = "Anby", Game = "zzz", HasBuild = true, HasOfficialBuild = true,
                SetGroups = new[] { new OverviewSetGroup { Pieces = 4, Options = new[] { typed, legacy } } },
            };

            var group = new CharacterOverviewViewModel(model).SetGroups.Single();
            var rows = group.Options.ToList();
            Assert.AreEqual("2", rows[0].Bonuses[0].Pieces);
            Assert.AreEqual("CRIT Rate +8%.", rows[0].Bonuses[0].Text);
            Assert.AreEqual("4", rows[1].Bonuses[0].Pieces);
            Assert.AreEqual("Legacy text", rows[1].Bonuses[0].Text);
        }

        [TestMethod]
        public void TeammateChips_FromCards_OrFromNames()
        {
            var withCards = new CharacterOverviewModel
            {
                Name = "Anby", Game = "zzz",
                Teammates = new[] { "Nicole" },
                TeammateCards = new[] { new OverviewTeammate { Id = "1031", Name = "Nicole", IconPath = null } },
            };
            var chips = new CharacterOverviewViewModel(withCards).TeammateCards;
            Assert.AreEqual(1, chips.Count);
            Assert.AreEqual("Nicole", chips[0].Name);
            Assert.AreEqual("N", chips[0].Initial);

            var namesOnly = new CharacterOverviewModel { Name = "Anby", Game = "zzz", Teammates = new[] { "Soldier 11" } };
            Assert.AreEqual("Soldier 11", new CharacterOverviewViewModel(namesOnly).TeammateCards.Single().Name);
        }
    }
}
