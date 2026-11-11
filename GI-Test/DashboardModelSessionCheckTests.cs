using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Games;
using GI_Subtitles.Services.Translation;
using GI_Subtitles.Views.Shell;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace GI_Test
{
    /// <summary>
    /// The first-session check: the Start card's three "why no line shows" states and the one-time "it works", from
    /// the reading loop's evidence. Pure model, the counters and the frame test; no window.
    /// </summary>
    [TestClass]
    public class DashboardModelSessionCheckTests
    {
        private static readonly SessionCheckThresholds Defaults = SessionCheckThresholds.From(new GameRegionProfile());
        private static readonly SessionCheckContext Playing = new(Translating: true, LocalGameWindow: true, FirstLinePending: false);
        private static readonly SessionCheckContext NewInstall = Playing with { FirstLinePending = true };

        private static SessionEvidenceSnapshot Seen(int dialogue = 0, int matched = 0, int black = 0, int packHits = 0,
            int unmatchedRun = 0, double inFront = 0, double blackSeconds = 0, bool picture = true, double pictureSeconds = 0, int textReads = -1) =>
            // Every counted read is a read with text; a test that needs others (HUD numbers, repeats) says so.
            new(dialogue, matched, black, packHits, unmatchedRun, inFront, blackSeconds, picture, pictureSeconds,
                textReads >= 0 ? textReads : dialogue + packHits);

        private static SessionCheckKind Raised(SessionEvidenceSnapshot e, SessionCheckContext c) =>
            SessionCheckModel.Next(default, e, Defaults, c).Kind;

        // ── Each state from its own evidence ────────────────────────────────

        [TestMethod]
        public void FullScreen_NeedsBlackFramesForTheWholeThreshold_BeforeAnyLineWasFound()
        {
            Assert.AreEqual(SessionCheckKind.FullScreen, Raised(Seen(black: 300, inFront: 30, blackSeconds: 30, picture: false), Playing));
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(black: 290, inFront: 29, blackSeconds: 29, picture: false), Playing),
                "a game's own black launch screen is shorter than the threshold");
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(dialogue: 3, matched: 3, black: 400, inFront: 200, blackSeconds: 40, picture: true), Playing),
                "a story's fade to black comes after lines were found: the game's, not full screen");
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(packHits: 1, black: 400, inFront: 200, blackSeconds: 40), Playing),
                "menu text was read in this run, so the capture works");
        }

        [TestMethod]
        public void FullScreen_IsStillSaid_WhenTheGameGoesFullScreenAfterItsSplashWindow()
        {
            // The launcher's splash is a picture; then the game takes the screen exclusively and every frame is black.
            var e = new SessionEvidence();
            long t = 10_000;
            for (int i = 0; i <= 50; i++) e.OnGameFrame(black: false, t + i * 100);          // 5 s of splash
            for (int i = 1; i <= 299; i++) e.OnGameFrame(black: true, t + 5_000 + i * 100);  // 29.9 s of black
            Assert.IsTrue(e.Snapshot().SawPicture);
            Assert.AreEqual(SessionCheckKind.None, Raised(e.Snapshot(), Playing));
            e.OnGameFrame(black: true, t + 5_000 + 300 * 100);
            Assert.AreEqual(SessionCheckKind.FullScreen, Raised(e.Snapshot(), Playing));
        }

        [TestMethod]
        public void FullScreen_IsNeverSaidInACloudSession()
        {
            var cloud = Playing with { LocalGameWindow = false };
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(black: 900, inFront: 90, blackSeconds: 90, picture: false), cloud));
            // The other two rest on what was read, not on the window, so a cloud session still gets them.
            Assert.AreEqual(SessionCheckKind.NoMatch, Raised(Seen(dialogue: 20, unmatchedRun: 20, inFront: 90), cloud));
            Assert.AreEqual(SessionCheckKind.NoDialogue, Raised(Seen(inFront: 60), cloud with { FirstLinePending = true }));
        }

        [TestMethod]
        public void NoMatch_NeedsARunOfSpokenLinesWithoutOnePackHit()
        {
            Assert.AreEqual(SessionCheckKind.NoMatch, Raised(Seen(dialogue: 20, unmatchedRun: 20, inFront: 120), Playing));
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(dialogue: 19, unmatchedRun: 19, inFront: 120), Playing));
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(dialogue: 60, matched: 40, unmatchedRun: 3, inFront: 600), Playing),
                "misses between matched lines are ordinary play");
        }

        [TestMethod]
        public void NoDialogue_IsForANewInstallOnly_AfterAQuietMinuteInFront()
        {
            Assert.AreEqual(SessionCheckKind.NoDialogue, Raised(Seen(inFront: 60), NewInstall));
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(inFront: 59), NewInstall));
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(inFront: 3600), Playing),
                "an install that has shown lines roams for an hour without a nag");
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(packHits: 1, inFront: 600), NewInstall),
                "menu text the pack knows was read: the area sees the game");
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(dialogue: 1, unmatchedRun: 1, inFront: 600), NewInstall));
        }

        [TestMethod]
        public void WhenTwoCausesFit_TheOneWithDirectEvidenceShows()
        {
            // A black picture also means nothing was read for a minute: advising the area there would be wrong.
            var black = Seen(black: 900, inFront: 90, blackSeconds: 90, picture: false);
            Assert.AreEqual(SessionCheckKind.FullScreen, Raised(black, NewInstall));
            Assert.AreEqual(SessionCheckKind.FullScreen, SessionCheckModel.Evidence(black, Defaults, NewInstall));
        }

        // ── Held, not flapping ──────────────────────────────────────────────

        [TestMethod]
        public void AState_StaysUntilALineMatches_AndDoesNotFlapAcrossRefreshes()
        {
            var state = default(SessionCheckState);
            var kinds = new System.Collections.Generic.List<SessionCheckKind>();
            void Step(SessionEvidenceSnapshot e)
            {
                state = SessionCheckModel.Next(state, e, Defaults, Playing);
                kinds.Add(state.Kind);
            }

            Step(Seen(dialogue: 19, unmatchedRun: 19, inFront: 100));
            Step(Seen(dialogue: 20, unmatchedRun: 20, inFront: 101));   // raised
            Step(Seen(dialogue: 21, unmatchedRun: 21, inFront: 102));
            Step(Seen(dialogue: 21, unmatchedRun: 21, inFront: 160));   // nothing new: still there
            Step(Seen(dialogue: 22, matched: 1, unmatchedRun: 0, inFront: 161));   // a line matched: gone
            Step(Seen(dialogue: 23, matched: 1, unmatchedRun: 1, inFront: 162));   // one miss does not bring it back
            Step(Seen(dialogue: 41, matched: 1, unmatchedRun: 19, inFront: 200));
            Step(Seen(dialogue: 42, matched: 1, unmatchedRun: 20, inFront: 201));  // its own evidence again

            CollectionAssert.AreEqual(new[]
            {
                SessionCheckKind.None, SessionCheckKind.NoMatch, SessionCheckKind.NoMatch, SessionCheckKind.NoMatch,
                SessionCheckKind.None, SessionCheckKind.None, SessionCheckKind.None, SessionCheckKind.NoMatch,
            }, kinds);
        }

        [TestMethod]
        public void AMatchBetweenTwoRefreshes_StillClearsTheState()
        {
            // The run was reset by a match and has grown again before the card looked: the match count tells.
            var raised = SessionCheckModel.Next(default, Seen(dialogue: 20, unmatchedRun: 20), Defaults, Playing);
            var later = SessionCheckModel.Next(raised, Seen(dialogue: 24, matched: 1, unmatchedRun: 3), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.None, later.Kind);
        }

        [TestMethod]
        public void APackHitOutsideDialogue_ClearsTheLanguageQuestion()
        {
            var raised = SessionCheckModel.Next(default, Seen(dialogue: 20, unmatchedRun: 20), Defaults, Playing);
            var later = SessionCheckModel.Next(raised, Seen(dialogue: 20, packHits: 1, unmatchedRun: 0), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.None, later.Kind, "English menu text the pack knows answers \"is the game in English?\"");
        }

        [TestMethod]
        public void FullScreen_GoesWhenAPictureArrives_AndNeedsAWholeNewRunToComeBack()
        {
            var state = SessionCheckModel.Next(default, Seen(black: 300, inFront: 30, blackSeconds: 30, picture: false), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.FullScreen, state.Kind);
            state = SessionCheckModel.Next(state, Seen(black: 300, inFront: 90, blackSeconds: 30, picture: false), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.FullScreen, state.Kind, "the player is in another window: nothing new, it stays");
            // One frame comes through while the display mode changes, then black again: nothing is proved, it stays.
            state = SessionCheckModel.Next(state, Seen(black: 300, inFront: 90.1, blackSeconds: 0, picture: true, pictureSeconds: 0.1), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.FullScreen, state.Kind);
            state = SessionCheckModel.Next(state, Seen(black: 320, inFront: 93, blackSeconds: 2, picture: true), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.FullScreen, state.Kind);
            // As much unbroken picture as it took black to raise it: the game is visible after all.
            state = SessionCheckModel.Next(state, Seen(black: 320, inFront: 122, blackSeconds: 0, picture: true, pictureSeconds: 29), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.FullScreen, state.Kind);
            state = SessionCheckModel.Next(state, Seen(black: 320, inFront: 123, blackSeconds: 0, picture: true, pictureSeconds: 30), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.None, state.Kind);
            state = SessionCheckModel.Next(state, Seen(black: 500, inFront: 160, blackSeconds: 29, picture: true), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.None, state.Kind);
            state = SessionCheckModel.Next(state, Seen(black: 510, inFront: 161, blackSeconds: 30, picture: true), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.FullScreen, state.Kind, "its own evidence again");
            // Once a line was found the capture is known to work: black after that is the game's own.
            state = SessionCheckModel.Next(state, Seen(dialogue: 1, matched: 1, black: 510, inFront: 130, blackSeconds: 0, picture: true), Defaults, Playing);
            state = SessionCheckModel.Next(state, Seen(dialogue: 1, matched: 1, black: 1500, inFront: 400, blackSeconds: 120, picture: true), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.None, state.Kind);
        }

        [TestMethod]
        public void FullScreen_GivesWayOnceLinesAreReadAgain_EvenBetweenFades()
        {
            // Raised, then the player switches to Borderless, but the game is in German: lines are read and not
            // found, and fades keep every picture run short. The capture works, so the language question must get through.
            var state = SessionCheckModel.Next(default, Seen(black: 300, inFront: 30, blackSeconds: 30, picture: false), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.FullScreen, state.Kind);
            state = SessionCheckModel.Next(state, Seen(black: 300, inFront: 38, picture: true, pictureSeconds: 8), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.FullScreen, state.Kind, "a picture alone, for less than the threshold, proves nothing yet");
            state = SessionCheckModel.Next(state, Seen(dialogue: 1, unmatchedRun: 1, black: 310, inFront: 45, picture: true, pictureSeconds: 3), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.None, state.Kind, "a line was read: the capture works");
            state = SessionCheckModel.Next(state, Seen(dialogue: 19, unmatchedRun: 19, black: 400, inFront: 140, blackSeconds: 4, picture: true), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.None, state.Kind);
            state = SessionCheckModel.Next(state, Seen(dialogue: 20, unmatchedRun: 20, black: 400, inFront: 150, picture: true, pictureSeconds: 6), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.NoMatch, state.Kind);
        }

        [TestMethod]
        public void FullScreen_GivesWay_WhenTheSameLineIsReadAgain_ThroughTheRealCounters()
        {
            // A line is read and missed, the picture goes black for the whole threshold, then the very same line is
            // read again: the resolver answers from its memo and the miss is not counted twice, yet text was read.
            var e = new SessionEvidence();
            var miss = new SubtitleResolution { Source = ResolutionSource.Matcher, Content = "" };
            var memoMiss = new SubtitleResolution { Source = ResolutionSource.MemoCache, Content = "" };
            const string line = "Guten Tag, Reisender.";
            long t = 70_000;
            e.OnGameFrame(black: false, t);
            e.OnRead(SessionEvidence.Classify(line, "Paimon", miss, false), line);
            for (int i = 1; i <= 301; i++) e.OnGameFrame(black: true, t + i * 100);
            var state = SessionCheckModel.Next(default, e.Snapshot(), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.FullScreen, state.Kind);

            e.OnGameFrame(black: false, t + 30_200);
            state = SessionCheckModel.Next(state, e.Snapshot(), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.FullScreen, state.Kind, "one frame of picture, nothing read yet");

            Assert.AreEqual(ReadOutcome.Ignored, SessionEvidence.Classify(line, "Paimon", memoMiss, false));
            e.OnRead(ReadOutcome.Ignored, line);
            Assert.AreEqual(1, e.Snapshot().DialogueReads, "the miss is still counted once");
            state = SessionCheckModel.Next(state, e.Snapshot(), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.None, state.Kind, "text was read: the capture works");
        }

        [TestMethod]
        public void ABalloon_WaitsWhileThePlayerIsOnAnotherPage()
        {
            Assert.AreEqual(GI_Subtitles.Views.SettingsWindow.CheckBalloon.Send, GI_Subtitles.Views.SettingsWindow.BalloonDecision(windowActive: false, startPageInView: false));
            Assert.AreEqual(GI_Subtitles.Views.SettingsWindow.CheckBalloon.Send, GI_Subtitles.Views.SettingsWindow.BalloonDecision(windowActive: false, startPageInView: true),
                "Start is open behind the game: the player is not looking at it");
            Assert.AreEqual(GI_Subtitles.Views.SettingsWindow.CheckBalloon.SeenOnCard, GI_Subtitles.Views.SettingsWindow.BalloonDecision(windowActive: true, startPageInView: true));
            Assert.AreEqual(GI_Subtitles.Views.SettingsWindow.CheckBalloon.Wait, GI_Subtitles.Views.SettingsWindow.BalloonDecision(windowActive: true, startPageInView: false),
                "on another page the card is not in view: the balloon is neither sent over the window nor used up");
        }

        [TestMethod]
        public void NoDialogue_GivesWayToWhatIsReadNext_NeverTheOtherWayRound()
        {
            var state = SessionCheckModel.Next(default, Seen(inFront: 60), Defaults, NewInstall);
            Assert.AreEqual(SessionCheckKind.NoDialogue, state.Kind);
            // Lines are read now: "no dialogue" is disproved; the language question waits for its own run.
            state = SessionCheckModel.Next(state, Seen(dialogue: 2, unmatchedRun: 2, inFront: 70), Defaults, NewInstall);
            Assert.AreEqual(SessionCheckKind.None, state.Kind);
            state = SessionCheckModel.Next(state, Seen(dialogue: 20, unmatchedRun: 20, inFront: 200), Defaults, NewInstall);
            Assert.AreEqual(SessionCheckKind.NoMatch, state.Kind);
            // The shown state is never traded down for a more general one.
            state = SessionCheckModel.Next(state, Seen(dialogue: 20, unmatchedRun: 20, inFront: 900), Defaults, NewInstall);
            Assert.AreEqual(SessionCheckKind.NoMatch, state.Kind);
        }

        [TestMethod]
        public void ThePlayerActing_PutsTheStateAway_UntilTranslationStartsAgain()
        {
            var evidence = Seen(dialogue: 30, unmatchedRun: 30, inFront: 300);
            var state = SessionCheckModel.Next(default, evidence, Defaults, Playing).Dismiss();
            Assert.AreEqual(SessionCheckKind.None, state.Kind);
            state = SessionCheckModel.Next(state, Seen(dialogue: 60, unmatchedRun: 60, inFront: 600), Defaults, Playing);
            Assert.AreEqual(SessionCheckKind.None, state.Kind, "no second ask in the same run");

            state = SessionCheckModel.Next(state, evidence, Defaults, Playing with { Translating = false });
            Assert.AreEqual(default, state, "stopping forgets everything");
            Assert.AreEqual(SessionCheckKind.NoMatch, SessionCheckModel.Next(state, evidence, Defaults, Playing).Kind);
        }

        [TestMethod]
        public void NotTranslating_ShowsNothing_WhateverTheEvidence()
        {
            var stopped = Playing with { Translating = false };
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(black: 900, inFront: 90, blackSeconds: 90, picture: false), stopped));
            Assert.AreEqual(SessionCheckKind.None, Raised(Seen(dialogue: 50, unmatchedRun: 50), stopped));
        }

        // ── Thresholds are data ─────────────────────────────────────────────

        [TestMethod]
        public void Thresholds_ComeFromTheProfile_WithDefaults_AndAManifestMayOverrideThem()
        {
            var defaults = new GameRegionProfile();
            Assert.AreEqual(new SessionCheckThresholds(30, 20, 60), SessionCheckThresholds.From(defaults));
            foreach (var registered in GameRegionProfile.RegisteredProfiles)
                Assert.AreEqual(Defaults, SessionCheckThresholds.From(registered), $"{registered.GameId} keeps the defaults until it is measured");

            string root = RenderTestKit.RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "GI-Test", "Fixtures", "games", "fixturegame.json"))).AsObject();
            json["desktop"]["sessionCheck"] = new JsonObject { ["blackSeconds"] = 12, ["unmatchedReads"] = 0 };
            var profile = GameProfileBuilder.Build(GameManifestLoader.Parse(json.ToJsonString()));
            var t = SessionCheckThresholds.From(profile);
            Assert.AreEqual(new SessionCheckThresholds(12, 0, 60), t, "a key left out keeps the default");

            Assert.AreEqual(SessionCheckKind.FullScreen,
                SessionCheckModel.Next(default, Seen(black: 120, inFront: 12, blackSeconds: 12, picture: false), t, Playing).Kind);
            Assert.AreEqual(SessionCheckKind.None,
                SessionCheckModel.Next(default, Seen(dialogue: 500, unmatchedRun: 500), t, Playing).Kind, "0 switches a state off for that game");
        }

        [TestMethod]
        public void TheSchema_DescribesTheSameThreeKeys()
        {
            string root = RenderTestKit.RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "games", "game.schema.json")));
            var keys = schema["$defs"]["desktop"]["properties"]["sessionCheck"]["properties"].AsObject().Select(k => k.Key).ToArray();
            CollectionAssert.AreEquivalent(new[] { "blackSeconds", "unmatchedReads", "quietSeconds" }, keys);
        }

        // ── The card ────────────────────────────────────────────────────────

        private static StartCardInputs Translating(SessionCheckKind check = SessionCheckKind.None, bool firstLine = false) => new()
        {
            Translating = true, Reader = ReaderState.Ready, PackOnDisk = true, SessionCheck = check, FirstLineShown = firstLine,
        };

        [TestMethod]
        public void TheCard_ShowsEachCheckWithItsOneFix()
        {
            var full = StartCardModel.Compute(Translating(SessionCheckKind.FullScreen));
            Assert.AreEqual((StartCardKind.SessionFullScreen, StartCardAction.OpenBorderlessGuide), (full.Kind, full.Action));
            var miss = StartCardModel.Compute(Translating(SessionCheckKind.NoMatch));
            Assert.AreEqual((StartCardKind.SessionNoMatch, StartCardAction.OpenLanguageGuide), (miss.Kind, miss.Action));
            var quiet = StartCardModel.Compute(Translating(SessionCheckKind.NoDialogue));
            Assert.AreEqual((StartCardKind.SessionNoDialogue, StartCardAction.CheckArea), (quiet.Kind, quiet.Action));
            foreach (var s in new[] { full, miss, quiet })
            {
                Assert.IsFalse(StartCardModel.IsGradientAction(s.Action), "the brand gradient stays with start and stop");
                Assert.IsTrue(StartCardModel.IsTranslating(s.Kind));
            }
            // The same evidence fits an area in the wrong place for these two; "no dialogue" already leads with it.
            Assert.IsTrue(StartCardModel.OffersAreaAsSecondFix(full.Kind) && StartCardModel.OffersAreaAsSecondFix(miss.Kind));
            Assert.IsFalse(StartCardModel.OffersAreaAsSecondFix(quiet.Kind) || StartCardModel.OffersAreaAsSecondFix(StartCardKind.Translating));
        }

        [TestMethod]
        public void TheChecks_RankBelowEveryExistingProblem_AndAbovePlainTranslating()
        {
            var limit = StartCardModel.Compute(new StartCardInputs { DeviceLimit = true, Translating = true, SessionCheck = SessionCheckKind.FullScreen });
            Assert.AreEqual(StartCardKind.DeviceLimit, limit.Kind);

            // Not translating: every existing state keeps its place, whatever the check input says.
            var cases = new (StartCardInputs Inputs, StartCardKind Kind)[]
            {
                (new StartCardInputs { Reader = ReaderState.Ready, PackOnDisk = true }, StartCardKind.Stopped),
                (new StartCardInputs { GameSwitchPending = true, Reader = ReaderState.Ready, PackOnDisk = true }, StartCardKind.GameSwitchPending),
                (new StartCardInputs { Offline = true, Reader = ReaderState.Ready }, StartCardKind.Offline),
                (new StartCardInputs { Downloading = true, Reader = ReaderState.Loading }, StartCardKind.Downloading),
                (new StartCardInputs { Reader = ReaderState.Unavailable, PackOnDisk = true }, StartCardKind.ReaderUnavailable),
                (new StartCardInputs { Reader = ReaderState.Failed, PackOnDisk = true }, StartCardKind.ReaderFailed),
                (new StartCardInputs { PackMissing = true, PackComing = true, Reader = ReaderState.Ready }, StartCardKind.PackComing),
                (new StartCardInputs { PackMissing = true, Reader = ReaderState.Ready }, StartCardKind.PackMissing),
                (new StartCardInputs { IndexBuilding = true, Reader = ReaderState.Loading, PackOnDisk = true }, StartCardKind.BuildingIndex),
                (new StartCardInputs { Reader = ReaderState.Loading, PackOnDisk = true }, StartCardKind.StartingReader),
                (new StartCardInputs { Reader = ReaderState.Ready, PackOnDisk = true, AutoStartBlockedBy = GI_Subtitles.Services.Automation.StartGate.Region }, StartCardKind.AutoStartBlocked),
                (new StartCardInputs { WaitingForGame = true, Reader = ReaderState.Ready, PackOnDisk = true }, StartCardKind.WaitingForGame),
            };
            foreach (var (inputs, kind) in cases)
            {
                Assert.AreEqual(kind, StartCardModel.Compute(inputs).Kind, $"{kind} without a check");
                var withCheck = new StartCardInputs
                {
                    Reader = inputs.Reader, PackOnDisk = inputs.PackOnDisk, GameSwitchPending = inputs.GameSwitchPending, Offline = inputs.Offline,
                    Downloading = inputs.Downloading, PackMissing = inputs.PackMissing, PackComing = inputs.PackComing,
                    IndexBuilding = inputs.IndexBuilding, AutoStartBlockedBy = inputs.AutoStartBlockedBy, WaitingForGame = inputs.WaitingForGame,
                    SessionCheck = SessionCheckKind.NoMatch, FirstLineShown = true,
                };
                Assert.AreEqual(kind, StartCardModel.Compute(withCheck).Kind, $"{kind} is not replaced by a check while nothing translates");
            }

            Assert.AreEqual(StartCardKind.Translating, StartCardModel.Compute(Translating()).Kind);
            Assert.AreEqual(StartCardAction.Stop, StartCardModel.Compute(Translating()).Action);
        }

        [TestMethod]
        public void ACheck_IsNeverLaidOverAProblemTheCardKnows()
        {
            // Translation "wins" over these on the card (it read Translating before the check existed); with one of
            // them present, advice about the game's language or the area would point the player the wrong way.
            var problems = new[]
            {
                new StartCardInputs { Translating = true, Reader = ReaderState.Failed, PackOnDisk = true, SessionCheck = SessionCheckKind.NoMatch },
                new StartCardInputs { Translating = true, Reader = ReaderState.Unavailable, PackOnDisk = true, SessionCheck = SessionCheckKind.FullScreen },
                new StartCardInputs { Translating = true, Reader = ReaderState.Ready, Offline = true, PackOnDisk = false, SessionCheck = SessionCheckKind.NoDialogue },
                new StartCardInputs { Translating = true, Reader = ReaderState.Ready, PackMissing = true, SessionCheck = SessionCheckKind.NoMatch },
                new StartCardInputs { Translating = true, Reader = ReaderState.Ready, PackMissing = true, PackComing = true, SessionCheck = SessionCheckKind.NoMatch },
                new StartCardInputs { Translating = true, Reader = ReaderState.Ready, PackOnDisk = true, GameSwitchPending = true, SessionCheck = SessionCheckKind.NoMatch },
            };
            foreach (var inputs in problems)
            {
                var s = StartCardModel.Compute(inputs);
                Assert.AreEqual(StartCardKind.Translating, s.Kind, "as before the check existed");
                Assert.AreEqual(StartCardAction.Stop, s.Action);
                Assert.AreEqual(SessionCheckKind.None, StartCardModel.SessionCheckOf(s.Kind), "so no balloon either");
            }
            // Offline with the pack on disk is no problem: translation works from it, and a check may show.
            var offline = StartCardModel.Compute(new StartCardInputs
            {
                Translating = true, Reader = ReaderState.Ready, Offline = true, PackOnDisk = true, SessionCheck = SessionCheckKind.NoMatch,
            });
            Assert.AreEqual(StartCardKind.SessionNoMatch, offline.Kind);
            Assert.IsTrue(offline.ShowOfflineNote);
        }

        [TestMethod]
        public void TheBalloon_FollowsTheCard_NotTheEvidence()
        {
            var limit = StartCardModel.Compute(new StartCardInputs { DeviceLimit = true, Translating = true, SessionCheck = SessionCheckKind.NoMatch });
            Assert.AreEqual(SessionCheckKind.None, StartCardModel.SessionCheckOf(limit.Kind), "the card says device limit: no language balloon");
            Assert.AreEqual(SessionCheckKind.FullScreen, StartCardModel.SessionCheckOf(StartCardModel.Compute(Translating(SessionCheckKind.FullScreen)).Kind));
            Assert.AreEqual(SessionCheckKind.NoMatch, StartCardModel.SessionCheckOf(StartCardModel.Compute(Translating(SessionCheckKind.NoMatch)).Kind));
            Assert.AreEqual(SessionCheckKind.NoDialogue, StartCardModel.SessionCheckOf(StartCardModel.Compute(Translating(SessionCheckKind.NoDialogue)).Kind));
            foreach (StartCardKind kind in System.Enum.GetValues(typeof(StartCardKind)))
                if (!kind.ToString().StartsWith("Session"))
                    Assert.AreEqual(SessionCheckKind.None, StartCardModel.SessionCheckOf(kind), kind.ToString());
        }

        [TestMethod]
        public void TheFirstLine_IsConfirmedOnTheCard_WithStopStillThere_AndACheckComesFirst()
        {
            var works = StartCardModel.Compute(Translating(firstLine: true));
            Assert.AreEqual((StartCardKind.FirstLineWorks, StartCardAction.Stop), (works.Kind, works.Action));
            Assert.AreEqual(StartCardKind.SessionNoMatch, StartCardModel.Compute(Translating(SessionCheckKind.NoMatch, firstLine: true)).Kind);
            var stopped = new StartCardInputs { Reader = ReaderState.Ready, PackOnDisk = true, FirstLineShown = true };
            Assert.AreEqual(StartCardKind.Stopped, StartCardModel.Compute(stopped).Kind);
        }

        [TestMethod]
        public void TheAreaNote_IsNotStackedUnderACheck()
        {
            var inputs = new StartCardInputs
            {
                Translating = true, Reader = ReaderState.Ready, PackOnDisk = true, RegionAutoFill = RegionAutoFillNote.Set,
                SessionCheck = SessionCheckKind.NoDialogue,
            };
            Assert.IsFalse(StartCardModel.Compute(inputs).ShowRegionAutoSetNote);
        }

        // ── The counters ────────────────────────────────────────────────────

        [TestMethod]
        public void Evidence_CountsTimeInFront_FromFrameToFrame_AndDropsLongGaps()
        {
            var e = new SessionEvidence();
            long t = 1_000_000;
            for (int i = 0; i <= 100; i++) e.OnGameFrame(black: false, t + i * 100);   // 10 s of frames
            Assert.AreEqual(10.0, e.Snapshot().InFrontSeconds, 0.001);
            e.OnGameFrame(black: false, t + 10_000 + 600_000);   // back after ten minutes in another window
            Assert.AreEqual(10.0, e.Snapshot().InFrontSeconds, 0.001, "the gap is not time in front");
            e.OnGameFrame(black: false, t + 10_000 + 600_100);
            Assert.AreEqual(10.1, e.Snapshot().InFrontSeconds, 0.001);
        }

        [TestMethod]
        public void Evidence_BlackRun_BuildsOnlyOnBlackFrames_AndAPictureEndsIt()
        {
            var e = new SessionEvidence();
            long t = 5_000;
            for (int i = 0; i <= 300; i++) e.OnGameFrame(black: true, t + i * 100);
            var s = e.Snapshot();
            Assert.AreEqual(30.0, s.BlackSeconds, 0.001);
            Assert.AreEqual(301, s.BlackFrames);
            Assert.IsFalse(s.SawPicture);
            Assert.AreEqual(SessionCheckKind.FullScreen, Raised(s, Playing));

            e.OnGameFrame(black: false, t + 30_100);
            s = e.Snapshot();
            Assert.AreEqual(0.0, s.BlackSeconds, 0.001);
            Assert.IsTrue(s.SawPicture);
            Assert.AreEqual(301, s.BlackFrames, "the counter keeps the total");
        }

        [TestMethod]
        public void Evidence_ARunIsUnbroken_APauseStartsItOver()
        {
            // Two loading screens with another window in between are not one black run.
            var e = new SessionEvidence();
            long t = 50_000;
            for (int i = 0; i <= 180; i++) e.OnGameFrame(black: true, t + i * 100);             // 18 s black
            long back = t + 18_000 + 5_000;                                                      // five seconds away
            for (int i = 0; i <= 120; i++) e.OnGameFrame(black: true, back + i * 100);          // 12 s black
            var s = e.Snapshot();
            Assert.AreEqual(12.0, s.BlackSeconds, 0.001);
            Assert.AreEqual(30.0, s.InFrontSeconds, 0.001);
            Assert.AreEqual(SessionCheckKind.None, Raised(s, Playing), "the whole threshold is needed after coming back");
            for (int i = 121; i <= 300; i++) e.OnGameFrame(black: true, back + i * 100);
            Assert.AreEqual(SessionCheckKind.FullScreen, Raised(e.Snapshot(), Playing));

            // The picture run is counted the same way, and a black frame ends it.
            var p = new SessionEvidence();
            for (int i = 0; i <= 100; i++) p.OnGameFrame(black: false, t + i * 100);
            Assert.AreEqual(10.0, p.Snapshot().PictureSeconds, 0.001);
            p.OnGameFrame(black: true, t + 10_100);
            Assert.AreEqual(0.0, p.Snapshot().PictureSeconds, 0.001);
            p.OnGameFrame(black: false, t + 10_200);
            p.OnGameFrame(black: false, t + 10_200 + 60_000);
            Assert.AreEqual(0.0, p.Snapshot().PictureSeconds, 0.001, "a pause starts the picture run over too");
        }

        [TestMethod]
        public void TheFirstLine_HasItsOwnBalloon_SettledLikeTheChecks()
        {
            int first = GI_Subtitles.Views.SettingsWindow.BalloonBit(StartCardKind.FirstLineWorks);
            var bits = new[] { StartCardKind.SessionFullScreen, StartCardKind.SessionNoMatch, StartCardKind.SessionNoDialogue }
                .Select(GI_Subtitles.Views.SettingsWindow.BalloonBit).Append(first).ToArray();
            Assert.IsTrue(bits.All(b => b != 0) && bits.Distinct().Count() == 4, "one balloon each, none shared");
            foreach (StartCardKind kind in System.Enum.GetValues(typeof(StartCardKind)))
                if (kind != StartCardKind.FirstLineWorks && !kind.ToString().StartsWith("Session"))
                    Assert.AreEqual(0, GI_Subtitles.Views.SettingsWindow.BalloonBit(kind), kind.ToString());
        }

        [TestMethod]
        public void Evidence_ReadCounters_AndTheUnmatchedRun()
        {
            var e = new SessionEvidence();
            e.OnRead(ReadOutcome.Unmatched, "Guten Tag, Reisender.");
            e.OnRead(ReadOutcome.Unmatched, "Guten Tag, Reisender.");          // the same line read again: once
            e.OnRead(ReadOutcome.Unmatched, "Was fuehrt dich hierher?");
            e.OnRead(ReadOutcome.Ignored, "Lv. 90");
            e.OnRead(ReadOutcome.NoText, "");
            Assert.AreEqual(Seen(dialogue: 2, unmatchedRun: 2, picture: false, textReads: 4), e.Snapshot(),
                "the repeat and the HUD number are text too; the empty read is not");

            e.OnRead(ReadOutcome.PackHit, "Adventurer Handbook");
            Assert.AreEqual(Seen(dialogue: 2, packHits: 1, picture: false, textReads: 5), e.Snapshot(), "a pack hit ends the run and is no dialogue read");

            e.OnRead(ReadOutcome.Unmatched, "Was fuehrt dich hierher?");
            e.OnRead(ReadOutcome.Matched, "Hello, Traveler.");
            Assert.AreEqual(Seen(dialogue: 4, matched: 1, packHits: 1, picture: false, textReads: 7), e.Snapshot());

            e.Reset();
            Assert.AreEqual(Seen(picture: false), e.Snapshot());
        }

        [TestMethod]
        public void TwentyForeignLines_RaiseTheLanguageQuestion_ThroughTheRealCounters()
        {
            var e = new SessionEvidence();
            var miss = new SubtitleResolution { Source = ResolutionSource.Matcher, Content = "", Key = "" };
            for (int i = 0; i < 19; i++)
                e.OnRead(SessionEvidence.Classify($"Das ist Zeile Nummer {i}, Reisender.", "Paimon", miss, gameUi: false), $"Das ist Zeile Nummer {i}, Reisender.");
            Assert.AreEqual(SessionCheckKind.None, Raised(e.Snapshot(), Playing));
            e.OnRead(SessionEvidence.Classify("Und noch eine Zeile fuer dich.", "Paimon", miss, gameUi: false), "Und noch eine Zeile fuer dich.");
            Assert.AreEqual(SessionCheckKind.NoMatch, Raised(e.Snapshot(), Playing));
        }

        // ── What a read counts as ───────────────────────────────────────────

        [TestMethod]
        public void Classify_OnlyASpeakerWithASentence_IsALineThePackShouldHave()
        {
            var miss = new SubtitleResolution { Source = ResolutionSource.Matcher, Content = "", Key = "" };
            var headerMiss = new SubtitleResolution { Source = ResolutionSource.MatcherHeaderSeparated, Content = "" };
            Assert.AreEqual(ReadOutcome.Unmatched, SessionEvidence.Classify("Guten Tag, Reisender.", "Paimon", miss, false));
            Assert.AreEqual(ReadOutcome.Ignored, SessionEvidence.Classify("Guten Tag, Reisender.", "", headerMiss, false), "no speaker: could be any text on screen");
            Assert.AreEqual(ReadOutcome.Ignored, SessionEvidence.Classify("Max HP", "Paimon", miss, false), "a label under an accent-coloured word is not speech");
            Assert.AreEqual(ReadOutcome.Ignored, SessionEvidence.Classify("Guten Tag, Reisender.", "Paimon", default, true), "game UI is never looked up");
            Assert.AreEqual(ReadOutcome.NoText, SessionEvidence.Classify("", "", default, false));
            Assert.AreEqual(ReadOutcome.NoText, SessionEvidence.Classify("a", "", default, false));
        }

        [TestMethod]
        public void Classify_MatchesAndPackHits()
        {
            var hit = new SubtitleResolution { Source = ResolutionSource.Matcher, Content = "Witaj, Podróżniku.", Key = "Hello, Traveler." };
            var memoHit = new SubtitleResolution { Source = ResolutionSource.MemoCache, Content = "Witaj, Podróżniku.", Key = "Hello, Traveler." };
            var notDialogue = new SubtitleResolution { Source = ResolutionSource.NotDialogue, Content = "" };
            var memoMiss = new SubtitleResolution { Source = ResolutionSource.MemoCache, Content = "" };
            Assert.AreEqual(ReadOutcome.Matched, SessionEvidence.Classify("Hello, Traveler.", "Paimon", hit, false));
            Assert.AreEqual(ReadOutcome.Matched, SessionEvidence.Classify("Hello, Traveler.", "", memoHit, false));
            Assert.AreEqual(ReadOutcome.PackHit, SessionEvidence.Classify("Open the Adventurer Handbook to view.", "Paimon", notDialogue, false));
            Assert.AreEqual(ReadOutcome.Ignored, SessionEvidence.Classify("Open the Adventurer Handbook to view.", "Paimon", memoMiss, false),
                "a remembered miss was counted when it was first read");
        }

        /// <summary>
        /// The Genshin trap: the evidence gate refuses map, menu and item text on purpose. A long visit to the menus
        /// (pack text refused as not dialogue, labels, numbers, the same panels seen again from the resolver's memo)
        /// must never look like "reads text but finds no line".
        /// </summary>
        [TestMethod]
        public void AGenshinMenuVisit_NeverRaisesTheLanguageQuestion()
        {
            var e = new SessionEvidence();
            var notDialogue = new SubtitleResolution { Source = ResolutionSource.NotDialogue, Content = "" };
            var memoMiss = new SubtitleResolution { Source = ResolutionSource.MemoCache, Content = "" };
            var miss = new SubtitleResolution { Source = ResolutionSource.Matcher, Content = "" };
            var headerMiss = new SubtitleResolution { Source = ResolutionSource.MatcherHeaderSeparated, Content = "" };
            void Read(string text, string speaker, SubtitleResolution r, bool ui = false) =>
                e.OnRead(SessionEvidence.Classify(text, speaker, r, ui), text);

            for (int visit = 0; visit < 30; visit++)
            {
                Read("A sword that once belonged to a wandering knight.", "Favonius Sword", notDialogue);   // item text, gold name
                Read("Weapons Artifacts Character Development Items Food Materials", "", headerMiss);        // merged tab labels
                Read($"Mora {visit},345,678", "", default, ui: true);                                        // numbers
                Read("Enhance", "Lock", miss);                                                               // a button under a gold word
                Read("A sword that once belonged to a wandering knight.", "Favonius Sword", memoMiss);       // the same panel again
                Read($"Obtained from the event shop of version {visit}.", "", headerMiss);                   // a sentence, no speaker
            }
            var s = e.Snapshot();
            Assert.AreEqual(0, s.UnmatchedRun);
            Assert.AreEqual(0, s.DialogueReads);
            Assert.AreEqual(30, s.PackHits);
            Assert.AreEqual(180, s.TextReads);
            Assert.AreEqual(SessionCheckKind.None, Raised(s with { InFrontSeconds = 900 }, Playing));
        }

        // ── Black frames ────────────────────────────────────────────────────

        [TestMethod]
        public void FrameBlackness_ABlockedCaptureIsBlack_AnyPictureIsNot()
        {
            using var black = new Mat(255, 1650, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));   // opaque alpha is not picture
            Assert.IsTrue(FrameBlackness.IsBlack(black));
            using var dithered = new Mat(255, 1650, MatType.CV_8UC3, Scalar.All(FrameBlackness.BlackLevel));
            Assert.IsTrue(FrameBlackness.IsBlack(dithered));
            using var night = new Mat(255, 1650, MatType.CV_8UC4, new Scalar(14, 12, 10, 255));   // a dark scene is still a picture
            Assert.IsFalse(FrameBlackness.IsBlack(night));
            using var dialogue = new Mat(255, 1650, MatType.CV_8UC3, Scalar.Black);
            Cv2.Rectangle(dialogue, new Rect(200, 60, 1200, 120), Scalar.All(230), -1);   // a dialogue box on a black screen
            Assert.IsFalse(FrameBlackness.IsBlack(dialogue));
            using var tiny = new Mat(3, 5, MatType.CV_8UC3, Scalar.Black);
            Assert.IsTrue(FrameBlackness.IsBlack(tiny));
            using var empty = new Mat();
            Assert.IsTrue(FrameBlackness.IsBlack(empty), "no frame at all is no picture");
            Assert.IsTrue(FrameBlackness.IsBlack(null));
        }

        // ── Strings ─────────────────────────────────────────────────────────

        private static readonly string[] NewKeys =
        {
            "shell_Card_FullScreen", "shell_Card_FullScreen_Detail", "shell_Card_NoMatch", "shell_Card_NoMatch_Detail",
            "shell_Card_NoDialogue", "shell_Card_NoDialogue_Detail", "shell_Card_FirstLine", "shell_Card_FirstLine_Detail",
            "shell_Action_ShowHow", "shell_Action_LanguageGuide", "shell_Action_CheckArea",
        };

        /// <summary>L("key") resolves at runtime, so the build's XAML check does not see a missing key: this does.</summary>
        [TestMethod]
        public void EverySentence_IsInBothDictionaries_AndSpeaksToPlayers()
        {
            string root = RenderTestKit.RepoRoot();
            if (root == null) Assert.Inconclusive("Repository root not found.");
            foreach (string lang in new[] { "en-US", "pl-PL" })
            {
                string xaml = File.ReadAllText(Path.Combine(root, "GI-Subtitles", "Resources", $"Strings.{lang}.xaml"));
                foreach (string key in NewKeys)
                {
                    var m = Regex.Match(xaml, "x:Key=\"" + Regex.Escape(key) + "\"[^>]*>([^<]*)<");
                    Assert.IsTrue(m.Success, $"{lang}: {key} is missing");
                    string text = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(text), $"{lang}: {key} is empty");
                    Assert.IsFalse(Regex.IsMatch(text, @"\bOCR\b", RegexOptions.IgnoreCase), $"{lang}: {key} says OCR");
                    // A tray balloon cuts its title at 63 characters and its text at 255.
                    Assert.IsTrue(text.Length <= (key.EndsWith("_Detail") ? 255 : 63), $"{lang}: {key} is too long for a balloon ({text.Length})");
                }
                foreach (string key in new[] { "onb_Rail_Tagline", "onb_Prepare_Index", "onb_Prepare_Index_Later", "onb_Prepare_Index_Done", "onb_Done_Index", "onb_Done_IndexManual" })
                {
                    var m = Regex.Match(xaml, "x:Key=\"" + Regex.Escape(key) + "\"[^>]*>([^<]*)<");
                    Assert.IsTrue(m.Success, $"{lang}: {key} is missing");
                    Assert.IsFalse(Regex.IsMatch(m.Groups[1].Value, "HoYoverse|index|indeks", RegexOptions.IgnoreCase),
                        $"{lang}: {key} names a publisher or says \"index\": {m.Groups[1].Value}");
                }
            }
        }
    }
}
