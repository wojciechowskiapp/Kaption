using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using GI_Subtitles.Services.Today;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The farm card's display text and brushes, from <see cref="FarmCardModel"/>. Strings come through
    /// <c>L(key, fallback)</c> from the host's dictionaries, so the render tests and the app word it the same.
    /// </summary>
    public sealed class FarmCardViewModel
    {
        // The game's own item frames: 2★ green (Teachings), 3★ blue (Guide), 4★ purple (Philosophies).
        private static readonly (Color Top, Color Bottom)[] TierFrames =
        {
            (Color.FromRgb(0x5E, 0x93, 0x7C), Color.FromRgb(0x33, 0x63, 0x53)),
            (Color.FromRgb(0x5B, 0x88, 0xCC), Color.FromRgb(0x33, 0x55, 0x92)),
            (Color.FromRgb(0x9E, 0x77, 0xC9), Color.FromRgb(0x5C, 0x43, 0x8A)),
        };

        private static readonly Brush[] TierBrushes = TierFrames.Select(f => Frame(f.Top, f.Bottom)).ToArray();

        public static Brush TierBrush(int tier) => TierBrushes[Math.Clamp(tier, 0, TierBrushes.Length - 1)];

        public FarmCardModel Model { get; }
        public string DayTitle { get; }
        public string ResetText { get; }
        public IReadOnlyList<WeekCell> Week { get; }
        public string TwinText { get; }
        public string Tip { get; }
        public string BooksLabel { get; }
        public bool HasBooks => Books.Count > 0;
        public IReadOnlyList<BookVm> Books { get; }
        public bool IsSunday => Model.IsSunday;
        public bool HasRows => Rows.Count > 0;
        public IReadOnlyList<RowVm> Rows { get; }
        public string MoreText { get; }
        public bool HasMore => MoreText.Length > 0;
        public string PlayerMessage { get; }
        public bool HasPlayerMessage => PlayerMessage.Length > 0;
        public bool ShowAddUid => Model.Player is FarmPlayerState.NoUid or FarmPlayerState.UnknownUid;
        /// <summary>"Add UID", or "Check UID" when the saved one has no player.</summary>
        public string AddUidText { get; }
        public string FullListUrl => Model.FullListUrl;

        public FarmCardViewModel(FarmCardModel model, string uiLanguage, Func<string, string, string> L)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            L ??= (_, f) => f;
            var culture = CultureInfo.GetCultureInfo(uiLanguage?.StartsWith("pl", StringComparison.OrdinalIgnoreCase) == true ? "pl-PL" : "en-US");

            string day = culture.DateTimeFormat.GetDayName(model.Weekday);
            DayTitle = day.Length > 0 ? char.ToUpper(day[0], culture) + day.Substring(1) : day;
            ResetText = string.Format(culture, L("FarmCard_ResetIn_Format", "Resets in {0}"), Duration(model.ResetIn, culture, L));

            string[] letters = L("FarmCard_WeekLetters", "Mo,Tu,We,Th,Fr,Sa,Su").Split(',');
            int twin = Twin((int)model.Weekday);
            // Monday first, as both languages' calendars: the strip shows which days open the same books.
            Week = Enumerable.Range(0, 7).Select(i =>
            {
                int wd = (i + 1) % 7;
                bool today = wd == (int)model.Weekday;
                bool same = !model.IsSunday && (wd == twin || wd == 0);
                return new WeekCell(i < letters.Length ? letters[i].Trim() : "", today, same && !today);
            }).ToList();
            TwinText = model.IsSunday
                ? L("FarmCard_Sunday", "All domains are open on Sunday")
                : string.Format(culture, L("FarmCard_Twin_Format", "The same books return {0} and on Sunday"), L($"FarmCard_On_{twin}", ((DayOfWeek)twin).ToString()));
            Tip = L($"FarmCard_Tip_{TipIndex(model)}", "");

            BooksLabel = model.IsSunday ? L("FarmCard_BooksMine", "Your books today") : L("FarmCard_Books", "Today's books");
            Books = model.Books.Select(b => new BookVm(b, b.Mine > 0
                ? string.Format(culture, L("FarmCard_Mine_Format", "Your characters: {0}"), b.Mine) : "")).ToList();

            string levelFormat = L("FarmCard_Level_Format", "Lv. {0}");
            string inGame = L("FarmCard_InGame_Format", "In game {0} → {1}");
            Rows = model.Rows.Select(r => new RowVm(r, string.Format(culture, levelFormat, r.Level),
                r.Steps.Select(s => new StepVm(s, L($"FarmCard_Talent_{s.Kind}", s.Kind),
                    s.Bonus > 0 ? string.Format(culture, inGame, s.Level + s.Bonus, s.Target + s.Bonus) : "",
                    L("FarmCard_Top", "Level this one first"))).ToList())).ToList();
            MoreText = model.MoreRows > 0 ? string.Format(culture, L("FarmCard_More_Format", "+{0} more on the full list"), model.MoreRows) : "";

            PlayerMessage = model.Player switch
            {
                FarmPlayerState.NoUid => L("FarmCard_NoUid", "Add your UID in Settings to see your characters"),
                FarmPlayerState.Unavailable => L("FarmCard_Unavailable", "Your showcase can't be read right now. Today's books are above."),
                FarmPlayerState.ShowcaseEmpty => L("FarmCard_ShowcaseEmpty", "Your showcase is empty or hidden. In the game: Profile › Character Showcase."),
                FarmPlayerState.NothingToday => L("FarmCard_NothingToday", "None of your showcase characters needs today's books."),
                FarmPlayerState.UnknownUid => L("FarmCard_UnknownUid", "No player has this UID. Check it in Settings › Characters."),
                FarmPlayerState.CatalogMissing => L("FarmCard_CatalogMissing", "Your characters will show once Kaption has downloaded the character data."),
                _ => "",
            };
            AddUidText = model.Player == FarmPlayerState.UnknownUid ? L("FarmCard_CheckUid", "Check UID") : L("FarmCard_AddUid", "Add UID");
        }

        /// <summary>The weekday opening the same domains (Monday and Thursday...); Sunday has none (-1).</summary>
        public static int Twin(int weekday) => weekday == 0 ? -1 : weekday <= 3 ? weekday + 3 : weekday - 3;

        // One of the tips, turning over with the day so a regular player sees each in turn.
        private static int TipIndex(FarmCardModel m) => (int)(m.Reset.ToUnixTimeSeconds() / 86400 % 4) + 1;

        private static string Duration(TimeSpan t, CultureInfo culture, Func<string, string, string> L) =>
            t.TotalHours >= 1
                ? string.Format(culture, L("FarmCard_Hours_Format", "{0} h"), (int)Math.Floor(t.TotalHours))
                : string.Format(culture, L("FarmCard_Minutes_Format", "{0} min"), Math.Max(1, (int)Math.Ceiling(t.TotalMinutes)));

        private static Brush Frame(Color top, Color bottom)
        {
            var b = new LinearGradientBrush(top, bottom, 90);
            b.Freeze();
            return b;
        }

        public sealed record WeekCell(string Letter, bool IsToday, bool IsSame);

        public sealed class BookVm
        {
            public BookVm(FarmBook b, string mineText)
            {
                Book = b;
                MineText = mineText;
                Tooltip = string.Join("\n", new[] { b.Region }.Concat(b.Items).Concat(mineText.Length > 0 ? new[] { mineText } : Array.Empty<string>()));
            }
            public FarmBook Book { get; }
            public string Series => Book.Series;
            public string Region => Book.Region;
            public string IconPath => Book.IconPath;
            public int Mine => Book.Mine;
            public bool HasMine => Book.Mine > 0;
            public string MineText { get; }
            public string Tooltip { get; }
            public Brush Frame => TierBrush(2);
        }

        public sealed class RowVm
        {
            public RowVm(FarmRow r, string levelText, IReadOnlyList<StepVm> steps)
            {
                Row = r;
                LevelText = levelText;
                Steps = steps;
                var hue = ElementPalette.For("Genshin", r.Element);
                ElementBrush = ElementPalette.Brush(hue);
            }
            public FarmRow Row { get; }
            public string Name => Row.Name;
            public string Initial => Row.Initial;
            public string AvatarPath => Row.IconPath;
            public string LevelText { get; }
            public Brush ElementBrush { get; }
            public IReadOnlyList<StepVm> Steps { get; }
        }

        public sealed class StepVm
        {
            public StepVm(FarmStep s, string talentName, string inGame, string topText)
            {
                Step = s;
                Levels = $"{s.Level} → {s.Target}";
                BonusText = s.Bonus > 0 ? "+" + s.Bonus.ToString(CultureInfo.InvariantCulture) : "";
                CountText = "×" + s.Count.ToString(CultureInfo.InvariantCulture);
                Tooltip = string.Join("\n", new[] { talentName, s.IsTop ? topText : "", inGame, $"{s.Item} ×{s.Count}", s.Crown ? "Crown of Insight ×1" : "" }
                    .Where(x => x.Length > 0));
            }
            public FarmStep Step { get; }
            public string Short => Step.Short;
            public string Levels { get; }
            public string BonusText { get; }
            public bool HasBonus => BonusText.Length > 0;
            public string CountText { get; }
            public bool IsTop => Step.IsTop;
            public bool Crown => Step.Crown;
            public string IconPath => Step.IconPath;
            public Brush Frame => TierBrush(Step.Tier);
            public string Tooltip { get; }
        }
    }
}
