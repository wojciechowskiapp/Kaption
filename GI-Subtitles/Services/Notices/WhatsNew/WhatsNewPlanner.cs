using System;
using System.Collections.Generic;
using System.Linq;

namespace GI_Subtitles.Services.Notices.WhatsNew
{
    /// <summary>What the player said to a question.</summary>
    public enum WhatsNewAnswer
    {
        /// <summary>Turned on from the card.</summary>
        On,
        /// <summary>"Not now".</summary>
        Later,
        /// <summary>
        /// "Don't ask again". At a start it does what "Not now" does: no answered question is ever asked again on its
        /// own. The two are kept apart in what is stored, so a later rule about "not now" has something to read.
        /// </summary>
        Never,
    }

    /// <summary>The switches an entry may name in <c>feature</c>. A new one needs code: how it is read and set.</summary>
    public static class WhatsNewFeatures
    {
        public const string StaminaNotice = "stamina-notice";
        public const string StartWithWindows = "start-with-windows";
        public const string ShowInRecordings = "show-in-recordings";
        public const string DayNotice = "day-notice";

        public static readonly IReadOnlyCollection<string> Known =
            new HashSet<string>(StringComparer.Ordinal) { StaminaNotice, StartWithWindows, ShowInRecordings, DayNotice };
    }

    /// <summary>The conditions an entry may name in <c>requires</c>, about the selected game.</summary>
    public static class WhatsNewRequirements
    {
        /// <summary>The app can read this game's stamina off the screen (desktop.staminaScreen).</summary>
        public const string StaminaScreen = "stamina-screen";
        /// <summary>The game has a card of the day to open.</summary>
        public const string TodayCard = "today-card";

        public static readonly IReadOnlyCollection<string> Known = new HashSet<string>(StringComparer.Ordinal) { StaminaScreen, TodayCard };
    }

    /// <summary>The actions an entry may name in <c>action</c>.</summary>
    public static class WhatsNewActions
    {
        /// <summary>Opens the selected game's card of the day.</summary>
        public const string TodayCard = "today-card";

        public static readonly IReadOnlyCollection<string> Known = new HashSet<string>(StringComparer.Ordinal) { TodayCard };
    }

    /// <summary>Everything the plan reads; plain values and questions, so the rules test without a window or Config.</summary>
    public sealed record WhatsNewInputs
    {
        /// <summary>WhatsNew.Enabled: the player wants the card after an update.</summary>
        public bool Enabled { get; init; } = true;
        /// <summary>The newest release whose card this profile was shown; null when none was ever recorded.</summary>
        public Version SeenThrough { get; init; }
        /// <summary>The setup ran in this launch, or has not run yet: a new player, who has just been shown everything.</summary>
        public bool FreshInstall { get; init; }
        /// <summary>The selected game's wire id ("genshin").</summary>
        public string Game { get; init; }
        /// <summary>The player answered a question about this feature before, whatever the answer.</summary>
        public Func<string, bool> Answered { get; init; } = _ => false;
        public Func<string, bool> FeatureOn { get; init; } = _ => false;
        public Func<string, bool> Meets { get; init; } = _ => false;
    }

    public enum WhatsNewPlanState
    {
        /// <summary>Nothing new for this profile.</summary>
        None,
        /// <summary>A new install: remember the current level and show nothing.</summary>
        Baseline,
        /// <summary>Newer entries exist but none is for this player, or the card is switched off: remember the level.</summary>
        Retire,
        Show,
    }

    public sealed record WhatsNewPlan(WhatsNewPlanState State, Version Level, IReadOnlyList<WhatsNewEntry> Rows)
    {
        public static readonly WhatsNewPlan None = new(WhatsNewPlanState.None, null, Array.Empty<WhatsNewEntry>());
    }

    /// <summary>
    /// "New in Kaption", once per release: which entries lead the Start page on the first start after an update.
    /// The caller records the level the first time the card is really on screen, so a later start has nothing newer.
    /// The level is the newest entry's release, not the build's version stamp: every entry in the file is in this
    /// build, and a Debug build's stamp is whatever the checked-in file says. Pure.
    /// </summary>
    public static class WhatsNewPlanner
    {
        /// <summary>Rows on the card: the two or three things that matter, not a changelog.</summary>
        public const int MaxRows = 3;

        private static readonly Version Never = new(0, 0);

        public static WhatsNewPlan Lead(WhatsNewCatalog catalog, WhatsNewInputs i)
        {
            Version level = catalog?.Level;
            if (level == null || i == null) return WhatsNewPlan.None;
            if (i.SeenThrough == null && i.FreshInstall) return new WhatsNewPlan(WhatsNewPlanState.Baseline, level, Array.Empty<WhatsNewEntry>());
            Version seen = i.SeenThrough ?? Never;
            if (level <= seen) return WhatsNewPlan.None;
            // Switched off: the release still counts as told, so switching it on later brings no old news.
            if (!i.Enabled) return new WhatsNewPlan(WhatsNewPlanState.Retire, level, Array.Empty<WhatsNewEntry>());
            // A player who skipped a release may find two entries asking about one feature: only the newest asks
            // (the catalog is newest first), so the card never puts the same question twice.
            var asked = new HashSet<string>(StringComparer.Ordinal);
            var rows = catalog.Entries.Where(e => e.Since > seen && Offered(e, i))
                .Where(e => e.Kind != WhatsNewKind.Ask || asked.Add(e.Feature)).Take(MaxRows).ToList();
            return rows.Count == 0
                ? new WhatsNewPlan(WhatsNewPlanState.Retire, level, rows)
                : new WhatsNewPlan(WhatsNewPlanState.Show, level, rows);
        }

        /// <summary>The list the player opens himself: every entry that applies to the selected game, answered or not.</summary>
        public static IReadOnlyList<WhatsNewEntry> List(WhatsNewCatalog catalog, WhatsNewInputs i) =>
            catalog == null || i == null ? Array.Empty<WhatsNewEntry>() : catalog.Entries.Where(e => Applies(e, i)).ToList();

        /// <summary>The entry is about something this build has, for the game the player selected.</summary>
        public static bool Applies(WhatsNewEntry e, WhatsNewInputs i)
        {
            if (e == null) return false;
            if (e.Feature != null && !WhatsNewFeatures.Known.Contains(e.Feature)) return false;
            if (e.Action != null && !WhatsNewActions.Known.Contains(e.Action)) return false;
            if (e.Games.Count > 0 && !e.Games.Contains(i.Game ?? "", StringComparer.OrdinalIgnoreCase)) return false;
            if (e.Requires != null && !(WhatsNewRequirements.Known.Contains(e.Requires) && i.Meets(e.Requires))) return false;
            return true;
        }

        /// <summary>
        /// Shown at a start: applies, and is not a question already dealt with. A question is not asked about a thing
        /// that is already on, or that the player answered before in any release; news about a switch the player
        /// turned off is not news to them.
        /// </summary>
        public static bool Offered(WhatsNewEntry e, WhatsNewInputs i)
        {
            if (!Applies(e, i)) return false;
            if (e.Feature == null) return true;
            bool on = i.FeatureOn(e.Feature);
            return e.Kind == WhatsNewKind.Ask ? !on && !i.Answered(e.Feature) : on;
        }
    }

    /// <summary>A row as the card draws it: the entry with its words filled in.</summary>
    public sealed record WhatsNewRow(WhatsNewEntry Entry, string Title, string Body, string Done, string ActionLabel, bool FeatureOn)
    {
        public bool IsAsk => Entry.Kind == WhatsNewKind.Ask;
        public bool HasSwitch => Entry.Feature != null;
    }

    public static class WhatsNewRows
    {
        /// <summary>
        /// The rows for <paramref name="entries"/> in one language. An entry whose words name a value this game does
        /// not have is left out rather than shown with a hole in it.
        /// </summary>
        public static IReadOnlyList<WhatsNewRow> Build(IEnumerable<WhatsNewEntry> entries, bool polish,
            IReadOnlyDictionary<string, string> values, Func<string, bool> featureOn)
        {
            var rows = new List<WhatsNewRow>();
            foreach (WhatsNewEntry e in entries ?? Array.Empty<WhatsNewEntry>())
            {
                WhatsNewText t = e.Text(polish);
                string title = WhatsNewCatalog.Fill(t.Title, values), body = WhatsNewCatalog.Fill(t.Body, values);
                if (title == null || body == null) continue;
                // A confirmation that cannot be filled falls back to none: the row then just shows its switch on.
                rows.Add(new WhatsNewRow(e, title, body, WhatsNewCatalog.Fill(t.Done, values), e.Action == null ? null : t.Action,
                    e.Feature != null && featureOn != null && featureOn(e.Feature)));
            }
            return rows;
        }
    }
}
