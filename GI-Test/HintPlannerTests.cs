using System;
using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Services.Hints;
using GI_Subtitles.Services.Notices;
using GI_Subtitles.Views;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Which hint the in-game card shows and when (Services/Hints/HintPlanner): the answer to a key first, nothing
    /// unasked over a subtitle or one of Kaption's windows, the lifetime budget of the two teaching tips, the welcome
    /// sheet's days, and what "off" still lets through.
    /// </summary>
    [TestClass]
    public class HintPlannerTests
    {
        // Noon local time, so a few hours either way stay on one local day in any time zone.
        private static readonly DateTimeOffset Noon = new(new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Local));
        private static readonly HintBindings AllBound = HintBindings.From(id => "Ctrl+" + id);

        private static HintBindings Without(params int[] unbound) =>
            HintBindings.From(id => unbound.Contains(id) ? null : "Ctrl+" + id);

        /// <summary>Translating in the game, hints on, nothing up, every shortcut bound.</summary>
        private static HintSignals InGame => new() { Enabled = true, Translating = true, GameInFront = true, TeachingOn = true, Bindings = AllBound };

        private static string Day(DateTimeOffset at) => NoticeGate.DayOf(at);

        /// <summary>An install whose welcome days are behind it and whose memory began a week ago; the two tips are still owed.</summary>
        private static HintState Untaught =>
            HintState.Create(Day(Noon.AddDays(-7)), newInstall: false).WithWelcomeOn(Day(Noon.AddDays(-7)));

        /// <summary>The same install once the player uses both taught shortcuts: nothing is left to say unasked.</summary>
        private static HintState Settled => Untaught.WithKeyUsed(HintCatalog.KeyQuickTranslate).WithKeyUsed(HintCatalog.KeyFixLine);

        private static HintDecision Next(HintSignals s, HintState state, HintRun run, DateTimeOffset now) => HintPlanner.Next(s, state, run, now);

        private static void AssertShows(HintDecision d, HintShowing what, HintEntry entry = null, string because = null)
        {
            Assert.AreEqual(HintAction.Show, d.Action, because);
            Assert.AreEqual(what, d.What, because);
            Assert.AreSame(entry, d.Entry, because);
        }

        [TestMethod]
        public void AnEchoAlwaysWins()
        {
            var run = HintRun.Start(Noon);
            var echoes = new[]
            {
                (HintRequest.SubtitlesHidden, HintCatalog.EchoSubtitlesHidden),
                (HintRequest.SubtitlesBack, HintCatalog.EchoSubtitlesBack),
                (HintRequest.TranslationStopped, HintCatalog.EchoStopped),
            };
            foreach (var (request, echo) in echoes)
                foreach (HintShowing up in Enum.GetValues<HintShowing>())
                {
                    // Over a tip, a sheet or another echo, during a subtitle, with one of Kaption's windows open,
                    // and with a state tip due at the same moment.
                    var s = InGame with
                    {
                        Request = request, Showing = up, LineUp = true, OwnWindowUp = true, Pinned = true,
                        Check = SessionCheckKind.NoMatch, Translating = request != HintRequest.TranslationStopped,
                    };
                    var d = Next(s, Settled, run, Noon.AddSeconds(30));
                    AssertShows(d, HintShowing.Echo, echo, $"{request} over {up}");
                    Assert.AreEqual(echo.Seconds, d.Seconds);
                    Assert.IsTrue(d.Seconds is > 0 and <= 4, "an echo is short");
                }

            // An echo answers a key pressed in the game: with another window in front there is nothing to say it over.
            Assert.AreEqual(HintAction.Keep, Next(InGame with { Request = HintRequest.SubtitlesHidden, GameInFront = false }, Settled, run, Noon).Action);
        }

        [TestMethod]
        public void NothingUnasked_WhileASubtitleShows_OrOneOfKaptionsWindowsIsUp()
        {
            var fresh = HintState.Create(Day(Noon), newInstall: true);
            var run = HintRun.Start(Noon);
            var at = Noon.AddSeconds(10);
            var due = new (string What, HintSignals Signals, HintState State)[]
            {
                ("a state tip", InGame with { Check = SessionCheckKind.NoMatch }, Settled),
                ("the welcome sheet", InGame, fresh),
            };
            foreach (var (what, s, state) in due)
            {
                Assert.AreEqual(HintAction.Show, Next(s, state, run, at).Action, $"{what} is due");
                Assert.AreEqual(HintAction.Show, Next(s with { SecondsSinceLine = HintPlanner.QuietSeconds }, state, run, at).Action, $"{what}: two seconds after a line");

                var held = new (string Why, HintSignals Signals)[]
                {
                    ("a subtitle is on screen", s with { LineUp = true }),
                    ("a subtitle left a second ago", s with { SecondsSinceLine = 1 }),
                    ("someone is talking and the subtitles are hidden", s with { LineUp = true, HiddenByKeySeconds = 10, SecondsSinceLineMatched = 1 }),
                    ("the game is in exclusive full screen, where nothing can be drawn", s with { Check = SessionCheckKind.FullScreen }),
                    ("one of Kaption's windows is up", s with { OwnWindowUp = true }),
                    ("the game is not in front", s with { GameInFront = false }),
                    ("translation is not running", s with { Translating = false }),
                    ("the end-to-end test mode", s with { Blocked = true }),
                    ("something is already up", s with { Showing = HintShowing.AskedSheet }),
                };
                foreach (var (why, signals) in held)
                    Assert.AreEqual(HintAction.Keep, Next(signals, state, run, at).Action, $"{what} while {why}");
            }
        }

        [TestMethod]
        public void ATeachingTip_AtMostOnceADay_TwiceEver_AndNeverAfterItsKeyWasUsed()
        {
            HintState state = Untaught;
            // Three minutes into a run with no subtitle: the moment for the Ctrl+Q tip. Thirty lines read and the
            // last one just gone: the moment for the fix tip.
            HintSignals quiet = InGame;
            HintSignals afterLines = InGame with { LinesShown = 40, SecondsSinceLine = 5 };

            HintState ShowOn(DateTimeOffset day, HintSignals s, HintEntry expected)
            {
                var run = HintRun.Start(day);
                var at = day.AddSeconds(200);
                var d = Next(s, state, run, at);
                if (expected == null)
                {
                    Assert.AreEqual(HintAction.Keep, d.Action, $"nothing on {Day(day)}");
                    return state;
                }
                AssertShows(d, HintShowing.Tip, expected, Day(day));
                return HintPlanner.Stayed(d.What, d.Entry, state, run, at).State;
            }

            Assert.AreEqual(HintAction.Keep, Next(quiet, state, HintRun.Start(Noon), Noon.AddSeconds(100)).Action, "not in the first three minutes");
            Assert.AreEqual(HintAction.Keep, Next(quiet with { SecondsSinceLine = 30 }, state, HintRun.Start(Noon), Noon.AddSeconds(200)).Action,
                "not while lines keep coming");

            state = ShowOn(Noon, quiet, HintCatalog.TipQuickTranslate);
            state = ShowOn(Noon.AddHours(2), quiet, null);                               // once a day
            state = ShowOn(Noon.AddHours(2), afterLines, null);                          // and that day is spent for the other tip too
            state = ShowOn(Noon.AddDays(1), quiet, HintCatalog.TipQuickTranslate);       // the second and last time
            state = ShowOn(Noon.AddDays(2), quiet, null);                                // twice ever
            state = ShowOn(Noon.AddDays(2), afterLines, HintCatalog.TipFixLine);
            state = ShowOn(Noon.AddDays(3), afterLines, HintCatalog.TipFixLine);
            state = ShowOn(Noon.AddDays(4), afterLines, null);
            Assert.AreEqual(4, state.Tips.Values.Sum(t => t.Count), "four unasked lines in the life of an install");

            // A player who has used the key is never taught it.
            Assert.AreEqual(HintAction.Keep, Next(quiet, Settled, HintRun.Start(Noon), Noon.AddSeconds(200)).Action);
            Assert.AreEqual(HintAction.Keep, Next(afterLines, Settled, HintRun.Start(Noon), Noon.AddSeconds(200)).Action);

            // The fix tip waits for a day after the first, for thirty lines, and for the moment a line has just gone.
            var firstDay = HintState.Create(Day(Noon), newInstall: false).WithWelcomeOn(Day(Noon)).WithKeyUsed(HintCatalog.KeyQuickTranslate);
            Assert.AreEqual(HintAction.Keep, Next(afterLines, firstDay, HintRun.Start(Noon), Noon.AddSeconds(200)).Action, "the first day");
            var later = Untaught.WithKeyUsed(HintCatalog.KeyQuickTranslate);
            Assert.AreEqual(HintAction.Keep, Next(afterLines with { LinesShown = 29 }, later, HintRun.Start(Noon), Noon.AddSeconds(200)).Action);
            Assert.AreEqual(HintAction.Keep, Next(afterLines with { SecondsSinceLine = 11 }, later, HintRun.Start(Noon), Noon.AddSeconds(200)).Action);

            // Never in a run that showed the welcome sheet.
            var welcomed = HintRun.Start(Noon) with { WelcomeShown = true };
            Assert.AreEqual(HintAction.Keep, Next(quiet, Untaught, welcomed, Noon.AddSeconds(200)).Action);

            // Never while lines are read and not found: the game is not in English, and Ctrl+Q would find nothing either.
            var toldAlready = HintRun.Start(Noon) with { NoMatchShown = true };
            Assert.AreEqual(HintAction.Keep, Next(quiet with { Check = SessionCheckKind.NoMatch }, Untaught, toldAlready, Noon.AddSeconds(200)).Action);
            AssertShows(Next(quiet with { Check = SessionCheckKind.NoDialogue }, Untaught, toldAlready with { NoDialogueShown = true }, Noon.AddSeconds(200)),
                HintShowing.Tip, HintCatalog.TipQuickTranslate, "no dialogue yet is the open world: the moment for Ctrl+Q");
        }

        /// <summary>Reads wait for a window that cannot carry the frame: the card says why, once per run, before any other "why no line".</summary>
        [TestMethod]
        public void AHeldRead_SaysWhy_OncePerRun_AndBeforeAnyOtherWhy()
        {
            foreach (var (hold, tip) in new[]
            {
                (GI_Subtitles.Services.Detection.AreaHold.TooSmall, HintCatalog.TipAreaHeldSmall),
                (GI_Subtitles.Services.Detection.AreaHold.OffScreen, HintCatalog.TipAreaHeldOff),
            })
            {
                var run = HintRun.Start(Noon);
                var s = InGame with { AreaHold = hold, Check = SessionCheckKind.NoDialogue };
                var d = Next(s, Settled, run, Noon.AddSeconds(70));
                AssertShows(d, HintShowing.Tip, tip, $"{hold}: said before the check's own sentence");
                run = HintPlanner.Stayed(d.What, d.Entry, Settled, run, Noon.AddSeconds(70)).Run;
                Assert.AreEqual(HintAction.Keep, Next(s, Settled, run, Noon.AddSeconds(90)).Action, $"{hold}: no advice about the game's text while reads wait");
                Assert.AreEqual(HintCheckAnswer.Later, HintPlanner.CheckAnswer(SessionCheckKind.NoDialogue, s, run, noPlace: false, waitedSeconds: 100),
                    $"{hold}: the balloon waits too");
                AssertShows(Next(s with { AreaHold = GI_Subtitles.Services.Detection.AreaHold.None }, Settled, run, Noon.AddSeconds(95)),
                    HintShowing.Tip, HintCatalog.TipNoDialogue, $"{hold}: once reads run again, the check's own sentence follows");
                Assert.AreEqual(HintAction.Keep, Next(InGame with { AreaHold = hold }, Settled, run, Noon.AddMinutes(20)).Action, $"{hold}: not again in this run");
            }
            Assert.AreEqual(HintAction.Keep, Next(InGame, Settled, HintRun.Start(Noon), Noon.AddSeconds(70)).Action, "nothing held, nothing said");
        }

        [TestMethod]
        public void AStateTip_OncePerRun()
        {
            foreach (var (check, tip) in new[] { (SessionCheckKind.NoMatch, HintCatalog.TipNoMatch), (SessionCheckKind.NoDialogue, HintCatalog.TipNoDialogue) })
            {
                var run = HintRun.Start(Noon);
                var s = InGame with { Check = check };
                var d = Next(s, Settled, run, Noon.AddSeconds(70));
                AssertShows(d, HintShowing.Tip, tip);
                run = HintPlanner.Stayed(d.What, d.Entry, Settled, run, Noon.AddSeconds(70)).Run;
                Assert.AreEqual(HintAction.Keep, Next(s, Settled, run, Noon.AddMinutes(20)).Action, $"{check}: not again in this run");
                AssertShows(Next(s, Settled, HintRun.Start(Noon.AddHours(1)), Noon.AddHours(1).AddSeconds(70)), HintShowing.Tip, tip, "the next run");
            }
        }

        [TestMethod]
        public void InExclusiveFullScreen_NothingUnaskedIsShownOrKept_SoNoBudgetIsSpentUnseen()
        {
            var fresh = HintState.Create(Day(Noon), newInstall: true);
            var run = HintRun.Start(Noon);
            HintSignals black = InGame with { Check = SessionCheckKind.FullScreen };

            var rows = new (string What, HintSignals Signals, HintState State, DateTimeOffset At, HintAction Expected)[]
            {
                ("the welcome sheet", black, fresh, Noon.AddSeconds(30), HintAction.Keep),
                ("a teaching tip", black, Untaught, Noon.AddSeconds(200), HintAction.Keep),
                ("the pinned sheet", black with { Pinned = true }, Settled, Noon.AddSeconds(200), HintAction.Keep),
                ("a pinned sheet that is up", black with { Pinned = true, Showing = HintShowing.PinnedSheet }, Settled, Noon.AddSeconds(200), HintAction.Hide),
                ("a welcome sheet that is up", black with { Showing = HintShowing.WelcomeSheet }, fresh, Noon.AddSeconds(30), HintAction.Hide),
                ("a tip that is up", black with { Showing = HintShowing.Tip }, Settled, Noon.AddSeconds(30), HintAction.Hide),
            };
            foreach (var (what, s, state, at, expected) in rows)
            {
                // The same moment in a window: the sheet or tip is due, or the card that is up stays.
                HintAction inAWindow = Next(s with { Check = SessionCheckKind.None }, state, run, at).Action;
                Assert.AreEqual(s.Showing == HintShowing.None ? HintAction.Show : HintAction.Keep, inAWindow, $"{what}, in a window");
                Assert.AreEqual(expected, Next(s, state, run, at).Action, what);
            }

            // What the player asks for is still answered: the sheet may be wanted on another screen.
            AssertShows(Next(black with { Request = HintRequest.Sheet }, fresh, run, Noon.AddSeconds(30)), HintShowing.AskedSheet);
        }

        [TestMethod]
        public void SubtitlesHidden_IsSaidOnce_AndAgainOnlyToAPlayerWhoAnsweredTheFirst()
        {
            var run = HintRun.Start(Noon);
            // The line is up all the while: this tip is said because someone is talking, not once they stop.
            HintSignals Hidden(double forSeconds, double? matchedAgo = 1) =>
                InGame with { HiddenByKeySeconds = forSeconds, SecondsSinceLineMatched = matchedAgo, LineUp = true };

            Assert.AreEqual(HintAction.Keep, Next(Hidden(30), Settled, run, Noon.AddSeconds(30)).Action, "the echo said it under a minute ago");
            Assert.AreEqual(HintAction.Keep, Next(Hidden(90, matchedAgo: 20), Settled, run, Noon.AddSeconds(90)).Action, "nobody is talking");
            Assert.AreEqual(HintAction.Keep, Next(InGame with { SecondsSinceLineMatched = 1 }, Settled, run, Noon.AddSeconds(90)).Action,
                "hidden in Kaption's own window, or not hidden: that player knows");
            Assert.AreEqual(HintAction.Keep, Next(Hidden(90) with { Bindings = Without(HintCatalog.KeyHideSubtitles) }, Settled, run, Noon.AddSeconds(90)).Action,
                "no shortcut to name");

            var first = Noon.AddSeconds(90);
            var d = Next(Hidden(90), Settled, run, first);
            AssertShows(d, HintShowing.Tip, HintCatalog.TipSubtitlesHidden);
            run = HintPlanner.Shown(d, run, first);

            // Still hidden since before the tip: the player saw it and left the subtitles off.
            Assert.AreEqual(HintAction.Keep, Next(Hidden(90 + 900), Settled, run, first.AddSeconds(900)).Action);
            // Shown again and hidden again, but too soon after the tip.
            Assert.AreEqual(HintAction.Keep, Next(Hidden(70), Settled, run, first.AddSeconds(300)).Action);
            // Hidden again after the tip, ten minutes on: once more, and then never in this run.
            var second = first.AddSeconds(700);
            d = Next(Hidden(70), Settled, run, second);
            AssertShows(d, HintShowing.Tip, HintCatalog.TipSubtitlesHidden);
            run = HintPlanner.Shown(d, run, second);
            Assert.AreEqual(HintAction.Keep, Next(Hidden(70), Settled, run, second.AddSeconds(1200)).Action);
        }

        [TestMethod]
        public void TheWelcomeSheet_OnThreeDaysForANewInstall_OnceForAnOldOne()
        {
            foreach (var (newInstall, days) in new[] { (true, 3), (false, 1) })
            {
                HintState state = HintState.Create(Day(Noon), newInstall);
                for (int day = 0; day < days + 2; day++)
                {
                    var start = Noon.AddDays(day);
                    var run = HintRun.Start(start);
                    var d = Next(InGame, state, run, start.AddSeconds(1));
                    if (day >= days)
                    {
                        Assert.AreEqual(HintAction.Keep, d.Action, $"new install {newInstall}: no welcome on day {day + 1}");
                        continue;
                    }
                    AssertShows(d, HintShowing.WelcomeSheet, null, $"new install {newInstall}: day {day + 1}");
                    Assert.AreEqual(day == 0 ? HintPlanner.FirstWelcomeSeconds : HintPlanner.WelcomeSeconds, d.Seconds, "longer the very first time");
                    (state, run) = HintPlanner.Stayed(d.What, d.Entry, state, run, start.AddSeconds(1 + HintPlanner.CountsAfterSeconds));

                    // Stopping and starting again on the same day does not bring it back.
                    Assert.AreEqual(HintAction.Keep, Next(InGame, state, HintRun.Start(start.AddHours(1)), start.AddHours(1).AddSeconds(1)).Action);
                }
            }
        }

        [TestMethod]
        public void TheWelcomeSheet_WaitsForTheGameAndAQuietMoment_ForAMinute_ThenTheDayIsNotCounted()
        {
            var state = HintState.Create(Day(Noon), newInstall: true);
            var run = HintRun.Start(Noon);

            // Started from Kaption's window: the sheet comes when the player is back in the game.
            Assert.AreEqual(HintAction.Keep, Next(InGame with { GameInFront = false }, state, run, Noon.AddSeconds(5)).Action);
            AssertShows(Next(InGame, state, run, Noon.AddSeconds(20)), HintShowing.WelcomeSheet);

            // Started in the middle of a dialogue: it waits for two seconds without a line.
            Assert.AreEqual(HintAction.Keep, Next(InGame with { LineUp = true }, state, run, Noon.AddSeconds(5)).Action);
            AssertShows(Next(InGame with { SecondsSinceLine = 2.5 }, state, run, Noon.AddSeconds(40)), HintShowing.WelcomeSheet);

            // A minute of dialogue: dropped for this run, and nothing was written, so the next start tries again.
            Assert.AreEqual(HintAction.Keep, Next(InGame, state, run, Noon.AddSeconds(61)).Action);
            Assert.AreEqual(0, state.WelcomeDays.Count);
            AssertShows(Next(InGame, state, HintRun.Start(Noon.AddMinutes(30)), Noon.AddMinutes(30).AddSeconds(1)), HintShowing.WelcomeSheet);
        }

        [TestMethod]
        public void AWelcomeSheetALineTookDownAtOnce_IsNotCounted_AndComesAgain()
        {
            var state = HintState.Create(Day(Noon), newInstall: true);
            var run = HintRun.Start(Noon);

            // It appears, and a line arrives a second later: nobody read it. Appearing alone spends nothing.
            HintDecision welcome = Next(InGame, state, run, Noon.AddSeconds(1));
            AssertShows(welcome, HintShowing.WelcomeSheet);
            Assert.AreEqual(run, HintPlanner.Shown(welcome, run, Noon.AddSeconds(1)), "a sheet that only appeared costs nothing");
            Assert.AreEqual(HintAction.Hide, Next(InGame with { Showing = HintShowing.WelcomeSheet, LineUp = true }, state, run, Noon.AddSeconds(2)).Action);

            // The line is gone: the same sheet again, still as the first one.
            HintDecision again = Next(InGame with { SecondsSinceLine = 3 }, state, run, Noon.AddSeconds(20));
            AssertShows(again, HintShowing.WelcomeSheet);
            Assert.AreEqual(HintPlanner.FirstWelcomeSeconds, again.Seconds);

            // This time it stays its three seconds: the day is counted and no second sheet follows in this run.
            (HintState after, HintRun stayed) = HintPlanner.Stayed(again.What, again.Entry, state, run, Noon.AddSeconds(20 + HintPlanner.CountsAfterSeconds));
            CollectionAssert.AreEqual(new[] { Day(Noon) }, after.WelcomeDays.ToArray());
            Assert.AreEqual(HintAction.Keep, Next(InGame, after, stayed, Noon.AddSeconds(40)).Action);
        }

        [TestMethod]
        public void WhyNoLineShows_IsSaidInTheGameOrByTheTray_NeverByBoth()
        {
            // The player is in the game, not in Kaption's window: what the Start card's balloon does with the card's answer.
            static SettingsWindow.CheckBalloon Tray(HintCheckAnswer card) =>
                SettingsWindow.BalloonDecision(windowActive: false, startPageInView: false, card);
            DateTimeOffset at = Noon.AddSeconds(70);

            foreach (var (kind, tip) in new[] { (SessionCheckKind.NoMatch, HintCatalog.TipNoMatch), (SessionCheckKind.NoDialogue, HintCatalog.TipNoDialogue) })
            {
                var run = HintRun.Start(Noon);
                HintSignals s = InGame with { Check = kind };
                HintCheckAnswer Answer(HintSignals signals, HintRun r, bool noPlace = false, double waited = 1) =>
                    HintPlanner.CheckAnswer(kind, signals, r, noPlace, waited);

                // The card showed it long enough to be read: no balloon, while the line is up and after it has gone.
                HintDecision d = Next(s, Settled, run, at);
                AssertShows(d, HintShowing.Tip, tip);
                HintRun said = HintPlanner.Stayed(d.What, d.Entry, Settled, run, at.AddSeconds(HintPlanner.CountsAfterSeconds)).Run;
                Assert.AreEqual(SettingsWindow.CheckBalloon.SaidInGame, Tray(Answer(s with { Showing = HintShowing.Tip, ShowingEntry = tip }, said)), $"{kind}: the card is saying it");
                Assert.AreEqual(SettingsWindow.CheckBalloon.SaidInGame, Tray(Answer(s, said)), $"{kind}: the card said it in this run");

                // The card cannot: the balloon goes out, as it did before there was a card.
                var cannot = new (string Why, HintCheckAnswer Answer)[]
                {
                    ("hints are off", Answer(s with { Enabled = false }, run)),
                    ("the card is not there (test mode, or it failed)", Answer(s with { Blocked = true }, run)),
                    ("no free place for the card", Answer(s, run, noPlace: true)),
                    ("the card waited for its moment and none came", Answer(s with { OwnWindowUp = true }, run, waited: HintPlanner.CheckWaitSeconds)),
                };
                foreach (var (why, answer) in cannot)
                    Assert.AreEqual(SettingsWindow.CheckBalloon.Send, Tray(answer), $"{kind}: {why}");

                // The card will, in a moment: the balloon waits for it.
                var soon = new (string Why, HintSignals Signals)[]
                {
                    ("asked in the same second the check came up", s),
                    ("the answer to a key is up", s with { Showing = HintShowing.Echo, ShowingEntry = HintCatalog.EchoStopped }),
                    ("the welcome sheet is up", s with { Showing = HintShowing.WelcomeSheet }),
                    ("one of Kaption's windows is open", s with { OwnWindowUp = true }),
                    ("the game is behind another window", s with { GameInFront = false }),
                };
                foreach (var (why, signals) in soon)
                    Assert.AreEqual(SettingsWindow.CheckBalloon.Wait, Tray(Answer(signals, run)), $"{kind}: {why}");

                // Once the balloon has it, the card keeps quiet about it for the rest of the run, though it is still due.
                HintRun left = HintPlanner.CheckLeftToTray(run, kind);
                Assert.AreEqual(HintAction.Keep, Next(s, Settled, left, at).Action, $"{kind}: left to the tray");
                Assert.AreEqual(SettingsWindow.CheckBalloon.Send, Tray(Answer(s, left)), $"{kind}: and the answer does not change back");
                AssertShows(Next(s, Settled, HintRun.Start(Noon.AddHours(1)), Noon.AddHours(1).AddSeconds(70)), HintShowing.Tip, tip, "the next run starts over");
            }

            // Exclusive full screen: nothing can be drawn over the game, so the tray keeps that one, at once.
            var black = InGame with { Check = SessionCheckKind.FullScreen };
            Assert.AreEqual(SettingsWindow.CheckBalloon.Send, Tray(HintPlanner.CheckAnswer(SessionCheckKind.FullScreen, black, HintRun.Start(Noon), false, 0)));
            Assert.AreEqual(0, HintPlanner.CheckLeftToTray(HintRun.Start(Noon), SessionCheckKind.FullScreen).ChecksLeftToTray, "nothing of the card's to leave");

            // The wait runs only while the player is out of Kaption's window. A visit ends it, however short, so a
            // player back in the game two seconds later is not met by the balloon when the card can speak again.
            SettingsWindow.CheckWait? wait = SettingsWindow.NextCheckWait(null, SessionCheckKind.NoMatch, windowActive: false, nowMs: 1_000);
            wait = SettingsWindow.NextCheckWait(wait, SessionCheckKind.NoMatch, windowActive: false, nowMs: 29_000);
            Assert.AreEqual(1_000, wait.Value.SinceMs, "the same check keeps its first question's time");
            wait = SettingsWindow.NextCheckWait(wait, SessionCheckKind.NoMatch, windowActive: true, nowMs: 29_500);
            Assert.IsNull(wait, "in Kaption's window nothing waits");
            wait = SettingsWindow.NextCheckWait(wait, SessionCheckKind.NoMatch, windowActive: false, nowMs: 31_500);
            Assert.AreEqual(31_500, wait.Value.SinceMs, "back in the game: a new wait, not the old one run out");
            Assert.AreEqual(31_800, SettingsWindow.NextCheckWait(wait, SessionCheckKind.NoDialogue, windowActive: false, nowMs: 31_800).Value.SinceMs, "another check waits for itself");
            Assert.IsNull(SettingsWindow.NextCheckWait(wait, SessionCheckKind.None, windowActive: false, nowMs: 32_000), "no check on the Start card, no wait");

            // In Kaption's own window the Start card decides as before, whatever the hint card would say.
            Assert.AreEqual(SettingsWindow.CheckBalloon.SeenOnCard, SettingsWindow.BalloonDecision(windowActive: true, startPageInView: true, HintCheckAnswer.Later));
            Assert.AreEqual(SettingsWindow.CheckBalloon.Wait, SettingsWindow.BalloonDecision(windowActive: true, startPageInView: false, HintCheckAnswer.Shown));
        }

        [TestMethod]
        public void ASentenceNobodyHadTimeToRead_IsNotSaid_AndTheBalloonNeverGoesOutBesideIt()
        {
            static SettingsWindow.CheckBalloon Tray(HintCheckAnswer card) =>
                SettingsWindow.BalloonDecision(windowActive: false, startPageInView: false, card);
            DateTimeOffset at = Noon.AddSeconds(70);

            foreach (var (kind, tip) in new[] { (SessionCheckKind.NoMatch, HintCatalog.TipNoMatch), (SessionCheckKind.NoDialogue, HintCatalog.TipNoDialogue) })
            {
                var run = HintRun.Start(Noon);
                HintSignals s = InGame with { Check = kind };
                HintSignals up = s with { Showing = HintShowing.Tip, ShowingEntry = tip };
                HintSignals away = s with { GameInFront = false };
                HintCheckAnswer Answer(HintSignals signals, HintRun r, bool noPlace = false, double waited = 1) =>
                    HintPlanner.CheckAnswer(kind, signals, r, noPlace, waited);

                HintDecision d = Next(s, Settled, run, at);
                AssertShows(d, HintShowing.Tip, tip);

                // On screen, not read yet. A balloon now would say it twice; "said" now would keep the balloon
                // silent until Kaption starts again. So it waits, however long the check has been asking.
                foreach (double waited in new[] { 0, 2, HintPlanner.CheckWaitSeconds, 10 * HintPlanner.CheckWaitSeconds })
                {
                    Assert.AreEqual(SettingsWindow.CheckBalloon.Wait, Tray(Answer(up, run, waited: waited)), $"{kind}: up, asked for {waited} s");
                    Assert.AreEqual(SettingsWindow.CheckBalloon.Wait, Tray(Answer(up with { Enabled = false }, run, waited: waited)),
                        $"{kind}: up, hints switched off this very second, asked for {waited} s");
                    Assert.AreEqual(SettingsWindow.CheckBalloon.Wait, Tray(Answer(up with { GameInFront = false }, run, noPlace: true, waited: waited)),
                        $"{kind}: up, the game has just left the front, asked for {waited} s");
                }
                Assert.AreEqual(run, HintPlanner.Shown(d, run, at), $"{kind}: appearing alone says nothing");
                // A card that failed counts nothing any more: what it left up must not hold the balloon back for good.
                Assert.AreEqual(SettingsWindow.CheckBalloon.Send, Tray(Answer(up with { Blocked = true }, run)), $"{kind}: the card failed with the sentence unread");

                // The player leaves the game a second later: the sentence goes, and it was not said.
                Assert.AreEqual(HintAction.Hide, Next(up with { GameInFront = false }, Settled, run, at.AddSeconds(1)).Action);

                // It counts at a look that keeps it, three seconds up. Not sooner, and never at a look that takes it
                // down, however late that look comes: Windows was locked at two seconds and the next look is at four.
                HintDecision kept = Next(up, Settled, run, at.AddSeconds(3));
                Assert.IsFalse(HintPlanner.CountsNow(kept, HintPlanner.CountsAfterSeconds - 0.2), $"{kind}: not read yet");
                Assert.IsTrue(HintPlanner.CountsNow(kept, HintPlanner.CountsAfterSeconds), $"{kind}: read");
                Assert.IsFalse(HintPlanner.CountsNow(Next(up with { GameInFront = false }, Settled, run, at.AddSeconds(4)), 4), $"{kind}: gone before anyone could read it");
                // Nor at a look that puts something else in its place: the player asked for the sheet.
                HintDecision replaced = Next(up with { Request = HintRequest.Sheet }, Settled, run, at.AddSeconds(4));
                AssertShows(replaced, HintShowing.AskedSheet);
                Assert.IsFalse(HintPlanner.CountsNow(replaced, 4), $"{kind}: replaced before anyone could read it");
                Assert.AreEqual(SettingsWindow.CheckBalloon.Wait, Tray(Answer(away, run, waited: 10)), $"{kind}: gone unread, the card still has its turn");
                AssertShows(Next(s, Settled, run, at.AddSeconds(12)), HintShowing.Tip, tip, $"{kind}: back in the game, the card says it again");

                // Taken down, the card fades for a moment and the sentence is still on screen: the balloon waits for
                // that too, with the wait run out and with the card's place lost. Another line's fade holds nothing back.
                HintSignals fading = away with { LeavingEntry = tip };
                Assert.AreEqual(SettingsWindow.CheckBalloon.Wait, Tray(Answer(fading, run, waited: HintPlanner.CheckWaitSeconds)), $"{kind}: fading, the wait ran out");
                Assert.AreEqual(SettingsWindow.CheckBalloon.Wait, Tray(Answer(fading, run, noPlace: true, waited: 10 * HintPlanner.CheckWaitSeconds)), $"{kind}: fading, its place lost");
                Assert.AreEqual(SettingsWindow.CheckBalloon.Send,
                    Tray(Answer(away with { LeavingEntry = HintCatalog.TipFixLine }, run, waited: HintPlanner.CheckWaitSeconds)), $"{kind}: something else is fading");
                // A card that failed in an earlier run has said nothing in this one: the balloon, as before the card.
                Assert.AreEqual(SettingsWindow.CheckBalloon.Send, Tray(Answer(s with { Blocked = true }, null)), $"{kind}: the card failed before this run");

                // The player stays away: the balloon says it after all, and the card leaves it alone from then on.
                Assert.AreEqual(SettingsWindow.CheckBalloon.Send, Tray(Answer(away, run, waited: HintPlanner.CheckWaitSeconds)), $"{kind}: gone unread and the wait ran out");
                Assert.AreEqual(HintAction.Keep, Next(s, Settled, HintPlanner.CheckLeftToTray(run, kind), at.AddSeconds(40)).Action);

                // Up for three seconds: read. Said for this run, whatever happens to the card afterwards.
                HintRun said = HintPlanner.Stayed(d.What, d.Entry, Settled, run, at.AddSeconds(HintPlanner.CountsAfterSeconds)).Run;
                var after = new (string Why, HintSignals Signals)[]
                {
                    ("it is still up", up),
                    ("it has gone", s),
                    ("the player left the game", away),
                    ("hints were switched off after it", s with { Enabled = false }),
                    ("the card failed after it", s with { Blocked = true }),
                };
                foreach (var (why, signals) in after)
                    foreach (double waited in new[] { 4, HintPlanner.CheckWaitSeconds })
                        Assert.AreEqual(SettingsWindow.CheckBalloon.SaidInGame, Tray(Answer(signals, said, waited: waited)), $"{kind}: read, then {why}");
                Assert.AreEqual(HintAction.Keep, Next(s, Settled, said, at.AddMinutes(5)).Action, $"{kind}: not again in this run");
            }
        }

        [TestMethod]
        public void ATeachingTipALineTookDownAtOnce_KeepsItsLifetimeShowings_AndIsTriedTwiceARun()
        {
            // The fix tip comes three to ten seconds after a line, in the middle of a conversation: the next line takes it down.
            HintState state = Untaught.WithKeyUsed(HintCatalog.KeyQuickTranslate);
            HintSignals afterLines = InGame with { LinesShown = 40, SecondsSinceLine = 5 };
            HintSignals up = afterLines with { Showing = HintShowing.Tip, ShowingEntry = HintCatalog.TipFixLine };
            var run = HintRun.Start(Noon);
            DateTimeOffset at = Noon.AddSeconds(200);

            for (int attempt = 1; attempt <= HintPlanner.TeachingTriesPerRun; attempt++)
            {
                HintDecision d = Next(afterLines, state, run, at);
                AssertShows(d, HintShowing.Tip, HintCatalog.TipFixLine, $"try {attempt}");
                run = HintPlanner.Shown(d, run, at);
                HintDecision taken = Next(up with { LineUp = true }, state, run, at.AddSeconds(1));
                Assert.AreEqual(HintAction.Hide, taken.Action, "the next line takes it down");
                Assert.IsFalse(HintPlanner.CountsNow(taken, HintPlanner.CountsAfterSeconds + 1), "and a look that takes it down never counts it, however late it comes");
                at = at.AddSeconds(40);
            }
            // Nobody could read either one: no third flash in this conversation.
            Assert.AreEqual(HintAction.Keep, Next(afterLines, state, run, at).Action, "tried twice in this run");

            // The next run tries again, and this time it stays its three seconds: the first of its two showings, not the third.
            var next = HintRun.Start(Noon.AddHours(1));
            DateTimeOffset later = Noon.AddHours(1).AddSeconds(200);
            HintDecision again = Next(afterLines, state, next, later);
            AssertShows(again, HintShowing.Tip, HintCatalog.TipFixLine, "the next run");
            next = HintPlanner.Shown(again, next, later);
            state = HintPlanner.Stayed(again.What, again.Entry, state, next, later.AddSeconds(HintPlanner.CountsAfterSeconds)).State;
            Assert.AreEqual(1, state.Tip(HintCatalog.TipFixLine.Id).Count, "the two that nobody read cost nothing");
            Assert.AreEqual(HintAction.Keep, Next(afterLines, state, HintRun.Start(Noon.AddHours(2)), Noon.AddHours(2).AddSeconds(200)).Action, "read: not again today");
        }

        [TestMethod]
        public void Off_MeansNothingButWhatThePlayerAsksFor()
        {
            var fresh = HintState.Create(Day(Noon), newInstall: true);
            var run = HintRun.Start(Noon);
            HintSignals off = InGame with { Enabled = false };

            Assert.AreEqual(HintAction.Keep, Next(off, fresh, run, Noon.AddSeconds(1)).Action, "no welcome sheet");
            Assert.AreEqual(HintAction.Keep, Next(off with { Check = SessionCheckKind.NoMatch }, fresh, run, Noon.AddSeconds(70)).Action, "no tip");
            Assert.AreEqual(HintAction.Keep, Next(off with { Request = HintRequest.SubtitlesHidden }, fresh, run, Noon.AddSeconds(70)).Action, "no echo");
            Assert.AreEqual(HintAction.Keep, Next(off with { Pinned = true }, fresh, run, Noon.AddSeconds(70)).Action, "no pinned sheet");
            Assert.AreEqual(HintAction.Hide, Next(off with { Pinned = true, Showing = HintShowing.PinnedSheet }, fresh, run, Noon.AddSeconds(70)).Action,
                "a pinned sheet that is up goes");

            // The sheet on request: with hints off, without the game in front, without translation.
            var asked = Next(new HintSignals { Request = HintRequest.Sheet, Bindings = AllBound }, fresh, null, Noon);
            AssertShows(asked, HintShowing.AskedSheet);
            Assert.AreEqual(HintPlanner.AskedSeconds, asked.Seconds);
            Assert.AreSame(fresh, HintPlanner.Stayed(asked.What, asked.Entry, fresh, null, Noon.AddSeconds(HintPlanner.AskedSeconds)).State, "asking costs no welcome day");

            // "Don't show again" is answered once, although hints are off by then.
            AssertShows(Next(off with { Request = HintRequest.HintsSwitchedOff, Showing = HintShowing.WelcomeSheet }, fresh, run, Noon.AddSeconds(5)),
                HintShowing.Echo, HintCatalog.EchoHintsOff);
        }

        [TestMethod]
        public void ARowWhoseKeyIsNotBound_IsLeftOut_AndATipWhoseKeyIsNotBound_IsNotShown()
        {
            var noHide = Without(HintCatalog.KeyHideSubtitles);
            CollectionAssert.AreEqual(
                new[] { HintCatalog.KeyQuickTranslate, HintCatalog.KeyFixLine, HintCatalog.KeyToggleTranslation },
                HintCatalog.RowsFor(noHide).Select(r => r.KeyId).ToArray(), "the other rows stay, in their order");
            Assert.AreEqual(4, HintCatalog.RowsFor(AllBound).Count);
            Assert.IsNull(noHide.Of(HintCatalog.KeyHideSubtitles));
            Assert.AreEqual("Ctrl+9005", noHide.Of(HintCatalog.KeyQuickTranslate), "a row carries the player's own binding");
            Assert.IsFalse(Without(HintCatalog.KeyShowDialogueFrame).Allows(HintCatalog.SheetFooter), "the footer falls back to its line without a shortcut");

            // No shortcut bound at all: there is no sheet, asked for or not.
            var none = HintBindings.From(_ => null);
            var fresh = HintState.Create(Day(Noon), newInstall: true);
            var run = HintRun.Start(Noon);
            Assert.AreEqual(HintAction.Keep, Next(InGame with { Bindings = none, Request = HintRequest.Sheet }, fresh, run, Noon.AddSeconds(1)).Action);
            Assert.AreEqual(HintAction.Keep, Next(InGame with { Bindings = none }, fresh, run, Noon.AddSeconds(1)).Action);
            Assert.AreEqual(HintAction.Keep, Next(InGame with { Bindings = none, Pinned = true }, fresh, run, Noon.AddSeconds(1)).Action);

            // A tip or an echo that would name a shortcut the player cannot press is not shown.
            AssertShows(Next(InGame, Untaught, run, Noon.AddSeconds(200)), HintShowing.Tip, HintCatalog.TipQuickTranslate);
            // Without the teaching tips' moments from the window, none is due.
            Assert.AreEqual(HintAction.Keep, Next(InGame with { TeachingOn = false }, Untaught, run, Noon.AddSeconds(200)).Action);
            Assert.AreEqual(HintAction.Keep,
                Next(InGame with { Bindings = Without(HintCatalog.KeyQuickTranslate) }, Untaught, run, Noon.AddSeconds(200)).Action);
            Assert.AreEqual(HintAction.Keep,
                Next(InGame with { Bindings = noHide, Request = HintRequest.SubtitlesHidden }, Settled, run, Noon.AddSeconds(200)).Action);
            // A line without a shortcut in it does not depend on any.
            AssertShows(Next(InGame with { Bindings = none, Check = SessionCheckKind.NoDialogue }, Settled, run, Noon.AddSeconds(70)),
                HintShowing.Tip, HintCatalog.TipNoDialogue);
        }

        [TestMethod]
        public void ThePinnedSheet_StaysWhileTranslatingInTheGame_AndGivesWayToWhatIsNew()
        {
            var run = HintRun.Start(Noon);
            var at = Noon.AddMinutes(5);
            HintSignals pinned = InGame with { Pinned = true };
            HintSignals up = pinned with { Showing = HintShowing.PinnedSheet };

            var d = Next(pinned, Settled, run, at);
            AssertShows(d, HintShowing.PinnedSheet);
            Assert.AreEqual(0, d.Seconds, "it stays");
            Assert.AreEqual(HintAction.Keep, Next(up, Settled, run, at).Action);
            Assert.AreEqual(HintAction.Keep, Next(up with { LineUp = true }, Settled, run, at).Action, "through a dialogue: the player pinned it");

            foreach (var (why, s) in new[]
            {
                ("the game left the front", up with { GameInFront = false }),
                ("translation stopped", up with { Translating = false }),
                ("one of Kaption's windows opened", up with { OwnWindowUp = true }),
                ("the player unpinned it", up with { Pinned = false }),
            })
                Assert.AreEqual(HintAction.Hide, Next(s, Settled, run, at).Action, why);

            // Something new to say replaces it, and the sheet comes back when that is gone.
            AssertShows(Next(up with { Check = SessionCheckKind.NoMatch }, Settled, run, at), HintShowing.Tip, HintCatalog.TipNoMatch);
            AssertShows(Next(up with { Request = HintRequest.SubtitlesHidden }, Settled, run, at), HintShowing.Echo, HintCatalog.EchoSubtitlesHidden);
            Assert.AreEqual(HintAction.Keep, Next(pinned with { Showing = HintShowing.Echo }, Settled, run, at).Action);

            // A pinned sheet is the welcome already: no second sheet, no day spent.
            var fresh = HintState.Create(Day(Noon), newInstall: true);
            AssertShows(Next(pinned, fresh, HintRun.Start(Noon), Noon.AddSeconds(1)), HintShowing.PinnedSheet);
        }

        [TestMethod]
        public void ASheetThePlayerHid_DoesNotComeBackAsTheWelcomeSheet()
        {
            // A new install starts a run with the sheet pinned; the pinned sheet is the welcome, so none was counted.
            var fresh = HintState.Create(Day(Noon), newInstall: true);
            var run = HintRun.Start(Noon);
            DateTimeOffset at = Noon.AddSeconds(10);
            AssertShows(Next(InGame with { Pinned = true }, fresh, run, at), HintShowing.PinnedSheet);

            // "x" unpins it. Without more, the next look would put the welcome sheet where the player just closed one.
            AssertShows(Next(InGame, fresh, run, at), HintShowing.WelcomeSheet);
            HintRun dismissed = HintPlanner.SheetDismissed(run);
            Assert.AreEqual(HintAction.Keep, Next(InGame, fresh, dismissed, at).Action, "hidden by the player: nothing comes back in this run");

            // The day was not spent: the next run still welcomes, and a request still shows the sheet in this one.
            AssertShows(Next(InGame, fresh, HintRun.Start(Noon.AddHours(1)), Noon.AddHours(1).AddSeconds(5)), HintShowing.WelcomeSheet);
            AssertShows(Next(InGame with { Request = HintRequest.Sheet }, fresh, dismissed, at), HintShowing.AskedSheet);
            Assert.IsNull(HintPlanner.SheetDismissed(null));
        }

        [TestMethod]
        public void WhatIsUp_GoesWhenTheGameLeavesTheFront_OrAWindowOfKaptionsOpens()
        {
            var run = HintRun.Start(Noon);
            var rows = new (HintShowing Up, HintSignals Change, HintAction Expected, string Why)[]
            {
                (HintShowing.Tip, InGame, HintAction.Keep, "nothing changed"),
                (HintShowing.Tip, InGame with { LineUp = true }, HintAction.Hide, "a new line takes a tip down: its subtitle would push the card away while the player reads"),
                (HintShowing.WelcomeSheet, InGame with { LineUp = true }, HintAction.Hide, "and the welcome sheet"),
                (HintShowing.Tip, InGame with { LineUp = true, ShowingEntry = HintCatalog.TipSubtitlesHidden }, HintAction.Keep, "the tip about hidden subtitles is about that line"),
                (HintShowing.Echo, InGame with { LineUp = true }, HintAction.Keep, "the answer to a key stays through a line"),
                (HintShowing.AskedSheet, InGame with { LineUp = true }, HintAction.Keep, "so does a sheet the player asked for"),
                (HintShowing.PinnedSheet, InGame with { LineUp = true, Pinned = true }, HintAction.Keep, "and one the player pinned"),
                (HintShowing.Tip, InGame with { GameInFront = false }, HintAction.Hide, "the game left the front"),
                (HintShowing.WelcomeSheet, InGame with { OwnWindowUp = true }, HintAction.Hide, "the area picker opened"),
                (HintShowing.WelcomeSheet, InGame with { Enabled = false }, HintAction.Hide, "switched off in Settings"),
                (HintShowing.AskedSheet, InGame with { GameInFront = false, Translating = false, Enabled = false }, HintAction.Keep, "asked for outside the game"),
                (HintShowing.AskedSheet, InGame with { OwnWindowUp = true }, HintAction.Hide, "a dialog opened over it"),
                (HintShowing.Echo, InGame with { GameInFront = false, OwnWindowUp = true }, HintAction.Keep, "an echo is never cut short"),
                (HintShowing.Echo, InGame with { Blocked = true }, HintAction.Hide, "the test mode shows nothing"),
            };
            foreach (var (showing, change, expected, why) in rows)
                Assert.AreEqual(expected, Next(change with { Showing = showing }, Settled, run, Noon.AddMinutes(1)).Action, $"{showing}: {why}");
        }
    }
}
