using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using GI_Subtitles.Core.Config;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Rendering;
using GI_Subtitles.Views.Shell;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// The dialogue area kept right by Kaption (design `.plan/research/s68/AUTO-DIALOGUE-AREA.md` 2.4, 2.5, 2.9): in
    /// automatic mode it is the game's measured block on the game's own window, placed when the window is first seen
    /// and again when it moves or changes size; a drawing is checked once a session and widened only when it cuts the
    /// game's text; "exactly as drawn" is never touched. Every save goes through <see cref="DialogueAreaStore"/>; this
    /// file answers its <see cref="DialogueAreaStore.Changed"/> for the running app.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>The probe watch while translation is off (replaces the one-time auto-fill of session 58).</summary>
        private readonly DialogueAreaWatch _areaWatch = new();
        /// <summary>The capture tick's still rule while translating.</summary>
        private readonly DialogueAreaFollow _areaFollow = new();
        /// <summary>The store's mode, kept here so the tick reads a field and not Config.</summary>
        private DialogueAreaMode _areaMode = DialogueAreaMode.Auto;
        /// <summary>A drawing was checked against the game's text in this session.</summary>
        private bool _areaHandDecided;
        /// <summary>The game's client area the area was last placed for; the panel centres on it in automatic mode.</summary>
        private Rectangle? _areaClient;
        /// <summary>A client the block could not be placed on while translating: reads hold until the window changes.</summary>
        private Rectangle? _areaUnplaceable;
        /// <summary>What the check of a drawing found this session; the Start card warns about a too-big or outside frame.</summary>
        private AreaVerdict _areaVerdict;
        /// <summary>Why reads are held right now (the hint card and the Start card say it); None while reads run.</summary>
        private AreaHold _areaHold;
        /// <summary>Tells auto-start an area change may have opened the Region gate, after the change's caller is done.</summary>
        private Services.Automation.GateRecheck _gateRecheck;
        /// <summary>The foreground window the tick's game check looked at (GetForegroundTarget sets it).</summary>
        private IntPtr _foregroundWindow;

        // First run (area.json's firstRun part): what the first session after setup came to.
        private bool _areaPickerOpened;
        private int _areaPlacementsThisRun, _areaMatchedThisRun;
        private long _areaRunStartMs, _areaFirstMatchMs;

        private void InitDialogueArea()
        {
            _areaMode = DialogueAreaStore.Mode;
            _gateRecheck = new Services.Automation.GateRecheck(() => _autoStart?.OnGatesChanged(),
                a => Dispatcher.BeginInvoke(a, System.Windows.Threading.DispatcherPriority.Background));
            DialogueAreaStore.Changed += OnDialogueAreaChanged;
            Closed += (_, __) =>
            {
                DialogueAreaStore.Changed -= OnDialogueAreaChanged;
                DialogueAreaCounters.Default.Flush();
            };
            data.GetRegionAutoFillNote = DialogueAreaNote;
            data.GetAreaHold = () => _areaHold;
            data.DescribeDialogueAreaAtStop = DialogueAreaAtStop;
            Logger.Log.Info($"Dialogue area: {DialogueArea.Name(_areaMode)} at launch.");
        }

        private static GameRegionProfile AreaProfile() => GameRegionProfile.ForCurrentGame();

        /// <summary>A game with a manifest: it has process names, a measured block and its text.</summary>
        private static bool IsKnownGame(GameRegionProfile p) => p?.ProcessNames is { Length: > 0 } && p.DialogueText != null;

        private PlaceContext AreaContext(bool busy) => new(
            _areaMode, IsKnownGame(AreaProfile()), _areaHandDecided,
            Config.Get("DevSkipGameGate", false), _gameGateBypassedThisSession, busy);

        private void OnDialogueAreaChanged(AreaChange c)
        {
            try
            {
                _areaMode = c.Mode;
                // A new array: FitDetectorToArea and the tick key on the reference.
                if (notify != null) notify.Region = c.Region.Split(',');
                // Whoever saved it (the watch, the tick, Detect, setup): the caches hold the window it was placed for, or
                // look again when it was placed for none.
                DialogueArea.SyncCaches(c, _areaFollow, _areaWatch);
                if (c.Origin is AreaOrigin.Placed or AreaOrigin.Followed)
                {
                    _areaClient = c.Client.IsEmpty ? _areaClient : c.Client;
                    _areaPlacementsThisRun++;
                    _areaUnplaceable = null;
                }
                if (c.Origin is AreaOrigin.Drawn or AreaOrigin.Typed or AreaOrigin.Chosen or AreaOrigin.Undone)
                {
                    // Placed for no window now, or the way it is kept changed: the tick and the watch look again.
                    _areaUnplaceable = null;
                    _areaHandDecided = false;
                    _areaVerdict = AreaVerdict.None;
                    _areaPickerOpened |= c.Origin == AreaOrigin.Drawn;
                }
                if (c.Quiet)
                {
                    // What the player's own changes get from OnCaptureRegionUserChange, minus the overlap dialog: an
                    // overlap after a change Kaption made shows as the silent gate on the Start card (session 58).
                    NotifyRegionChanged();
                    ResetOcrPipelineState(clearImageCaches: true);
                    _sessionEvidence.Reset();
                    data?.ResetSessionCheck();
                }
                // The panel hangs from the area's top edge: it moves with the area, also while translation is off. Left
                // where it waited (the screen's bottom edge when nothing was saved at launch), it sat inside a frame
                // reaching that edge (WuWa's) and the first start stopped at "subtitles cover the dialogue".
                UpdateWindowPosition();
                data?.UpdateDashboardRegionInfo();
                // After this change's caller is done: the Region gate saves the frame inside the Start button's own start.
                _gateRecheck?.Request();
                data?.UpdateDashboardStatus();
                if (c.Origin == AreaOrigin.Drawn) CheckDrawingNow();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Dialogue area: applying a change failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The picker closed: when the game's window is there, check the drawing against the game's text before the
        /// first read. A click, so the window lookup may run here. With the game closed it waits for the first sight.
        /// </summary>
        private void CheckDrawingNow()
        {
            if (!DialogueArea.MayPlace(AreaContext(busy: false))) return;
            var (state, window) = GameWindowDetector.FindGameClient(AreaProfile());
            if (state == GameWindowState.Found && window is { } w)
                DecideArea(new Rectangle(w.GameX, w.GameY, w.GameW, w.GameH), quiet: true);
        }

        /// <summary>
        /// The reconcile table for this window, applied: place the block, widen a drawing that cuts the game's text,
        /// or keep. False when the block cannot be placed on this window (too small, mostly off the screen).
        /// </summary>
        private bool DecideArea(Rectangle client, bool quiet) => DecideArea(client, quiet, out _);

        /// <param name="status">Why the block was not placed (too small, off the screen); Placed when it was.</param>
        private bool DecideArea(Rectangle client, bool quiet, out PlacementStatus status)
        {
            GameRegionProfile profile = AreaProfile();
            PlacementResult placed = DialogueAreaPlacement.Place(profile, client, SystemInformation.VirtualScreen, DialogueAreaPlacement.MonitorBounds());
            status = placed.Status;
            if (placed.Status != PlacementStatus.Placed)
            {
                Logger.Log.Info($"Dialogue area: not placed on a {client.Width}x{client.Height} window ({placed.Status}).");
                return false;
            }
            Rectangle? saved = DialogueArea.Parse(Config.Get<string>(DialogueAreaStore.RegionKey, ""));
            DialogueAreaMode mode = _areaMode;
            AreaDecision d = DialogueAreaReconcile.Decide(saved, mode, client, placed.Area, placed.Text);
            string game = Config.Get("Game", Game ?? "Genshin");
            switch (d.Action)
            {
                case AreaAction.Place:
                    if (saved is Rectangle before)
                        DialogueAreaCounters.Default.CountPlacedAgain(game, before.Size == d.Area.Size ? "moved" : "resized");
                    DialogueAreaStore.Commit(d.Area, DialogueAreaMode.Auto, saved == null ? AreaOrigin.Placed : AreaOrigin.Followed, quiet, client);
                    Logger.Log.Info($"Dialogue area: the {profile.GameId} frame placed on a {client.Width}x{client.Height} window ({d.Verdict}).");
                    break;
                case AreaAction.Widen:
                    DialogueAreaStore.Commit(d.Area, DialogueAreaMode.Hand, AreaOrigin.Widened, quiet, client);
                    Logger.Log.Info($"Dialogue area: a drawing that cut the {profile.GameId} dialogue was widened to hold it.");
                    break;
            }
            if (mode == DialogueAreaMode.Hand)
            {
                _areaHandDecided = true;
                _areaVerdict = d.Verdict;
                DialogueAreaCounters.Default.CountDrawing(game, d.Verdict);
                if (d.Action == AreaAction.Keep) Logger.Log.Info($"Dialogue area: the drawing is kept ({d.Verdict}).");
            }
            _areaClient = client;
            return true;
        }

        /// <summary>
        /// The 1.5 s watch, translation off: once the game's own window has held one usable size for three probes,
        /// decide the area for it (nothing saved, automatic, or a drawing not yet checked this session). The probe has
        /// just said the game's process is in front, so its window is the foreground one: two Win32 calls, no listing.
        /// </summary>
        private void WatchDialogueArea(bool gameInFront, uint gamePid)
        {
            bool busy = ChooseRegion || OCRTimer.IsEnabled || _quickTranslateBusy;
            if (!gameInFront || !DialogueArea.MayPlace(AreaContext(busy)))
            {
                if (!gameInFront) _areaWatch.Interrupt();
                return;
            }

            GameWindowState state = GameWindowState.NotFound;
            Rectangle client = default;
            IntPtr front = GetForegroundWindow();
            GetWindowThreadProcessId(front, out uint frontPid);
            // The probe ran a moment ago on another thread: the window in front must still be the game's.
            if (front != IntPtr.Zero && (gamePid == 0 || frontPid == gamePid) && GameWindowDetector.TryGetClientArea(front, out client))
                state = GameWindowState.Found;

            WatchStep step = _areaWatch.Observe(state, client);
            if (step == WatchStep.GiveUp)
            {
                if (!HasConfiguredRegion())
                {
                    Logger.Log.Warn($"Dialogue area: no steady, usable game window ({state}, {client.Width}x{client.Height}); the Start card offers the picker.");
                    _autoStart?.OnGatesChanged();
                    data?.UpdateDashboardStatus();
                }
                return;
            }
            if (step != WatchStep.Ready) return;

            if (DecideArea(client, quiet: true))
            {
                _areaWatch.MarkDecided(client);
                _areaFollow.MarkPlaced(client);
            }
            else
            {
                // Not on this window (mostly off the screen): ask again when it changes; the card offers the picker once
                // the watch gives up with nothing saved.
                _areaWatch.MarkUnplaceable(client);
            }
        }

        /// <summary>
        /// The capture tick, the game in front: true to skip this tick's read. In automatic mode, a window that moves
        /// or changes size holds the reads until it has been still, then the block is placed again (same size after a
        /// move, so the reader keeps its shape). A drawing not yet checked this session is checked before the first
        /// read. Free while nothing changed: two Win32 calls and a compare.
        /// </summary>
        private bool HoldReadForDialogueArea()
        {
            bool hold = HoldRead(out AreaHold why);
            SetAreaHold(why);
            return hold;
        }

        /// <summary>The tick's hold, and why: a window too small or off the screen holds reads until it changes; a move holds them for a moment.</summary>
        private bool HoldRead(out AreaHold why)
        {
            why = AreaHold.None;
            if (_areaMode == DialogueAreaMode.Exact || (_areaMode == DialogueAreaMode.Hand && _areaHandDecided)) return false;
            if (!GameWindowDetector.TryGetClientArea(_foregroundWindow, out Rectangle client) || !DialogueAreaPlacement.IsUsable(client))
            {
                // Automatic: the game's window in front is too small for its dialogue (resized down, a splash): the
                // saved area points where the window used to be, so nothing is read until a usable window is back.
                // A drawing is the player's screen spot and is read as it is.
                bool tooSmall = _areaMode == DialogueAreaMode.Auto && DialogueArea.MayPlace(AreaContext(ChooseRegion || _quickTranslateBusy));
                if (tooSmall) why = AreaHold.TooSmall;
                return tooSmall;
            }
            if (_areaFollow.IsPlacedFor(client)) return false;
            // The block does not fit this window (mostly off the screen): the old area is somewhere else now, so no read
            // until the window changes again.
            if (_areaUnplaceable == client)
            {
                why = AreaHold.OffScreen;
                return true;
            }
            if (!DialogueArea.MayPlace(AreaContext(ChooseRegion || _quickTranslateBusy))) return false;

            if (_areaMode == DialogueAreaMode.Hand)
            {
                DecideArea(client, quiet: true);
                _areaFollow.MarkPlaced(client);
                return false;
            }

            PlacementResult placed = DialogueAreaPlacement.Place(AreaProfile(), client, SystemInformation.VirtualScreen);
            bool fits = placed.Status == PlacementStatus.Placed && DialogueArea.Parse(notify?.Region) == placed.Area;
            switch (_areaFollow.Observe(client, fits))
            {
                case FollowStep.Read:
                    // Last session's area adopted on this window: the panel, placed at start without it, centres now.
                    bool newClient = _areaClient != client;
                    _areaClient = client;
                    if (newClient) UpdateWindowPosition();
                    return false;
                case FollowStep.Hold:
                    return true;
                default:
                    if (DecideArea(client, quiet: true))
                    {
                        _areaFollow.MarkPlaced(client);
                        _areaUnplaceable = null;
                    }
                    else
                    {
                        _areaUnplaceable = client;
                        why = AreaHold.OffScreen;
                    }
                    return true;
            }
        }

        /// <summary>The Start card and the hint card say why no line shows while the window cannot carry the frame.</summary>
        private void SetAreaHold(AreaHold why)
        {
            if (why == _areaHold) return;
            _areaHold = why;
            if (why != AreaHold.None) Logger.Log.Info($"Dialogue area: reads held, the game's window is {(why == AreaHold.TooSmall ? "too small" : "partly off the screen")}.");
            data?.UpdateDashboardStatus();
        }

        /// <summary>
        /// The Start gate with no area: in automatic mode, place it now (a click, so the window lookup may run here).
        /// True when an area is saved afterwards; <paramref name="failure"/> says why not, so a minimised, small or
        /// off-screen game is not told to start.
        /// </summary>
        private bool TryPlaceAreaOnStart(out AreaPlaceFailure failure)
        {
            failure = AreaPlaceFailure.NoWindow;
            if (_areaMode != DialogueAreaMode.Auto || !DialogueArea.MayPlace(AreaContext(busy: false))) return false;
            try
            {
                var (state, window) = GameWindowDetector.FindGameClient(AreaProfile());
                if (state == GameWindowState.Minimised) failure = AreaPlaceFailure.Minimised;
                else if (state == GameWindowState.Found && window is { } w)
                {
                    var client = new Rectangle(w.GameX, w.GameY, w.GameW, w.GameH);
                    if (DecideArea(client, quiet: true, out PlacementStatus status))
                    {
                        _areaFollow.MarkPlaced(client);
                        _areaWatch.MarkDecided(client);
                        failure = AreaPlaceFailure.None;
                    }
                    else
                    {
                        failure = status == PlacementStatus.Unusable ? AreaPlaceFailure.TooSmall : AreaPlaceFailure.OffScreen;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Dialogue area: placing at start failed: {ex.Message}");
            }
            return HasConfiguredRegion();
        }

        /// <summary>
        /// Automatic mode with nothing saved yet for a game Kaption can place: the Start card says it waits for the game,
        /// never "pick the area". Not once the watch gave up, nor under the dev switch or in a cloud session.
        /// </summary>
        private bool AreaPlacedOnSight() =>
            !HasConfiguredRegion() && !_areaWatch.GaveUp && DialogueArea.MayPlace(AreaContext(busy: false))
            && _areaMode == DialogueAreaMode.Auto;

        /// <summary>What the Start card says about the area: Kaption's own, widened by Kaption, or not found.</summary>
        private RegionAutoFillNote DialogueAreaNote()
        {
            bool saved = HasConfiguredRegion();
            if (!saved) return _areaWatch.GaveUp && _areaMode == DialogueAreaMode.Auto ? RegionAutoFillNote.Failed : RegionAutoFillNote.None;
            if (_areaMode == DialogueAreaMode.Auto) return RegionAutoFillNote.Set;
            if (_areaMode != DialogueAreaMode.Hand) return RegionAutoFillNote.None;
            if (DialogueAreaStore.IsWidened) return RegionAutoFillNote.Adjusted;
            // A frame the player drew and answered a warning about once is not warned about again.
            if (DialogueAreaStore.IsKeptMine) return RegionAutoFillNote.None;
            return _areaVerdict switch
            {
                AreaVerdict.TooBig => RegionAutoFillNote.TooBig,
                AreaVerdict.OutsideGame => RegionAutoFillNote.Outside,
                _ => RegionAutoFillNote.None,
            };
        }

        /// <summary>Translation started: the first-run counters of this run start from nothing.</summary>
        private void StartAreaRun()
        {
            _areaRunStartMs = Environment.TickCount64;
            _areaHold = AreaHold.None;
            _areaFirstMatchMs = 0;
            _areaMatchedThisRun = 0;
        }

        /// <summary>A read finished (the UI tick): only a matched line counts.</summary>
        private void NoteAreaRead(ReadOutcome outcome)
        {
            if (outcome != ReadOutcome.Matched) return;
            if (_areaMatchedThisRun++ == 0) _areaFirstMatchMs = Environment.TickCount64;
        }

        /// <summary>
        /// Translation stopped: the counters' line for the log, and the first run's report once after setup. Counts and
        /// buckets only.
        /// </summary>
        private string DialogueAreaAtStop(bool sessionCheckRaised)
        {
            string game = Config.Get("Game", Game ?? "Genshin");
            try
            {
                if (Config.Get(DialogueAreaCounters.FirstRunPendingKey, false))
                {
                    Rectangle c = _areaClient ?? Rectangle.Empty;
                    string firstLine = _areaFirstMatchMs == 0 ? "none"
                        : (_areaFirstMatchMs - _areaRunStartMs) switch
                        {
                            < 10_000 => "under 10 s",
                            < 60_000 => "under 1 min",
                            < 300_000 => "under 5 min",
                            _ => "later",
                        };
                    string region = Config.Get<string>(DialogueAreaStore.RegionKey, "");
                    string how = _areaMode switch
                    {
                        DialogueAreaMode.Auto => "placed",
                        DialogueAreaMode.Exact => "typed or exact",
                        _ => region == Config.Get<string>(DialogueAreaStore.WidenedKey, null) ? "widened" : "drawn",
                    };
                    var report = new FirstRunReport(how, DialogueArea.Name(_areaMode),
                        c.IsEmpty ? "unknown" : DialogueAreaCounters.ShapeOf(c.Width, c.Height),
                        !c.IsEmpty && DialogueAreaCounters.IsMeasuredShape(c.Width, c.Height),
                        _areaPickerOpened, _areaPlacementsThisRun, _areaMatchedThisRun, firstLine, sessionCheckRaised);
                    if (DialogueAreaCounters.Default.RecordFirstRun(game, report))
                        Logger.Log.Info($"Dialogue area, first run: {report}");
                    Config.Set(DialogueAreaCounters.FirstRunPendingKey, false);
                }
                return DialogueAreaCounters.Default.Describe(game);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Dialogue area counters failed: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Where the panel hangs: the monitor and the x (DIPs) it centres on. In automatic mode that is the game's window,
        /// on the monitor holding its middle, so the panel stays over a windowed game, also one straddling two monitors;
        /// otherwise the monitor holding the frame's top-left corner, centred on it. Saving a drag and applying the saved
        /// offset (UpdateWindowPosition, PersistManualPadAfterDrag) both read this one answer.
        /// </summary>
        private (Screen Screen, double Scale, double Centre)? PanelScreen()
        {
            if (!CaptureRegionValidator.TryParse(notify?.Region, out int x, out int y, out _, out _)) return null;
            Screen[] screens = Screen.AllScreens;
            var bounds = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++) bounds[i] = screens[i].Bounds;
            Rectangle? game = _areaMode == DialogueAreaMode.Auto ? _areaClient : null;
            int index = OverlayPlacement.MonitorFor(bounds, new System.Drawing.Point(x, y), game);
            if (index < 0) return null;
            double scale = GetScaleForScreen(screens[index]);
            return (screens[index], scale, OverlayPlacement.CentreDip(bounds[index], scale, game));
        }
    }
}
