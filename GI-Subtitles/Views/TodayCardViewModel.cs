using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Services.Today;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The Today card's display text, from <see cref="TodayCardModel"/>: the header, the sections as lines, and for
    /// Genshin the farm card's own view model unchanged. Strings come through <c>L(key, fallback)</c>, so the render
    /// tests, the card over the game and the block on Start word it the same. A reading is always worded as a
    /// reading ("Read at 19:40"), never as the present state.
    /// </summary>
    public sealed class TodayCardViewModel
    {
        public TodayCardModel Model { get; }
        /// <summary>Genshin's farm content; null for the other games (the farm blocks then stay collapsed).</summary>
        public FarmCardViewModel Farm { get; }
        public bool HasFarm => Farm != null;

        public string Eyebrow { get; }
        public string DayTitle { get; }
        public string ResetText { get; }
        public bool HasReset => ResetText.Length > 0;
        public IReadOnlyList<SectionVm> Sections { get; }
        public bool HasSections => Sections.Count > 0;
        /// <summary>The Start block's sections: the same, plus the daily reset the card keeps in its header.</summary>
        public IReadOnlyList<SectionVm> BlockSections { get; }
        public string FullListUrl => Model.FullListUrl;

        // The farm card's bindings, answered by Genshin's view model and empty for the other games.
        public IReadOnlyList<FarmCardViewModel.WeekCell> Week => Farm?.Week ?? Array.Empty<FarmCardViewModel.WeekCell>();
        public string TwinText => Farm?.TwinText ?? "";
        public string Tip => Farm?.Tip ?? "";
        public string BooksLabel => Farm?.BooksLabel ?? "";
        public bool HasBooks => Farm?.HasBooks == true;
        public IReadOnlyList<FarmCardViewModel.BookVm> Books => Farm?.Books ?? Array.Empty<FarmCardViewModel.BookVm>();
        public bool HasRows => Farm?.HasRows == true;
        public IReadOnlyList<FarmCardViewModel.RowVm> Rows => Farm?.Rows ?? Array.Empty<FarmCardViewModel.RowVm>();
        public string MoreText => Farm?.MoreText ?? "";
        public bool HasMore => Farm?.HasMore == true;
        public string PlayerMessage => Farm?.PlayerMessage ?? "";
        public bool HasPlayerMessage => Farm?.HasPlayerMessage == true;
        public bool ShowAddUid => Farm?.ShowAddUid == true;
        public string AddUidText => Farm?.AddUidText ?? "";

        public TodayCardViewModel(TodayCardModel model, string uiLanguage, Func<string, string, string> L)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            L ??= (_, f) => f;
            var culture = CultureInfo.GetCultureInfo(uiLanguage?.StartsWith("pl", StringComparison.OrdinalIgnoreCase) == true ? "pl-PL" : "en-US");
            Farm = model.Farm != null ? new FarmCardViewModel(model.Farm, uiLanguage, L) : null;

            Eyebrow = Farm != null
                ? L("FarmCard_Eyebrow", "What to farm today")
                : string.Format(culture, L("TodayCard_Eyebrow_Format", "Today in {0}"), model.GameName);
            string day = culture.DateTimeFormat.GetDayName(model.Weekday);
            DayTitle = Farm?.DayTitle ?? (day.Length > 0 ? char.ToUpper(day[0], culture) + day.Substring(1) : day);
            ResetText = Farm?.ResetText ?? (model.Resets != null
                ? string.Format(culture, L("FarmCard_ResetIn_Format", "Resets in {0}"), StaminaText.Duration(model.Resets.Daily - model.Now, L))
                : "");

            var sections = new List<SectionVm>();
            if (model.Stamina != null) sections.Add(Stamina(model.Stamina, model.Now, L, culture));
            // Genshin's card already carries the week strip, the books and up to five characters: over the game it
            // keeps the weekly reset for Start and lists two periods, so a 1080p screen at 125 % still reads it.
            bool tall = Farm != null;
            SectionVm week = model.Resets != null && !tall ? ThisWeek(model.Resets, model.Now, L, culture, withDaily: false) : null;
            if (week != null) sections.Add(week);
            if (model.Ending.Count > 0) sections.Add(Ending(model, tall ? EndingBesideFarm : model.Ending.Count, L, culture));
            if (model.Seen.Count > 0) sections.Add(Seen(model, L, culture));
            Sections = sections;

            // Start: the stamina timer has its own card there, the daily reset has no header chip to sit in, and
            // Genshin's books and characters come down to one line.
            var block = new List<SectionVm>();
            if (model.Resets != null) block.Add(ThisWeek(model.Resets, model.Now, L, culture, withDaily: true));
            if (model.Ending.Count > 0) block.Add(Ending(model, model.Ending.Count, L, culture));
            if (model.Seen.Count > 0) block.Add(Seen(model, L, culture));
            if (Farm != null && model.Farm.Books.Count > 0)
            {
                string names = string.Join(", ", model.Farm.Rows.Select(r => r.Name)) + (model.Farm.MoreRows > 0 ? $" +{model.Farm.MoreRows}" : "");
                block.Add(new SectionVm(Farm.BooksLabel, new[]
                {
                    new LineVm("", string.Join(", ", model.Farm.Books.Select(b => b.Series)), "",
                        model.Farm.Rows.Count > 0 ? string.Format(culture, L("TodayBlock_Level_Format", "Talents to level today: {0}"), names) : "", null, null),
                }));
            }
            BlockSections = block;
        }

        /// <summary>The farm card alone, as it was before the other games had a card: the same pixels.</summary>
        public TodayCardViewModel(FarmCardViewModel farm)
        {
            Farm = farm ?? throw new ArgumentNullException(nameof(farm));
            Model = new TodayCardModel { Game = "genshin", GameId = "Genshin", Farm = farm.Model, Now = farm.Model.Now, Weekday = farm.Model.Weekday, FullListUrl = farm.FullListUrl };
            Eyebrow = null; // the view keeps its own resource text
            DayTitle = farm.DayTitle;
            ResetText = farm.ResetText;
            Sections = Array.Empty<SectionVm>();
            BlockSections = Sections;
        }

        private static SectionVm Stamina(TodayStamina s, DateTimeOffset now, Func<string, string, string> L, CultureInfo culture)
        {
            string line = s.State switch
            {
                TodayStaminaState.Filling => string.Format(culture, L("Stamina_FullAt", "Full {0}, in {1}."),
                    StaminaText.Moment(s.FullAt, now, L, culture), StaminaText.Duration(s.FullAt - now, L)),
                TodayStaminaState.Full => string.Format(culture, L("Stamina_FullSince", "Full. It filled {0}."), StaminaText.Moment(s.FullAt, now, L, culture)),
                _ => FullSinceAbout(s.FullAt, now, L, culture),
            };
            string read = string.Format(culture, L("TodayCard_ReadAt_Format", "Read {0}"), StaminaText.Moment(s.ReadAt, now, L, culture));
            var lines = new List<LineVm>
            {
                new(string.Create(CultureInfo.InvariantCulture, $"{s.Value}/{s.Max}"), line, "", read, s.TimerUrl, L("Stamina_OpenTimer", "Open the timer")),
            };
            if (s.Reserve != null)
            {
                lines.Add(new LineVm("", s.Reserve.AtCap
                    ? string.Format(culture, L("TodayCard_Reserve_Cap_Format", "{0}: full by the timer as well ({1})."), s.Reserve.Name, s.Reserve.Max)
                    : string.Format(culture, L("TodayCard_Reserve_Format", "{0}: about +{1} since it filled."), s.Reserve.Name, s.Reserve.Gained),
                    "", "", null, null));
            }
            return new SectionVm(s.PoolName, lines);
        }

        // "Full since about 14:20": the hour alone today, the day with it otherwise; never the present tense of a fact.
        private static string FullSinceAbout(DateTimeOffset full, DateTimeOffset now, Func<string, string, string> L, CultureInfo culture)
        {
            DateTime local = full.ToLocalTime().DateTime;
            string clock = local.ToString("t", culture);
            int days = (local.Date - now.ToLocalTime().Date).Days;
            return days switch
            {
                0 => string.Format(culture, L("TodayCard_FullAbout_Today", "Full since about {0}."), clock),
                -1 => string.Format(culture, L("TodayCard_FullAbout_Yesterday", "Full since about yesterday, {0}."), clock),
                _ => string.Format(culture, L("TodayCard_FullAbout_Day", "Full since about {0}, {1}."), local.ToString("m", culture), clock),
            };
        }

        private static SectionVm ThisWeek(TodayResets r, DateTimeOffset now, Func<string, string, string> L, CultureInfo culture, bool withDaily)
        {
            string In(DateTimeOffset at) => string.Format(culture, L("TodayCard_In_Format", "in {0}"), StaminaText.Duration(at - now, L));
            string weekday = culture.DateTimeFormat.GetDayName(r.WeeklyDay);
            var lines = new List<LineVm>();
            if (withDaily) lines.Add(new LineVm("", L("TodayCard_Daily", "Daily reset"), In(r.Daily), "", null, null));
            lines.Add(new LineVm("", string.Format(culture, L("TodayCard_Weekly_Format", "Weekly reset, {0}"), weekday), In(r.Weekly), "", null, null));
            return new SectionVm(L("TodayCard_Week", "This week"), lines);
        }

        /// <summary>How many periods Genshin's card lists beside its farm content; the rest is a count.</summary>
        public const int EndingBesideFarm = 2;

        private static SectionVm Ending(TodayCardModel m, int max, Func<string, string, string> L, CultureInfo culture)
        {
            int more = m.MoreEnding + Math.Max(0, m.Ending.Count - max);
            var lines = m.Ending.Take(max).Select(p => new LineVm("",
                string.IsNullOrEmpty(p.Label) ? p.Title : $"{p.Title} · {p.Label}",
                string.Format(culture, L("TodayCard_EndsIn_Format", "ends in {0}"), StaminaText.Duration(p.EndsAt - m.Now, L)),
                "", p.Url, null)).ToList();
            if (more > 0)
                lines.Add(new LineVm("", string.Format(culture, L("TodayCard_MoreEnding_Format", "+{0} more on the site"), more), "", "", m.FullListUrl, null));
            return new SectionVm(L("TodayCard_Ending", "Ending soon"), lines);
        }

        private static SectionVm Seen(TodayCardModel m, Func<string, string, string> L, CultureInfo culture) =>
            new(L("TodayCard_Seen", "Seen on this PC"), m.Seen.Select(s => new LineVm("",
                $"{L("Assist_Reader_" + s.ReaderId + "_Title", s.ReaderId)}: {s.Value}", "",
                string.Format(culture, L("TodayCard_SeenAt_Format", "Seen {0}"), StaminaText.Moment(s.SeenAt, m.Now, L, culture)), null, null)).ToList());

        /// <summary>A titled group of lines.</summary>
        public sealed record SectionVm(string Title, IReadOnlyList<LineVm> Lines);

        /// <summary>
        /// One line: an optional big lead ("143/200"), the text, a right-hand note ("in 5 h"), a small line under it
        /// ("Read at 19:40") and a link. <see cref="LinkText"/> names a link shown as its own words; without it the
        /// whole line is the link.
        /// </summary>
        public sealed record LineVm(string Lead, string Text, string Note, string Sub, string Url, string LinkText)
        {
            public bool HasLead => !string.IsNullOrEmpty(Lead);
            public bool HasText => !string.IsNullOrEmpty(Text);
            public bool HasNote => !string.IsNullOrEmpty(Note);
            public bool HasSub => !string.IsNullOrEmpty(Sub);
            public bool HasLinkText => !string.IsNullOrEmpty(Url) && !string.IsNullOrEmpty(LinkText);
            /// <summary>The whole line opens its page (an article); a line with link words keeps them as the link.</summary>
            public bool IsLink => !string.IsNullOrEmpty(Url) && string.IsNullOrEmpty(LinkText);
        }
    }
}
