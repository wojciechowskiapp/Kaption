using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Core.Input;
using GI_Subtitles.Services.Assist;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Diagnostics;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Hints;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Services.Rendering;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The in-game hint card's seat in the app (.plan/research/s68/HINTS-AND-MORE-TRANSLATION.md, Part B): it reads
    /// what the app knows once a second (and at once on a key), asks <see cref="HintPlanner"/> what to do, finds a
    /// place with <see cref="HintPlacement"/>, and shows or hides the two windows. It also answers the Start card's
    /// check, which asks here before it sends its tray balloon. The card never takes a click or the focus from the
    /// game and never starts or stops translation; nothing here reads the screen or touches the game. Everything
    /// stays on this PC.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>A pinned sheet goes to its resting strength after this long without a pointer on it.</summary>
        private const int HintRestAfterMs = 6000;
        /// <summary>How long "Don't show again" stays after a sheet was hidden.</summary>
        private const int HintNeverMs = 5000;
        /// <summary>DIPs between the card and its strip.</summary>
        private const double HintStripGapDip = 6;
        private static readonly TimeSpan HintIdleTick = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan HintUpTick = TimeSpan.FromMilliseconds(HintInteractivity.TickMs);

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize, flags;
            public IntPtr hCursor;
            public System.Drawing.Point ptScreenPos;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CURSORINFO info);
        private const int CURSOR_SHOWING = 0x00000001;

        private DispatcherTimer _hintTimer;
        private HintCardWindow _hintCard;
        private HintStripWindow _hintStrip;
        private HintTally _hintTally = new();

        // Settings and shortcuts as last read: the tick never goes to Config or to the shortcuts file.
        private bool _hintsBlocked, _hintsEnabled, _hintsPinned;
        private HintPlace _hintsPlace;
        private string _hintsGame, _hintsWireId;
        private HintBindings? _hintBindings;

        private HintState _hintState;
        private HintRun _hintRun;
        private readonly List<int> _hintKeysBeforeState = new();

        // What is up.
        private HintShowing _hintShowing;
        private HintEntry _hintEntry;
        private HintCardViewModel _hintModel;
        private System.Windows.Size _hintFullDip, _hintCompactDip;
        private HintPlaced _hintPlaced;
        private double _hintSecondsLeft, _hintSecondsTotal;
        private long _hintLastStepMs, _hintFullSinceMs, _hintShownAtMs, _hintNeverUntilMs;
        private HintPointerState _hintPointer;
        /// <summary>
        /// A welcome sheet or a tip is up and nobody has had the time to read it yet (HintPlanner.CountsAfterSeconds):
        /// it is not counted, and taken down now it comes again.
        /// </summary>
        private bool _hintUncounted;
        /// <summary>The tip that was taken down with a fade; it is on screen until the card says it is no longer up.</summary>
        private HintEntry _hintLeaving;
        // The card that had no place at the last try, and the two sizes measured for it then: while it still has
        // none, the next look places with those and builds and measures nothing.
        private (HintShowing What, HintEntry Entry)? _hintNoPlaceFor;
        private (System.Windows.Size Full, System.Windows.Size Compact)? _hintNoPlaceSizes;

        // The scene the card was placed in; the parts that cost a Config read are kept while the game's window and
        // the dialogue area stay where they were.
        private Rectangle _hintClient, _hintDialogue, _hintReserve;
        private List<Rectangle> _hintGameAreas;
        private double _hintScale = 1;
        private bool _hintInGame;

        private bool _hintWasTranslating, _hintLineWasUp;
        private long? _hintLineGoneAtMs, _hintHiddenEchoAtMs;
        private (HintEntry Tip, long AtMs)? _hintLastTip;

        // What the tips wait for. When the hide shortcut hid the subtitles (null while they show, and when Kaption's
        // own window hid them: that player knows); when a line was last matched; lines painted in this run.
        private long? _hintHiddenByKeyAtMs, _hintMatchedAtMs;
        private int _hintMatchedReads, _hintLinesShown;

        private void InitHints()
        {
            _hintsBlocked = !HintSettings.AllowedInThisMode;
            if (_hintsBlocked) return; // the end-to-end mode: no window, no timer, nothing written
            LoadHintSettings();
            HintSettings.Changed += OnHintSettingsChanged;
            _hintTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = HintIdleTick };
            _hintTimer.Tick += (_, _) => StepHints(HintRequest.None);
            if (data != null)
            {
                data.OnShowHintSheet = () => RequestHint(HintRequest.Sheet);
                data.OnHotkeysRegistered = () =>
                {
                    _hintBindings = null;
                    _hintNoPlaceSizes = null; // the sheet's rows, and so its size, follow the shortcuts
                };
                data.AskHintCard = AnswerSessionCheck;
            }
            DiagnosticBundle.RegisterStateProvider("Hints", DescribeHints);
            Closed += (_, _) =>
            {
                HintSettings.Changed -= OnHintSettingsChanged;
                _hintTimer?.Stop();
                try { _hintStrip?.Close(); } catch (InvalidOperationException) { /* closing with the app */ }
                try { _hintCard?.Close(); } catch (InvalidOperationException) { /* closing with the app */ }
            };
        }

        private void OnHintSettingsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(OnHintSettingsChanged));
                return;
            }
            LoadHintSettings();
        }

        private void LoadHintSettings()
        {
            _hintsEnabled = HintSettings.Enabled;
            _hintsPinned = HintSettings.Pinned;
            _hintsGame = Config.Get<string>("Game", Game ?? "Genshin");
            _hintsWireId = GameRegistry.Current.Find(_hintsGame)?.Id;
            _hintsPlace = HintSettings.Place(_hintsWireId);
            _hintNoPlaceSizes = null; // a setting or a key: the next try for a place starts from a fresh look
        }

        private HintBindings HintBindingsNow => _hintBindings ??= HintBindings.From(HotkeySettingsManager.DescribeBinding);

        /// <summary>Diagnostics: on or off, pinned, the place and what the card remembers. No text the player read.</summary>
        private string DescribeHints() =>
            $"{(_hintsEnabled ? "on" : "off")}, pinned {(_hintsPinned ? "yes" : "no")}, place {HintPlacement.NameOf(_hintsPlace)} ({_hintsWireId ?? "no game"}), "
            + $"card {(_hintCard == null ? "not shown yet" : "Windows: " + _hintCard.HideAnswer)}, state {_hintState?.ToJson().ToJsonString() ?? "not loaded"}";

        // ── What the rest of the window tells the card ─────────────────────────────────────────────

        /// <summary>Translation started: a new run for the card, and the timer that looks once a second.</summary>
        private void OnHintRunStarted()
        {
            // A card that failed starts no run of its own. What it said belongs to the run before: asked in this
            // one, it has said nothing, and the balloon goes out as it did before there was a card.
            if (_hintsBlocked) _hintRun = null;
            GuardHints(StartHintRun);
        }

        private void StartHintRun()
        {
            var now = DateTimeOffset.Now;
            if (_hintState == null)
            {
                // The first start is the moment that tells a new install (setup done, no line ever) from an old one.
                HintState loaded = HintSettings.LoadState(Config.Get(SettingsWindow.FirstLinePendingKey, false), now);
                HintState state = loaded;
                foreach (int key in _hintKeysBeforeState) state = state.WithKeyUsed(key);
                _hintKeysBeforeState.Clear();
                if (!ReferenceEquals(state, loaded)) HintSettings.SaveState(state);
                _hintState = state;
            }
            _hintRun = HintRun.Start(now);
            _hintLineWasUp = false;
            _hintLineGoneAtMs = null;
            _hintNoPlaceFor = null;
            // StartOcrTimers has just emptied the session's counters: the matched lines count from nothing again.
            _hintMatchedReads = 0;
            _hintMatchedAtMs = null;
            _hintLinesShown = 0;
            LoadHintSettings(); // the game may have changed since the last run
            _hintTimer.Start();
        }

        /// <summary>A shortcut was pressed: remembered, so a player is never taught a key they already use.</summary>
        private void NoteHintKey(int keyId) => GuardHints(() => NoteHintKeyCore(keyId));

        private void NoteHintKeyCore(int keyId)
        {
            if (_hintLastTip is { } tip && tip.Tip.KeyId == keyId && Environment.TickCount64 - tip.AtMs <= HintTally.TipKeyWithinMs)
            {
                _hintTally.TipKeyUsed();
                _hintLastTip = null;
            }
            if (_hintState == null)
            {
                if (!_hintKeysBeforeState.Contains(keyId)) _hintKeysBeforeState.Add(keyId);
                return;
            }
            HintState state = _hintState.WithKeyUsed(keyId);
            if (ReferenceEquals(state, _hintState)) return;
            _hintState = state;
            HintSettings.SaveState(state);
        }

        /// <summary>A new line was painted (not a restore of the one before). One that is hidden was not read, and is not counted.</summary>
        private void NoteHintLineShown()
        {
            if (ShowText) _hintLinesShown++;
        }

        /// <summary>The subtitles were shown or hidden in Kaption's own window: that player knows, and is not told.</summary>
        private void OnHintSubtitlesToggledInWindow() => _hintHiddenByKeyAtMs = null;

        /// <summary>The hide shortcut was pressed and <paramref name="shown"/> is what the subtitles are now.</summary>
        private void OnHintSubtitlesToggledByKey(bool shown)
        {
            _hintHiddenByKeyAtMs = shown ? null : Environment.TickCount64;
            if (shown && _hintHiddenEchoAtMs is long at && Environment.TickCount64 - at <= HintTally.UndoneWithinMs) _hintTally.Undone();
            if (shown) _hintHiddenEchoAtMs = null;
            RequestHint(shown ? HintRequest.SubtitlesBack : HintRequest.SubtitlesHidden);
        }

        /// <summary>The player did something the card answers at once. One-shot: it is asked once and not kept.</summary>
        private void RequestHint(HintRequest request) => GuardHints(() =>
        {
            LoadHintSettings(); // the selected game may have changed since the last look; a key press is rare
            StepHintsCore(request);
        });

        /// <summary>
        /// The Start card's check asks before its tray balloon goes out: does the card say it in the game? Asked
        /// once a second while the check is on the Start card and the player is not in Kaption's window;
        /// <paramref name="waitedSeconds"/> is how long it has been asking (the Start card keeps that clock: it
        /// knows when the player was in its window). A check the card gives up on (no free place, or it waited too
        /// long for its moment) is the balloon's from then on, so the two never say the same thing. A card that
        /// failed still answers for what it said before it did.
        /// </summary>
        private HintCheckAnswer AnswerSessionCheck(SessionCheckKind kind, double waitedSeconds)
        {
            if (_hintTimer == null) return HintCheckAnswer.Cannot; // the end-to-end mode: there is no card
            try
            {
                bool noPlace = _hintNoPlaceFor is (HintShowing.Tip, HintEntry missed) && ReferenceEquals(missed, HintPlanner.TipFor(kind));
                var signals = new HintSignals
                {
                    Blocked = _hintsBlocked, Enabled = _hintsEnabled, Translating = OCRTimer.IsEnabled, Showing = _hintShowing, ShowingEntry = _hintEntry,
                    // Taken down a moment ago and still fading: on screen, so the balloon waits for that too.
                    LeavingEntry = _hintCard?.IsUp == true ? _hintLeaving : null,
                    AreaHold = _areaHold,
                };
                HintCheckAnswer answer = HintPlanner.CheckAnswer(kind, signals, _hintRun, noPlace, waitedSeconds);
                if (answer == HintCheckAnswer.Cannot) _hintRun = HintPlanner.CheckLeftToTray(_hintRun, kind);
                return answer;
            }
            catch (Exception ex)
            {
                if (!_hintsBlocked) HintsFailed(ex); // asked every second: a card that is off already has said so
                return HintCheckAnswer.Cannot;
            }
        }

        /// <summary>
        /// Everything the rest of the app calls here goes through this, and so does every button of the strip: a
        /// hint is never worth a crash or a stuck card.
        /// </summary>
        private void GuardHints(Action step)
        {
            if (_hintsBlocked || _hintTimer == null) return;
            try
            {
                step();
            }
            catch (Exception ex)
            {
                HintsFailed(ex);
            }
        }

        /// <summary>
        /// Said once, with the stack, for a person to look at; the card stays away until Kaption starts again
        /// rather than fail on every look.
        /// </summary>
        private void HintsFailed(Exception ex)
        {
            Logger.Log.Error("The hint card failed and is off until the next start.", ex);
            _hintsBlocked = true;
            _hintTimer?.Stop();
            try
            {
                TakeHintDown(fade: false);
                CloseHintStrip();
            }
            catch (Exception again)
            {
                Logger.Log.Warn($"Hint card: could not be taken down: {again.Message}");
            }
        }

        // ── One look ───────────────────────────────────────────────────────────────────────────────

        /// <summary>The timer's look, once a second: no delegate is made for it.</summary>
        private void StepHints(HintRequest request)
        {
            try
            {
                StepHintsCore(request);
            }
            catch (Exception ex)
            {
                HintsFailed(ex);
            }
        }

        private void StepHintsCore(HintRequest request)
        {
            long nowMs = Environment.TickCount64;
            var now = DateTimeOffset.Now;
            bool translating = OCRTimer.IsEnabled;
            bool stoppedNow = _hintWasTranslating && !translating;
            _hintWasTranslating = translating;

            // A line is up whether or not it is painted: someone is talking. The panel's "ready" line is not one.
            bool lineUp = translating && (!string.IsNullOrEmpty(lastContent) || !string.IsNullOrEmpty(lastHeader));
            if (_hintLineWasUp && !lineUp) _hintLineGoneAtMs = nowMs;
            _hintLineWasUp = lineUp;

            // "Someone is talking": the reading loop found a line since the last look. A count that fell is the
            // session's counters starting over (the dialogue area changed), not a line.
            int matched = _sessionEvidence.MatchedReads;
            if (translating && matched > _hintMatchedReads) _hintMatchedAtMs = nowMs;
            _hintMatchedReads = matched;

            bool up = _hintShowing != HintShowing.None;
            bool held = false;
            if (up)
            {
                held = StepHintPointer(nowMs);
                if (_hintSecondsTotal > 0)
                {
                    if (!held) _hintSecondsLeft -= (nowMs - _hintLastStepMs) / 1000.0;
                    _hintCard.SetClock(_hintSecondsLeft / _hintSecondsTotal);
                    if (_hintSecondsLeft <= 0 && request == HintRequest.None)
                    {
                        TakeHintDown(fade: true);
                        up = false;
                    }
                }
            }
            else if (_hintStrip?.IsVisible == true && nowMs >= _hintNeverUntilMs)
            {
                CloseHintStrip();
            }
            _hintLastStepMs = nowMs;

            bool gameInFront = GetForegroundTarget() == ForegroundTarget.Game;
            var signals = new HintSignals
            {
                Enabled = _hintsEnabled,
                Pinned = _hintsPinned,
                Translating = translating,
                GameInFront = gameInFront,
                OwnWindowUp = HintOwnWindowUp(),
                Showing = _hintShowing,
                ShowingEntry = _hintEntry,
                Request = request,
                LineUp = lineUp,
                SecondsSinceLine = _hintLineGoneAtMs is long gone ? (nowMs - gone) / 1000.0 : null,
                HiddenByKeySeconds = !ShowText && _hintHiddenByKeyAtMs is long hidden ? (nowMs - hidden) / 1000.0 : null,
                SecondsSinceLineMatched = _hintMatchedAtMs is long matchedAt ? (nowMs - matchedAt) / 1000.0 : null,
                LinesShown = _hintLinesShown,
                Check = data?.SessionCheckNow ?? SessionCheckKind.None,
                AreaHold = _areaHold,
                TeachingOn = true,
                Bindings = HintBindingsNow,
            };
            HintState state = _hintState ?? HintState.Create(NoticeGate.DayOf(now), newInstall: false);
            HintDecision decision = HintPlanner.Next(signals, state, _hintRun, now);
            switch (decision.Action)
            {
                case HintAction.Show:
                    ShowHint(decision, gameInFront, now, nowMs);
                    break;
                case HintAction.Hide:
                    TakeHintDown(fade: true);
                    break;
                default:
                    if (up) KeepHintInPlace(gameInFront, held, nowMs);
                    break;
            }
            // Counted by a look that keeps it: whatever took the card down in this look (the planner, the clock, a
            // place lost) has cleared the flag, and a card shown in this look has been up for no time at all.
            if (_hintUncounted && HintPlanner.CountsNow(decision, (nowMs - _hintShownAtMs) / 1000.0)) CountHint(now);

            if (stoppedNow)
            {
                // After the look, so the answer to the stop shortcut is in the run's line.
                if (!_hintTally.IsEmpty) Logger.Log.Info(_hintTally.Describe(_hintsPinned) + ".");
                _hintTally = new HintTally();
            }

            bool busy = _hintShowing != HintShowing.None || _hintStrip?.IsVisible == true;
            if (!busy && !translating) _hintTimer.Stop();
            else
            {
                TimeSpan tick = busy ? HintUpTick : HintIdleTick;
                if (_hintTimer.Interval != tick) _hintTimer.Interval = tick;
                if (!_hintTimer.IsEnabled) _hintTimer.Start();
            }
        }

        /// <summary>
        /// One of Kaption's windows that the card must not show beside: the area picker, the Ctrl+Q popup, the fix
        /// window, the overlap alert, a dialog. A list by name, never "any window of ours": Kaption's main window
        /// behind the game and the dialogue frame of Ctrl+Shift+D are not in it, and neither are the card's own two.
        /// </summary>
        private bool HintOwnWindowUp() =>
            ChooseRegion || _quickTranslateBusy || _quickTranslatePopup?.IsVisible == true
            || _suggestFixWindow?.IsVisible == true || _overlapAlertOverlay?.IsShown == true
            || ComponentDispatcher.IsThreadModal;

        // ── Showing, keeping, hiding ───────────────────────────────────────────────────────────────

        private void ShowHint(HintDecision decision, bool gameInFront, DateTimeOffset now, long nowMs)
        {
            bool wasUp = _hintShowing != HintShowing.None;
            bool sheet = decision.Entry == null;
            // What replaces a card shows where that card was; so does the answer to "Don't show again" on a card just hidden.
            bool whereItWas = wasUp || ReferenceEquals(decision.Entry, HintCatalog.EchoHintsOff);
            HintPlace first = whereItWas ? _hintPlaced.Place : _hintsPlace;
            (HintShowing, HintEntry) card = (decision.What, decision.Entry);
            if (_hintNoPlaceFor == card && _hintNoPlaceSizes is { } kept)
            {
                // The planner asks for a card with no place again every second (a pinned sheet, for the whole run):
                // with the sizes measured then, nothing is built or measured until a place is free.
                HintScene asItWas = BuildHintScene(gameInFront, fresh: false);
                if (!HintPlacement.TryPlace(asItWas, first, HintPixels(kept.Full, asItWas.Scale), HintPixels(kept.Compact, asItWas.Scale), out _)) return;
            }

            HintBindings bindings = HintBindingsNow;
            HintCardViewModel model = sheet
                ? HintCardViewModel.Sheet(bindings, LocalizedString, clock: decision.What != HintShowing.PinnedSheet)
                : HintCardViewModel.Line(decision.Entry, bindings, LocalizedString);

            _hintCard ??= new HintCardWindow();
            var accent = GameAccents.For(_hintsGame);
            _hintCard.SetContent(model, accent);
            HintScene scene = BuildHintScene(gameInFront, fresh: true);
            System.Windows.Size fullDip = _hintCard.MeasureContent(model, compact: false);
            System.Windows.Size compactDip = sheet ? _hintCard.MeasureContent(model, compact: true) : default;
            Size full = HintPixels(fullDip, scene.Scale), compact = HintPixels(compactDip, scene.Scale);
            if (!HintPlacement.TryPlace(scene, first, full, compact, out HintPlaced placed))
            {
                _hintNoPlaceSizes = (fullDip, compactDip);
                // Counted once per card, not once per look: the planner asks again every second while it is due.
                if (_hintNoPlaceFor != card)
                {
                    _hintNoPlaceFor = card;
                    _hintTally.NoPlace();
                    Logger.Log.Info($"Hint card: no free place for {decision.What}{(decision.Entry == null ? "" : " " + decision.Entry.Id)}.");
                }
                if (wasUp) _hintCard.SetContent(_hintModel, accent, _hintPlaced.Compact);
                return;
            }

            CloseHintStrip();
            _hintCard.SetContent(model, accent, placed.Compact);
            _hintCard.Present(placed.Bounds.Location, fade: !wasUp);
            // On screen from now, not from the start of this look: building and placing the card (its window, the
            // first time) is neither reading time nor time off its clock.
            long shownMs = Environment.TickCount64;

            // What was up and not yet counted is replaced uncounted, and comes again.
            _hintRun = HintPlanner.Shown(decision, _hintRun, now);
            _hintLeaving = null; // the card on screen is this one now

            _hintShowing = decision.What;
            _hintEntry = decision.Entry;
            _hintModel = model;
            _hintFullDip = fullDip;
            _hintCompactDip = compactDip;
            _hintPlaced = placed;
            _hintSecondsTotal = decision.What == HintShowing.Tip ? HintCatalog.TipSeconds(model.Message.Length) : decision.Seconds;
            _hintSecondsLeft = _hintSecondsTotal;
            _hintFullSinceMs = _hintShownAtMs = _hintLastStepMs = shownMs;
            _hintUncounted = decision.What is HintShowing.WelcomeSheet or HintShowing.Tip;
            _hintPointer = default;
            _hintNoPlaceFor = null;
            _hintNoPlaceSizes = null;
            _hintTally.Shown(decision.What, decision.Entry, placed.Place);
            if (ReferenceEquals(decision.Entry, HintCatalog.EchoSubtitlesHidden)) _hintHiddenEchoAtMs = nowMs;
            if (decision.What == HintShowing.Tip) _hintLastTip = (decision.Entry, nowMs);
        }

        /// <summary>
        /// A card that is up stays where it is while it touches nothing, moves to a free place when something came
        /// under it (a subtitle, the Ctrl+Q popup, the game's window moved), and goes when there is none.
        /// </summary>
        private void KeepHintInPlace(bool gameInFront, bool held, long nowMs)
        {
            HintScene scene = BuildHintScene(gameInFront, fresh: false);
            if (!HintPlacement.TryKeep(scene, _hintPlaced, HintPixels(_hintFullDip, scene.Scale), HintPixels(_hintCompactDip, scene.Scale), out HintPlaced placed))
            {
                // Counted here; the planner asking for the same card again a second later is not a second miss.
                _hintNoPlaceFor = (_hintShowing, _hintEntry);
                _hintNoPlaceSizes = (_hintFullDip, _hintCompactDip);
                _hintTally.NoPlace();
                TakeHintDown(fade: true);
                return;
            }
            if (placed != _hintPlaced)
            {
                if (placed.Compact != _hintPlaced.Compact) _hintCard.SetContent(_hintModel, GameAccents.For(_hintsGame), placed.Compact);
                _hintPlaced = placed;
                _hintPointer = default;
                CloseHintStrip();
                _hintCard.MoveTo(placed.Bounds.Location);
            }

            if (held) _hintFullSinceMs = nowMs;
            _hintCard.SetResting(_hintShowing == HintShowing.PinnedSheet && !held && nowMs - _hintFullSinceMs >= HintRestAfterMs);
        }

        /// <summary>
        /// The welcome sheet or the tip was up long enough to be read, or the player hid it themselves: it counts
        /// (the day, a teaching tip's showing, a "why no line" sentence said in this run). One that a new line took
        /// down at once does not, and comes again at the next quiet moment.
        /// </summary>
        private void CountHint(DateTimeOffset now)
        {
            _hintUncounted = false;
            if (_hintState == null) return; // no run yet: nothing Kaption shows on its own is up
            (HintState after, HintRun run) = HintPlanner.Stayed(_hintShowing, _hintEntry, _hintState, _hintRun, now);
            _hintRun = run;
            if (ReferenceEquals(after, _hintState)) return;
            _hintState = after;
            HintSettings.SaveState(after);
        }

        private void TakeHintDown(bool fade)
        {
            // With a fade the card is on screen a moment longer (HintCardWindow.FadeOutMs); at once, it is gone.
            // Asked again with nothing up, it leaves alone what is still fading.
            if (_hintShowing != HintShowing.None) _hintLeaving = fade && _hintShowing == HintShowing.Tip ? _hintEntry : null;
            _hintCard?.Dismiss(fade);
            _hintUncounted = false;
            _hintShowing = HintShowing.None;
            _hintEntry = null;
            _hintModel = null;
            _hintPointer = default;
            if (Environment.TickCount64 >= _hintNeverUntilMs) CloseHintStrip();
        }

        private static Size HintPixels(System.Windows.Size dips, double scale) =>
            new((int)Math.Ceiling(dips.Width * scale), (int)Math.Ceiling(dips.Height * scale));

        // ── Where ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What the card is placed against: the game's client and everything it must keep clear of, or, for a sheet
        /// asked for outside the game, the work area of the screen Kaption's window is on. <paramref name="fresh"/>:
        /// a card is being placed, so the parts that cost a Config read (the game's own areas, the subtitle panel's
        /// reserve) are read now; a card that is only being kept reuses them while the game's window and the dialogue
        /// area stay where they were.
        /// </summary>
        private HintScene BuildHintScene(bool gameInFront, bool fresh)
        {
            Rectangle client = Rectangle.Empty, dialogue = Rectangle.Empty;
            bool inGame = gameInFront && GameWindowDetector.TryGetClientArea(GetForegroundWindow(), out client);
            if (inGame)
            {
                if (CaptureRegionValidator.TryParse(notify?.Region, out int x, out int y, out int w, out int h))
                {
                    var area = new Rectangle(x, y, w, h);
                    if (area.IntersectsWith(client)) dialogue = area;
                }
            }
            else
            {
                Screen screen = data?.IsVisible == true ? Screen.FromHandle(new WindowInteropHelper(data).Handle) : Screen.PrimaryScreen;
                client = screen.WorkingArea;
            }

            if (fresh || inGame != _hintInGame || client != _hintClient || dialogue != _hintDialogue || _hintGameAreas == null)
            {
                _hintInGame = inGame;
                _hintClient = client;
                _hintDialogue = dialogue;
                _hintScale = GetScaleForScreen(Screen.FromRectangle(client));
                _hintGameAreas = inGame
                    ? HintPlacement.GameAreas(GameRegistry.Current.Find(_hintsGame)?.Desktop, client, AssistSettings.IsOn(AssistSettings.StaminaReader))
                    : new List<Rectangle>(0);
                _hintReserve = inGame && !dialogue.IsEmpty && OCRTimer.IsEnabled ? SubtitlePanelReserve(client) : Rectangle.Empty;
            }

            var keepClear = new List<Rectangle>(_hintGameAreas.Count + 4);
            keepClear.AddRange(_hintGameAreas);
            if (inGame)
            {
                keepClear.AddRange(SubtitleAndPopupAreas());
                if (!_hintReserve.IsEmpty) keepClear.Add(_hintReserve);
            }
            return new HintScene(client, dialogue, keepClear, _hintScale);
        }

        /// <summary>
        /// The most the subtitle panel can cover while translation runs, so a card placed beside it is still beside it
        /// when a long line comes. Read when the card is placed, not on every look.
        /// </summary>
        private Rectangle SubtitlePanelReserve(Rectangle client)
        {
            try
            {
                if (!IsVisible || SubtitleBackground == null || System.Windows.PresentationSource.FromVisual(this) == null) return Rectangle.Empty;
                Rectangle window = ScreenBox(this, new System.Windows.Rect(0, 0, ActualWidth, ActualHeight));
                double scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
                System.Windows.Thickness pad = SubtitleBackground.Padding;
                double maxText = SubtitleText.MaxTextWidth > 0 && !double.IsInfinity(SubtitleText.MaxTextWidth)
                    ? SubtitleText.MaxTextWidth
                    : Config.Get<int>("MaxOverlayWidth", SubtitleOverlayDefaults.MaxOverlayWidth);
                int maxWidth = (int)Math.Ceiling((maxText + pad.Left + pad.Right + 8) * scale);
                int saved = Config.Get<int>("MaxOverlayHeight", SubtitleOverlayDefaults.MaxOverlayHeight);
                int maxHeight = saved > 0
                    ? (int)Math.Ceiling(saved * scale)
                    : (int)Math.Ceiling(Screen.FromRectangle(client).Bounds.Height * DefaultSubtitleLayoutEngine.DefaultMaxHeightScreenFraction);
                return HintPlacement.PanelReserve(window, SubtitleBackground.GrowsUpward, maxWidth, maxHeight);
            }
            catch (InvalidOperationException)
            {
                return Rectangle.Empty; // the overlay is not on screen yet
            }
        }

        /// <summary>
        /// The card and its strip where a read of the screen can show them, for the mask and the screen watch: only
        /// while Windows does not keep them out of the frame. Empty with no card up, so nothing changes for a player
        /// who never sees one.
        /// </summary>
        private IReadOnlyList<Rectangle> HintAreasInFrame()
        {
            if (!HintWindowsCanBeInFrame()) return Array.Empty<Rectangle>();
            bool honours = _captureBackend is DxgiScreenCapture;
            bool card = _hintCard.CanBeInFrame(honours), strip = _hintStrip?.CanBeInFrame(honours) == true;
            var areas = new List<Rectangle>(2);
            if (card && _hintCard.PhysicalBounds is { Width: > 0, Height: > 0 } cardArea) areas.Add(cardArea);
            if (strip && _hintStrip.PhysicalBounds is { Width: > 0, Height: > 0 } stripArea) areas.Add(stripArea);
            return areas;
        }

        /// <summary>The capture tick's question, every tick: plain fields and no list, so it costs nothing with no card up.</summary>
        private bool HintWindowsCanBeInFrame()
        {
            if (_hintCard == null) return false;
            bool honours = _captureBackend is DxgiScreenCapture;
            return _hintCard.CanBeInFrame(honours) || _hintStrip?.CanBeInFrame(honours) == true;
        }

        // ── The strip ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Looks where the pointer is; opens or closes the strip. True while the player is at the card.</summary>
        private bool StepHintPointer(long nowMs)
        {
            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            bool visible = GetCursorInfo(ref info) && (info.flags & CURSOR_SHOWING) != 0;
            Rectangle strip = _hintStrip?.IsVisible == true ? _hintStrip.PhysicalBounds : Rectangle.Empty;
            bool wasOpen = _hintPointer.StripOpen;
            // The window as Windows has it; what was planned until then.
            Rectangle card = _hintCard.PhysicalBounds is { Width: > 0, Height: > 0 } real ? real : _hintPlaced.Bounds;
            _hintPointer = HintInteractivity.Next(_hintPointer, visible, info.ptScreenPos, card, strip, nowMs,
                (int)Math.Ceiling(HintInteractivity.RestSlopDip * _hintScale));
            if (_hintPointer.StripOpen && !wasOpen)
            {
                OpenHintStrip(_hintModel.IsSheet ? HintStripMode.Sheet : HintStripMode.Line);
                _hintTally.StripOpened();
            }
            else if (!_hintPointer.StripOpen && wasOpen) CloseHintStrip();
            return _hintPointer.Holds;
        }

        private void OpenHintStrip(HintStripMode mode)
        {
            if (_hintStrip == null)
            {
                _hintStrip = new HintStripWindow();
                _hintStrip.PinClicked += () => GuardHints(OnHintPinClicked);
                _hintStrip.MoveClicked += () => GuardHints(OnHintMoveClicked);
                _hintStrip.CloseClicked += () => GuardHints(OnHintCloseClicked);
                _hintStrip.NeverClicked += () => GuardHints(OnHintNeverClicked);
            }
            _hintStrip.SetMode(mode, _hintsPinned);
            Size size = HintPixels(_hintStrip.MeasureContent(), _hintScale);
            Rectangle card = _hintPlaced.Bounds;
            int gap = (int)Math.Round(HintStripGapDip * _hintScale);
            int x = Math.Max(_hintClient.Left, card.Right - size.Width);
            int y = card.Top - gap - size.Height;
            if (y < _hintClient.Top) y = card.Bottom + gap;
            _hintStrip.Present(new System.Drawing.Point(x, y));
        }

        private void CloseHintStrip()
        {
            _hintNeverUntilMs = 0;
            _hintStrip?.Dismiss();
            if (_hintPointer.StripOpen) _hintPointer = default;
        }

        /// <summary>Keep on screen, or let go: a sheet that is up becomes the pinned one, a pinned one goes.</summary>
        private void OnHintPinClicked()
        {
            bool pin = !_hintsPinned;
            HintSettings.Pinned = pin; // Changed brings the kept values up to date
            if (!pin)
            {
                if (_hintShowing == HintShowing.PinnedSheet)
                {
                    // Let go of: it must not come back as this run's welcome sheet a second later.
                    _hintRun = HintPlanner.SheetDismissed(_hintRun);
                    TakeHintDown(fade: true);
                }
            }
            else if (_hintShowing is (HintShowing.WelcomeSheet or HintShowing.AskedSheet) && OCRTimer.IsEnabled && _hintInGame)
            {
                // Over the game the sheet stays from now on. Asked for elsewhere, it runs out its ten seconds and
                // the pin holds for the next time translation runs.
                // The same sheet without its clock, so a later change of size does not bring the bar back.
                _hintUncounted = false; // a pinned sheet is the welcome already; no day is spent on it
                _hintShowing = HintShowing.PinnedSheet;
                _hintSecondsTotal = _hintSecondsLeft = 0;
                _hintModel = HintCardViewModel.Sheet(HintBindingsNow, LocalizedString, clock: false);
                _hintFullDip = _hintCard.MeasureContent(_hintModel, compact: false);
                _hintCompactDip = _hintCard.MeasureContent(_hintModel, compact: true);
                _hintCard.SetContent(_hintModel, GameAccents.For(_hintsGame), _hintPlaced.Compact);
                _hintFullSinceMs = Environment.TickCount64;
            }
            if (_hintStrip?.IsVisible == true) _hintStrip.SetMode(HintStripMode.Sheet, pin);
        }

        /// <summary>To the other place, for this game, when the card fits there now.</summary>
        private void OnHintMoveClicked()
        {
            if (_hintShowing == HintShowing.None || _hintModel == null) return;
            HintPlace other = _hintPlaced.Place == HintPlace.Left ? HintPlace.Top : HintPlace.Left;
            HintScene scene = BuildHintScene(_hintInGame, fresh: true);
            bool fits = HintPlacement.TryPlace(scene, other, HintPixels(_hintFullDip, scene.Scale), HintPixels(_hintCompactDip, scene.Scale), out HintPlaced placed)
                && placed.Place == other;
            if (!fits) return;
            if (placed.Compact != _hintPlaced.Compact) _hintCard.SetContent(_hintModel, GameAccents.For(_hintsGame), placed.Compact);
            if (_hintInGame) HintSettings.SetPlace(_hintsWireId, other);
            _hintPlaced = placed;
            _hintPointer = default;
            CloseHintStrip();
            _hintCard.MoveTo(placed.Bounds.Location);
        }

        /// <summary>Hide: closes what is showing. A pinned sheet is unpinned by it, or it would be back in a second.</summary>
        private void OnHintCloseClicked()
        {
            bool wasSheet = _hintModel?.IsSheet == true;
            _hintTally.Hidden();
            // The strip opens only under a pointer that rested on the card: this sheet or tip was seen.
            if (_hintUncounted) CountHint(DateTimeOffset.Now);
            if (_hintsPinned && wasSheet) HintSettings.Pinned = false;
            // Hidden by the player: no sheet comes back on its own in this run. The day's welcome is not spent by it.
            if (wasSheet) _hintRun = HintPlanner.SheetDismissed(_hintRun);
            _hintNeverUntilMs = wasSheet && _hintsEnabled ? Environment.TickCount64 + HintNeverMs : 0;
            TakeHintDown(fade: true);
            // The strip stays a few seconds with the one way to stop the card for good.
            if (_hintNeverUntilMs > 0) OpenHintStrip(HintStripMode.Never);
        }

        private void OnHintNeverClicked()
        {
            _hintTally.NeverAgain();
            HintSettings.Enabled = false;
            CloseHintStrip();
            RequestHint(HintRequest.HintsSwitchedOff);
        }
    }
}
