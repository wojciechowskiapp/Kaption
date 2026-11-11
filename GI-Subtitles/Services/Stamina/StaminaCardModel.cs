using System;
using System.Globalization;
using GI_Subtitles.Services.Games;

namespace GI_Subtitles.Services.Stamina
{
    public enum StaminaCardState
    {
        /// <summary>No card: the game has no stamina, or its screen is not measured yet.</summary>
        Hidden,
        /// <summary>The player has not switched the reader on: one sentence that says what it would read.</summary>
        Off,
        /// <summary>Switched on, no number read yet: where the number shows.</summary>
        Waiting,
        /// <summary>A reading: the value now, when it is full, how fresh the read is.</summary>
        Tracking,
    }

    /// <summary>What the Start page's stamina card shows; every text is final.</summary>
    public sealed record StaminaCardView(
        StaminaCardState State,
        string PoolName,
        string Number,   // "143/200" by the timer; null without a reading
        string Line,
        string Fresh,    // "Read from the game 5 min ago."; null without a reading
        string TimerUrl,
        string ToggleLabel,
        bool NoticeOn,
        string NoticeHint)
    {
        public static readonly StaminaCardView None = new(StaminaCardState.Hidden, null, null, null, null, null, null, false, null);
        public bool Visible => State != StaminaCardState.Hidden;
        public bool On => State is StaminaCardState.Waiting or StaminaCardState.Tracking;
    }

    /// <summary>Builds the card for the selected game. Pure: the caller supplies the time and the strings.</summary>
    public static class StaminaCardModel
    {
        /// <summary>How often the card is drawn again while it is on screen: its texts count minutes.</summary>
        public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

        /// <summary>Whether the app can read this game's stamina at all (the card exists only then).</summary>
        public static bool Supports(GameManifest game) =>
            game?.Stamina != null && game.Desktop?.StaminaScreen != null && !string.IsNullOrWhiteSpace(game.Desktop.StaminaScreen.Icon);

        /// <param name="seenAt">When the number on screen last agreed with the timer; null when not since the reading.</param>
        public static StaminaCardView Build(GameManifest game, bool enabled, bool noticeOn, TimeSpan noticeLead, StaminaReading reading,
            DateTimeOffset? seenAt, DateTimeOffset now, string uiLanguage, Func<string, string, string> l, CultureInfo culture)
        {
            if (!Supports(game)) return StaminaCardView.None;
            culture ??= CultureInfo.CurrentUICulture;
            GameStamina pool = game.Stamina;
            string toggle = string.Format(culture, l("Stamina_Toggle", "Read {0} from the game screen"), pool.Name);
            string noticeHint = string.Format(culture, l("Stamina_Notice_Hint", "One notice, {0} before it fills, never over the game."),
                StaminaText.Duration(noticeLead, l));

            if (!enabled)
            {
                return new StaminaCardView(StaminaCardState.Off, pool.Name, null,
                    string.Format(culture, l("Stamina_Off", "Kaption can read your {0} from the game screen while subtitles are running, and keep a timer for you. It reads that one number and keeps it on this PC."), pool.Name),
                    null, null, toggle, noticeOn, noticeHint);
            }
            if (reading == null)
            {
                string where = l("Stamina_Where_" + game.GameId, l("Stamina_Where_Other", "It shows beside the stamina icon in the game."));
                return new StaminaCardView(StaminaCardState.Waiting, pool.Name, null,
                    string.Format(culture, l("Stamina_Waiting", "Waiting for the number. {0} Kaption reads it while subtitles are running."), where),
                    null, StaminaLinks.TimerUrl(game, uiLanguage), toggle, noticeOn, noticeHint);
            }

            int value = StaminaClock.ValueAt(pool, reading, now);
            DateTimeOffset full = StaminaClock.FullAt(pool, reading);
            string line = value >= pool.Max
                ? string.Format(culture, l("Stamina_FullSince", "Full. It filled {0}."), StaminaText.Moment(full, now, l, culture))
                : string.Format(culture, l("Stamina_FullAt", "Full {0}, in {1}."), StaminaText.Moment(full, now, l, culture), StaminaText.Duration(full - now, l));
            DateTimeOffset read = seenAt is DateTimeOffset seen && seen > reading.At ? seen : reading.At;
            TimeSpan age = now - read;
            string fresh = age < TimeSpan.FromMinutes(1)
                ? l("Stamina_Read_Now", "Read from the game just now.")
                : string.Format(culture, l("Stamina_Read_Ago", "Read from the game {0} ago."), StaminaText.Duration(age, l));
            return new StaminaCardView(StaminaCardState.Tracking, pool.Name,
                string.Create(CultureInfo.InvariantCulture, $"{value}/{pool.Max}"), line, fresh,
                StaminaLinks.TimerUrl(game, uiLanguage, reading, seenAt), toggle, noticeOn, noticeHint);
        }
    }
}
