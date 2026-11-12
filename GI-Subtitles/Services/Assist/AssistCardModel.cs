using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Stamina;

namespace GI_Subtitles.Services.Assist
{
    /// <summary>One reader on the Settings card: its switch, and one sentence saying exactly what is read.</summary>
    public sealed record AssistReaderRow(string ReaderId, string Title, string Sentence, bool On);

    /// <summary>
    /// Settings, "In-game helper": every reader the selected game has, each with its own switch, and what Kaption
    /// has read so far. Pure: the texts come through <c>l(key, fallback)</c>, the readings from the store.
    /// </summary>
    public sealed class AssistCardModel
    {
        public IReadOnlyList<AssistReaderRow> Readers { get; private set; } = Array.Empty<AssistReaderRow>();
        /// <summary>Shown instead of the switches when the game has nothing to read yet.</summary>
        public string NothingToRead { get; private set; }
        public IReadOnlyList<string> ReadLines { get; private set; } = Array.Empty<string>();
        /// <summary>Whether there is anything the delete button would forget.</summary>
        public bool HasReadings => ReadLines.Count > 0;

        public static AssistCardModel Build(GameManifest game, Func<string, bool> isOn, IReadOnlyList<AssistStoredReading> stored,
            Func<string, string, string> l, CultureInfo culture)
        {
            var model = new AssistCardModel();
            var readers = game?.Desktop?.Assist?.Readers;
            bool any = readers != null && readers.Count > 0;
            // Everything that is stored is listed, also what a reader that has since left the manifest read: the
            // delete button must be able to forget it, so the player must be able to see it.
            model.ReadLines = (stored ?? Array.Empty<AssistStoredReading>())
                .Select(r => string.Format(culture, l("Assist_Read_Line", "{0}: {1}, read {2}"),
                    any && readers.ContainsKey(r.ReaderId) ? Label(game, r.ReaderId, l, culture) : r.ReaderId, r.Value,
                    r.SeenUtc.ToLocalTime().ToString("g", culture)))
                .ToList();
            if (!any)
            {
                model.NothingToRead = string.Format(culture, l("Assist_None", "There is nothing to read in {0} yet."), game?.Name ?? "");
                return model;
            }
            model.Readers = readers.Keys.OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => new AssistReaderRow(id, Title(game, id, l, culture), Sentence(game, id, l, culture), isOn(id)))
                .ToList();
            return model;
        }

        // The stamina reader's words carry the pool's own English name (Original Resin) and its cap, from the manifest.
        private static string Title(GameManifest game, string id, Func<string, string, string> l, CultureInfo culture) =>
            id == AssistSettings.StaminaReader && game.Stamina != null
                ? string.Format(culture, l("Assist_Reader_stamina_Title", "{0} timer"), game.Stamina.Name)
                : l("Assist_Reader_" + id + "_Title", id);

        private static string Sentence(GameManifest game, string id, Func<string, string, string> l, CultureInfo culture) =>
            id == AssistSettings.StaminaReader && game.Stamina != null
                ? string.Format(culture, l("Assist_Reader_stamina_Body",
                    "Reads the {0} number beside its icon at the top of the game screen (like 121/{1}) and keeps a timer for you. Nothing else is read there."),
                    game.Stamina.Name, game.Stamina.Max)
                : l("Assist_Reader_" + id + "_Body", "");

        private static string Label(GameManifest game, string id, Func<string, string, string> l, CultureInfo culture) =>
            id == AssistSettings.StaminaReader && game.Stamina != null ? game.Stamina.Name : Title(game, id, l, culture);
    }

    /// <summary>"Delete what was read": one place that forgets everything read off a game's screen.</summary>
    public static class AssistData
    {
        /// <returns>False when a file could not be removed (something has it open): the caller says so and offers another try.</returns>
        public static bool DeleteEverything(GameManifest game, AssistStore store = null)
        {
            if (game == null) return true;
            // The timer's own copy first, so whoever hears the store's Deleted finds nothing left to read back.
            StaminaSettings.Forget(game.Id);
            bool pictures = AssistSampleSaver.DeleteFolder(AssistSampleSaver.FolderFor(game.GameId));
            pictures &= AssistSampleSaver.DeleteFolder(StaminaSampleSaver.FolderFor(game.GameId));
            return (store ?? AssistStore.Default).DeleteGame(game.GameId) && pictures;
        }
    }
}
