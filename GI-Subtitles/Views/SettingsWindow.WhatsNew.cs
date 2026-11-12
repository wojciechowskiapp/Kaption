using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Services.Notices.WhatsNew;
using GI_Subtitles.Services.Stamina;
using GI_Subtitles.Services.Startup;
using GI_Subtitles.Services.Today;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Start › "New in Kaption": after an update the page leads with the two or three things that matter to this
    /// player, and asks, once, where a thing is theirs to switch on. The rules and the memory are
    /// Services/Notices/WhatsNew; the entries are Resources/WhatsNew/entries.json; the card is Views/WhatsNewLead.
    /// The same card is the whole list when the player opens it from Help. The two switches on Gra i napisy (this
    /// card, and the notice of the day) live here too.
    /// </summary>
    public partial class SettingsWindow
    {
        private readonly IWhatsNewMemory _whatsNewMemory = ConfigWhatsNewMemory.Default;
        private WhatsNewPlan _whatsNewPlan = WhatsNewPlan.None;
        private bool _whatsNewPresented, _whatsNewFresh, _syncingNoticeToggles;

        /// <summary>The card is up with a release's rows (not the list): this start's notice, when the window opened.</summary>
        internal bool WhatsNewLeadUp => WhatsNewLeadCard != null && WhatsNewLeadCard.Visibility == Visibility.Visible && !WhatsNewLeadCard.IsList;

        /// <summary>The card had rows for this player when the app started: whatever happened to it since, it was this start's notice.</summary>
        internal bool WhatsNewLedThisStart { get; private set; }

        internal static bool WhatsNewIsFreshInstall(bool setupRanThisLaunch, bool setupCompleted, bool setupDismissed) =>
            setupRanThisLaunch || (!setupCompleted && !setupDismissed);

        /// <summary>The card was really on screen in this run (the window visible on Start), so the player has been told.</summary>
        internal bool WhatsNewPresentedThisRun => _whatsNewPresented;

        /// <summary>The release the waiting rows belong to; null when nothing waits.</summary>
        internal Version WhatsNewLevel => _whatsNewPlan.State == WhatsNewPlanState.Show ? _whatsNewPlan.Level : null;

        /// <summary>The waiting rows' titles, for the tray's one line on a start without a window.</summary>
        internal IReadOnlyList<string> WhatsNewTitles =>
            _whatsNewPlan.State == WhatsNewPlanState.Show ? BuildWhatsNewRows(_whatsNewPlan.Rows).Select(r => r.Title).ToList() : Array.Empty<string>();

        private void InitializeWhatsNewLead()
        {
            if (WhatsNewLeadCard == null) return;
            // A setup that ran in this launch, or has not run yet: a new player, who starts from this release. A player
            // who closed the setup guide and uses Kaption anyway is not new.
            _whatsNewFresh = WhatsNewIsFreshInstall(App.FirstRunOutcome != OnboardingWindow.Outcome.None,
                Config.Get("SetupCompleted", false), Config.Get("SetupDismissed", false));
            WhatsNewLeadCard.TurnOnRequested += row => AnswerWhatsNew(row, WhatsNewAnswer.On);
            WhatsNewLeadCard.NotNowRequested += row => AnswerWhatsNew(row, WhatsNewAnswer.Later);
            WhatsNewLeadCard.NeverRequested += row => AnswerWhatsNew(row, WhatsNewAnswer.Never);
            WhatsNewLeadCard.ActionRequested += RunWhatsNewAction;
            WhatsNewLeadCard.SwitchRequested += (row, on) =>
            {
                SetWhatsNewFeature(row.Entry.Feature, on, "the news list");
                // Every row that names the thing, not only the one clicked.
                WhatsNewLeadCard.SyncSwitches(WhatsNewFeatureOn);
            };
            WhatsNewLeadCard.CloseRequested += CloseWhatsNew;
            WhatsNewLeadCard.Emptied += RetireWhatsNew;

            SyncNoticeToggles();
            ConfigWhatsNewMemory.EnabledChanged += OnNoticeSettingsChanged;
            DayNoticeSettings.Changed += OnNoticeSettingsChanged;
            StaminaSettings.Changed += OnNoticeSettingsChanged;
            UiLanguage.Changed += OnWhatsNewLanguageChanged;
            NavTabs.SelectionChanged += OnWhatsNewTabChanged;
            IsVisibleChanged += (_, __) => MarkWhatsNewPresented();
            StateChanged += (_, __) => MarkWhatsNewPresented();
            Loaded += (_, __) => MarkWhatsNewPresented();
            Closed += (_, __) =>
            {
                ConfigWhatsNewMemory.EnabledChanged -= OnNoticeSettingsChanged;
                DayNoticeSettings.Changed -= OnNoticeSettingsChanged;
                StaminaSettings.Changed -= OnNoticeSettingsChanged;
                UiLanguage.Changed -= OnWhatsNewLanguageChanged;
            };
            PlanWhatsNewLead();
            WhatsNewLedThisStart = WhatsNewLeadUp;
        }

        private void OnWhatsNewLanguageChanged(object sender, EventArgs e)
        {
            if (WhatsNewLeadCard == null || WhatsNewLeadCard.Visibility != Visibility.Visible) return;
            if (WhatsNewLeadCard.IsList) ShowWhatsNewList(scroll: false);
            else ShowWhatsNewRows();
        }

        private void OnWhatsNewTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, NavTabs)) return;
            MarkWhatsNewPresented();
            // Back on Start with the list open: a switch flipped on another page shows as it is now.
            SyncWhatsNewSwitches();
        }

        // ── The plan ────────────────────────────────────────────────────

        private WhatsNewInputs WhatsNewInputsNow()
        {
            GameManifest game = GameRegistry.Current.Find(Config.Get("Game", "Genshin"));
            return new WhatsNewInputs
            {
                Enabled = _whatsNewMemory.Enabled,
                SeenThrough = _whatsNewMemory.SeenThrough,
                FreshInstall = _whatsNewFresh,
                Game = game?.Id,
                Answered = feature => _whatsNewMemory.AnswerOf(feature) != null,
                FeatureOn = WhatsNewFeatureOn,
                Meets = requirement => WhatsNewMeets(requirement, game),
            };
        }

        private void PlanWhatsNewLead()
        {
            _whatsNewPlan = WhatsNewPlanner.Lead(WhatsNewCatalog.Default, WhatsNewInputsNow());
            switch (_whatsNewPlan.State)
            {
                case WhatsNewPlanState.Baseline:
                    _whatsNewMemory.SeenThrough = _whatsNewPlan.Level;
                    Logger.Log.Info($"New in Kaption: a new install starts from {_whatsNewPlan.Level}.");
                    break;
                case WhatsNewPlanState.Retire:
                    RetireWhatsNew();
                    break;
                case WhatsNewPlanState.Show:
                    ShowWhatsNewRows();
                    break;
            }
        }

        private IReadOnlyList<WhatsNewRow> BuildWhatsNewRows(IEnumerable<WhatsNewEntry> entries)
        {
            GameManifest game = GameRegistry.Current.Find(Config.Get("Game", "Genshin"));
            return WhatsNewRows.Build(entries, UiLanguage.Current == UiLanguage.Polish, WhatsNewValues(game, L, CultureInfo.CurrentUICulture), WhatsNewFeatureOn);
        }

        private void ShowWhatsNewRows()
        {
            // Drawn again (another language, back from the list): a question answered meanwhile is not asked again.
            // One that was turned on from the card keeps its row, as the line that says so; the others are gone.
            bool TurnedOn(WhatsNewRow r) => r.IsAsk && r.FeatureOn && _whatsNewMemory.AnswerOf(r.Entry.Feature) == WhatsNewAnswer.On;
            var rows = BuildWhatsNewRows(_whatsNewPlan.Rows)
                .Where(r => !r.IsAsk || TurnedOn(r) || (!r.FeatureOn && _whatsNewMemory.AnswerOf(r.Entry.Feature) == null)).ToList();
            if (rows.Count == 0)
            {
                RetireWhatsNew();
                return;
            }
            WhatsNewLeadCard.Apply(rows, list: false, L);
            foreach (WhatsNewRow row in rows.Where(TurnedOn)) WhatsNewLeadCard.MarkDone(row);
            // One once-card at a time, and this one leads: the 2.5 card and the spotlight come back when it is gone.
            if (Intro25Card != null) Intro25Card.Visibility = Visibility.Collapsed;
            if (SpotlightCard != null) SpotlightCard.Visibility = Visibility.Collapsed;
            if (!_whatsNewPresented) Logger.Log.Info($"New in Kaption {_whatsNewPlan.Level}: {string.Join(", ", rows.Select(r => r.Entry.Id))}.");
            MarkWhatsNewPresented();
        }

        /// <summary>
        /// Once per release: the first time the card is really on screen (the window visible, not minimised, on Start)
        /// its release counts as shown, so no later start leads with it again. The card stays for this run; the list
        /// in Help keeps everything, questions the player left open included. A start with Windows opens no window,
        /// so there the card waits.
        /// </summary>
        private void MarkWhatsNewPresented()
        {
            if (_whatsNewPresented || !WhatsNewLeadUp || !IsVisible || WindowState == WindowState.Minimized) return;
            if ((NavTabs?.SelectedItem as TabItem)?.Tag as string != SettingsNavigation.Home) return;
            _whatsNewPresented = true;
            _whatsNewMemory.SeenThrough = _whatsNewPlan.Level;
            Logger.Log.Info($"New in Kaption {_whatsNewPlan.Level}: on screen; later starts do not lead with it.");
        }

        /// <summary>This release's news is done with: nothing of it leads Start again. The list still has everything.</summary>
        private void RetireWhatsNew()
        {
            if (_whatsNewPlan.Level != null) _whatsNewMemory.SeenThrough = _whatsNewPlan.Level;
            _whatsNewPlan = WhatsNewPlan.None;
            if (WhatsNewLeadCard != null && !WhatsNewLeadCard.IsList) WhatsNewLeadCard.Apply(null, false, L);
            ShowIntro25IfNew();
        }

        // ── What the card's buttons do ──────────────────────────────────

        private void AnswerWhatsNew(WhatsNewRow row, WhatsNewAnswer answer)
        {
            // "Turn on": the switch first, then the answer. A stop between the two leaves the thing on, and a thing
            // that is on is not asked about; the other order could leave "answered" with nothing turned on.
            if (answer == WhatsNewAnswer.On) SetWhatsNewFeature(row.Entry.Feature, true, "the news card");
            _whatsNewMemory.Answer(row.Entry.Feature, answer);
            Logger.Log.Info($"New in Kaption: {row.Entry.Id} answered {answer}.");
            // Only a switch that really took says so; one that did not (Windows refused the entry) just leaves.
            if (answer == WhatsNewAnswer.On && WhatsNewFeatureOn(row.Entry.Feature)) WhatsNewLeadCard.MarkDone(row);
            else WhatsNewLeadCard.Remove(row);
        }

        private void RunWhatsNewAction(WhatsNewRow row)
        {
            if (row.Entry.Action == WhatsNewActions.TodayCard) OnShowFarmCard?.Invoke();
        }

        private void CloseWhatsNew()
        {
            if (WhatsNewLeadCard.IsList)
            {
                // Back to what Start showed before the list: this release's rows when they still wait, else nothing.
                WhatsNewLeadCard.Apply(null, false, L);
                if (_whatsNewPlan.State == WhatsNewPlanState.Show) ShowWhatsNewRows();
                return;
            }
            Logger.Log.Info($"New in Kaption {_whatsNewPlan.Level}: closed by the player.");
            RetireWhatsNew();
        }

        /// <summary>Help › "Show what's new": every entry for this game, each with its switch, on Start.</summary>
        private void ShowWhatsNewList(bool scroll = true)
        {
            if (WhatsNewLeadCard == null) return;
            var rows = BuildWhatsNewRows(WhatsNewPlanner.List(WhatsNewCatalog.Default, WhatsNewInputsNow()));
            WhatsNewLeadCard.Apply(rows, list: true, L);
            if (!scroll) return;
            SelectTabByTag(SettingsNavigation.Home);
            StartScroller?.ScrollToTop();
        }

        private void WhatsNewList_Click(object sender, RoutedEventArgs e) => ShowWhatsNewList();

        // ── The switches an entry can name ──────────────────────────────

        /// <summary>Whether the thing an entry names is on now, read where the app itself reads it.</summary>
        internal static bool WhatsNewFeatureOn(string feature) => feature switch
        {
            WhatsNewFeatures.StaminaNotice => StaminaSettings.Enabled && StaminaSettings.Notice,
            WhatsNewFeatures.StartWithWindows => Config.Get(WindowsStartup.SettingKey, false),
            WhatsNewFeatures.ShowInRecordings => CaptureVisibility.ShowInRecordings,
            WhatsNewFeatures.DayNotice => DayNoticeSettings.Enabled,
            _ => false,
        };

        /// <summary>Flips the thing the way its own switch in Settings does, and lets those switches follow.</summary>
        private void SetWhatsNewFeature(string feature, bool on, string where)
        {
            switch (feature)
            {
                case WhatsNewFeatures.StaminaNotice:
                    // Reading the number is the consent; the notice is the point of asking. Off here is the notice only.
                    // The notice's own switch first: reading is what makes the pair count as on, so it is the last write.
                    StaminaSettings.Notice = on;
                    if (on) StaminaSettings.Enabled = true;
                    break;
                case WhatsNewFeatures.StartWithWindows:
                    Config.Set(WindowsStartup.SettingKey, on);
                    WindowsStartup.ApplySetting(on);
                    SyncAutoStartControls();
                    break;
                case WhatsNewFeatures.ShowInRecordings:
                    Config.Set(CaptureVisibility.ShowInRecordingsKey, on);
                    if (Application.Current?.MainWindow is MainWindow overlay) overlay.ApplyCaptureVisibility();
                    _syncingLook = true;
                    try { SyncRecordingCard(); }
                    finally { _syncingLook = false; }
                    break;
                case WhatsNewFeatures.DayNotice:
                    DayNoticeSettings.Enabled = on;
                    break;
                default:
                    return;
            }
            Logger.Log.Info($"{feature} turned {(on ? "on" : "off")} on {where}.");
        }

        /// <summary>Whether the selected game has what an entry needs.</summary>
        internal static bool WhatsNewMeets(string requirement, GameManifest game) => requirement switch
        {
            WhatsNewRequirements.StaminaScreen => StaminaCardModel.Supports(game),
            WhatsNewRequirements.TodayCard => TodayCardSettings.HasCard(game),
            _ => false,
        };

        /// <summary>The values an entry's words may name, for the selected game; one without a value is simply absent.</summary>
        internal static IReadOnlyDictionary<string, string> WhatsNewValues(GameManifest game, Func<string, string, string> l, CultureInfo culture)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (game == null) return values;
            values["game"] = game.Name;
            if (StaminaCardModel.Supports(game))
            {
                values["stamina"] = game.Stamina.Name;
                values["staminaWhere"] = l("Stamina_Where_" + game.GameId, l("Stamina_Where_Other", "It shows beside the stamina icon in the game."));
                values["staminaLead"] = StaminaText.Duration(StaminaSettings.NoticeLead, l);
            }
            return values;
        }

        // ── Gra i napisy: the two switches ──────────────────────────────

        private void OnNoticeSettingsChanged()
        {
            if (Dispatcher.HasShutdownStarted) return;
            if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke(new Action(SyncNoticeToggles));
            else SyncNoticeToggles();
        }

        private void SyncNoticeToggles()
        {
            _syncingNoticeToggles = true;
            try
            {
                if (WhatsNewToggle != null) WhatsNewToggle.IsChecked = _whatsNewMemory.Enabled;
                if (DayNoticeToggle != null) DayNoticeToggle.IsChecked = DayNoticeSettings.Enabled;
            }
            finally
            {
                _syncingNoticeToggles = false;
            }
            SyncWhatsNewSwitches();
        }

        /// <summary>The list's switches as the things really are (the list from Help only: the card has none).</summary>
        private void SyncWhatsNewSwitches()
        {
            if (WhatsNewLeadCard != null && WhatsNewLeadCard.IsList && WhatsNewLeadCard.Visibility == Visibility.Visible)
                WhatsNewLeadCard.SyncSwitches(WhatsNewFeatureOn);
        }

        private void WhatsNewToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || _syncingNoticeToggles) return;
            bool on = WhatsNewToggle.IsChecked == true;
            _whatsNewMemory.Enabled = on;
            Logger.Log.Info($"New in Kaption after an update turned {(on ? "on" : "off")}.");
            // Off takes the waiting card with it, and its release counts as told: also while the list from Help is
            // open in its place, so closing the list cannot bring the card back.
            if (!on && _whatsNewPlan.State == WhatsNewPlanState.Show) RetireWhatsNew();
        }

        private void DayNoticeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || _syncingNoticeToggles) return;
            SetWhatsNewFeature(WhatsNewFeatures.DayNotice, DayNoticeToggle.IsChecked == true, "Gra i napisy");
        }
    }
}
