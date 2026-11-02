using System;
using System.Collections.Generic;
using GI_Subtitles.Services.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// <see cref="AutoStartController"/> against a fake host and a fake clock: when it may call the gated start,
    /// what it stops, and what it reports. No WPF and no real waiting.
    /// </summary>
    [TestClass]
    public class AutoStartControllerTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

        private FakeHost _host;
        private FakeClock _clock;

        [TestInitialize]
        public void SetUp()
        {
            _host = new FakeHost();
            _clock = new FakeClock { Now = T0 };
        }

        private AutoStartController Create(bool enabled = true)
            => new AutoStartController(_host, enabled, () => _clock.Now);

        /// <summary>Game in front, every gate looked open, the start returned false at T0. The next start would succeed.</summary>
        private AutoStartController CreateAfterFailedStart()
        {
            AutoStartController controller = Create();
            _host.StartResult = false;
            controller.OnGameForeground(true);
            Assert.AreEqual(1, _host.StartCalls, "Setup: the first attempt should have been made.");
            Assert.AreEqual(AutoStartState.Blocked, controller.State, "Setup: a failed start blocks.");
            _host.StartResult = true;
            return controller;
        }

        /// <summary>Game in front with every gate open: the controller started translation and owns it.</summary>
        private AutoStartController CreateTranslating()
        {
            AutoStartController controller = Create();
            controller.OnGameForeground(true);
            Assert.IsTrue(controller.StartedByController, "Setup: the controller should own the session.");
            return controller;
        }

        // ── Construction (rule 15) ──────────────────────────────────────────

        [TestMethod]
        public void Constructor_Enabled_WaitsForTheGame_WithoutStarting()
        {
            AutoStartController controller = Create();

            Assert.AreEqual(AutoStartState.WaitingForGame, controller.State);
            Assert.AreEqual(StartGate.None, controller.BlockingGate);
            Assert.IsFalse(controller.StartedByController);
            Assert.IsFalse(controller.IsGameForeground);
            Assert.IsFalse(controller.IsGameRunning);
            Assert.AreEqual(0, _host.StartCalls, "Construction must never start translation.");
        }

        [TestMethod]
        public void Constructor_SomeoneAlreadyTranslating_ReportsTranslating_WithoutOwningIt()
        {
            _host.Running = true;

            AutoStartController controller = Create();

            Assert.AreEqual(AutoStartState.Translating, controller.State);
            Assert.IsFalse(controller.StartedByController, "A session that was already running is not the controller's to stop.");
            Assert.AreEqual(0, _host.StartCalls);
        }

        [TestMethod]
        public void Constructor_NullHost_Throws()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => { _ = new AutoStartController(null, enabled: true); });
        }

        // ── Disabled (rule 1) ───────────────────────────────────────────────

        [TestMethod]
        public void Disabled_NeverStartsOrStops_WhateverTheWatchersReport()
        {
            AutoStartController controller = Create(enabled: false);
            int changes = 0;
            controller.StateChanged += (_, _) => changes++;

            controller.OnGameRunningChanged(true);
            controller.OnGameForeground(true);
            controller.OnGatesChanged();
            _host.Running = true;
            controller.OnUserStarted();
            controller.OnGameRunningChanged(false);
            _host.Running = false;
            controller.OnUserStopped();
            controller.OnTranslationStoppedExternally();

            Assert.AreEqual(0, _host.StartCalls);
            Assert.AreEqual(0, _host.StopCalls, "Disabled auto-start must not stop a session when the game exits.");
            Assert.AreEqual(AutoStartState.Disabled, controller.State);
            Assert.AreEqual(StartGate.None, controller.BlockingGate);
            Assert.AreEqual(0, changes, "Nothing moves a disabled controller out of Disabled.");
        }

        // ── Rule 2: enabled, not paused, nothing running ───────────────────

        [TestMethod]
        public void GameForeground_GatesOpen_StartsOnce()
        {
            AutoStartController controller = Create();

            controller.OnGameForeground(true);

            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Translating, controller.State);
            Assert.AreEqual(StartGate.None, controller.BlockingGate);
            Assert.IsTrue(controller.StartedByController);
            Assert.IsTrue(controller.IsGameForeground);
        }

        [TestMethod]
        [DataRow(StartGate.Session)]
        [DataRow(StartGate.InitialSync)]
        [DataRow(StartGate.Engine)]
        [DataRow(StartGate.Matcher)]
        [DataRow(StartGate.Region)]
        [DataRow(StartGate.GameRunning)]
        [DataRow(StartGate.DisplayModeTip)]
        [DataRow(StartGate.OverlayOverlap)]
        public void GameForeground_GateClosed_BlocksOnThatGate_WithoutStarting(StartGate gate)
        {
            _host.Gate = gate;
            AutoStartController controller = Create();

            controller.OnGameForeground(true);

            Assert.AreEqual(AutoStartState.Blocked, controller.State);
            Assert.AreEqual(gate, controller.BlockingGate);
            Assert.AreEqual(0, _host.StartCalls, "A closed gate must never reach the start path, which would show its dialog.");
        }

        [TestMethod]
        public void GameForeground_StartFails_BlocksOnTheGateReReadAfterTheAttempt()
        {
            AutoStartController controller = Create();
            _host.StartResult = false;
            _host.DuringNextStart = () => _host.Gate = StartGate.OverlayOverlap;   // the real path saw what the silent check missed

            controller.OnGameForeground(true);

            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Blocked, controller.State);
            Assert.AreEqual(StartGate.OverlayOverlap, controller.BlockingGate);
            Assert.IsFalse(controller.StartedByController);
        }

        [TestMethod]
        public void GameForeground_StartFails_HostCannotSayWhy_StaysBlockedWithNone()
        {
            AutoStartController controller = CreateAfterFailedStart();

            Assert.AreEqual(AutoStartState.Blocked, controller.State);
            Assert.AreEqual(StartGate.None, controller.BlockingGate);
            Assert.IsFalse(controller.StartedByController);
        }

        [TestMethod]
        public void GatesChanged_WhileGameNotInForeground_WaitsAndNeverStarts()
        {
            _host.Gate = StartGate.InitialSync;
            AutoStartController controller = Create();
            controller.OnGameRunningChanged(true);
            int changes = 0;
            controller.StateChanged += (_, _) => changes++;

            _host.Gate = StartGate.None;
            controller.OnGatesChanged();

            Assert.AreEqual(AutoStartState.WaitingForGame, controller.State);
            Assert.AreEqual(0, _host.StartCalls, "Open gates are not enough: the game window must be in the foreground.");
            Assert.AreEqual(0, changes);
        }

        [TestMethod]
        public void LosingForeground_WhileBlocked_GoesBackToWaiting()
        {
            _host.Gate = StartGate.Region;
            AutoStartController controller = Create();
            controller.OnGameForeground(true);

            controller.OnGameForeground(false);

            Assert.AreEqual(AutoStartState.WaitingForGame, controller.State);
            Assert.AreEqual(StartGate.None, controller.BlockingGate);
        }

        // ── Rules 3 and 4: one start per edge, retry cooldown ───────────────

        [TestMethod]
        public void RepeatedForegroundTrue_IsNotANewEdge_EvenAfterTheCooldown()
        {
            AutoStartController controller = CreateAfterFailedStart();
            _clock.Advance(TimeSpan.FromMinutes(1));

            controller.OnGameForeground(true);
            controller.OnGameForeground(true);
            Assert.AreEqual(1, _host.StartCalls, "A watcher repeating 'foreground' is not a new edge, and time alone is not a trigger.");

            controller.OnGameForeground(false);
            controller.OnGameForeground(true);
            Assert.AreEqual(2, _host.StartCalls, "A real new edge after the cooldown does retry.");
        }

        [TestMethod]
        public void FailedStart_TriggersInsideTheCooldown_DoNotRetry()
        {
            AutoStartController controller = CreateAfterFailedStart();
            _clock.Advance(TimeSpan.FromSeconds(4));

            controller.OnGatesChanged();
            controller.OnGameForeground(false);
            controller.OnGameForeground(true);

            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Blocked, controller.State);
        }

        [TestMethod]
        public void FailedStart_InsideTheCooldown_StillReportsWhichGateIsClosed()
        {
            AutoStartController controller = CreateAfterFailedStart();

            _host.Gate = StartGate.Engine;
            controller.OnGatesChanged();
            Assert.AreEqual(StartGate.Engine, controller.BlockingGate);

            _host.Gate = StartGate.None;
            controller.OnGatesChanged();
            Assert.AreEqual(AutoStartState.Blocked, controller.State);
            Assert.AreEqual(StartGate.None, controller.BlockingGate);
            Assert.AreEqual(1, _host.StartCalls);
        }

        [TestMethod]
        public void FailedStart_RetriesOnATriggerAtExactlyTheCooldown_NotATickBefore()
        {
            AutoStartController controller = CreateAfterFailedStart();

            _clock.Advance(Cooldown - TimeSpan.FromTicks(1));
            controller.OnGatesChanged();
            Assert.AreEqual(1, _host.StartCalls, "One tick short of 5 s is still cooling down.");

            _clock.Advance(TimeSpan.FromTicks(1));
            controller.OnGatesChanged();
            Assert.AreEqual(2, _host.StartCalls, "At exactly 5 s the cooldown has elapsed.");
            Assert.AreEqual(AutoStartState.Translating, controller.State);
        }

        [TestMethod]
        public void FailedStart_CooldownCountsFromWhenTheStartReturned()
        {
            AutoStartController controller = Create();
            _host.StartResult = false;
            _host.DuringNextStart = () => _clock.Advance(TimeSpan.FromSeconds(30));   // a dialog left open for 30 s

            controller.OnGameForeground(true);
            _host.StartResult = true;
            controller.OnGatesChanged();
            Assert.AreEqual(1, _host.StartCalls, "Time spent inside the failed start must not use up the cooldown.");

            _clock.Advance(Cooldown);
            controller.OnGatesChanged();
            Assert.AreEqual(2, _host.StartCalls);
        }

        [TestMethod]
        public void BlockedByGate_GatesChanged_StartsImmediately_NoCooldown()
        {
            _host.Gate = StartGate.Matcher;
            AutoStartController controller = Create();
            controller.OnGameForeground(true);

            _host.Gate = StartGate.None;
            controller.OnGatesChanged();

            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Translating, controller.State);
        }

        [TestMethod]
        public void BlockedByGate_NewForegroundEdge_StartsImmediately_NoCooldown()
        {
            _host.Gate = StartGate.Region;
            AutoStartController controller = Create();
            controller.OnGameForeground(true);
            controller.OnGameForeground(false);

            _host.Gate = StartGate.None;
            controller.OnGameForeground(true);

            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Translating, controller.State);
        }

        // ── Rule 6: losing the foreground ───────────────────────────────────

        [TestMethod]
        public void LosingForeground_WhileTranslating_DoesNotStop()
        {
            AutoStartController controller = CreateTranslating();

            controller.OnGameForeground(false);

            Assert.AreEqual(0, _host.StopCalls);
            Assert.AreEqual(AutoStartState.Translating, controller.State);
            Assert.IsTrue(controller.StartedByController);
            Assert.IsFalse(controller.IsGameForeground);
        }

        // ── Rule 7: game exit ───────────────────────────────────────────────

        [TestMethod]
        public void GameExit_AfterAutoStart_StopsOnce_AndWaitsForTheGame()
        {
            AutoStartController controller = CreateTranslating();

            controller.OnGameRunningChanged(false);
            controller.OnGameRunningChanged(false);

            Assert.AreEqual(1, _host.StopCalls);
            Assert.IsFalse(controller.StartedByController);
            Assert.AreEqual(AutoStartState.WaitingForGame, controller.State);
            Assert.IsFalse(controller.IsGameForeground);
            Assert.IsFalse(controller.IsGameRunning);
        }

        [TestMethod]
        public void GameExit_ManualSession_IsNotStopped()
        {
            AutoStartController controller = Create();
            _host.Running = true;
            controller.OnUserStarted();
            controller.OnGameForeground(true);

            controller.OnGameRunningChanged(false);

            Assert.AreEqual(0, _host.StopCalls, "Only a session the controller started is the controller's to stop.");
            Assert.AreEqual(AutoStartState.Translating, controller.State);
            Assert.IsFalse(controller.IsGameForeground);
        }

        // ── Rules 8 and 9: user stop and user start ─────────────────────────

        [TestMethod]
        public void UserStop_PausesAutoStart_ForTheRestOfTheSession()
        {
            AutoStartController controller = CreateTranslating();

            _host.Running = false;
            controller.OnUserStopped();
            Assert.AreEqual(AutoStartState.PausedByUser, controller.State);
            Assert.IsFalse(controller.StartedByController);

            controller.OnGameForeground(false);
            controller.OnGameForeground(true);
            controller.OnGatesChanged();
            controller.OnGameRunningChanged(false);
            controller.OnGameRunningChanged(true);
            controller.OnGameForeground(true);
            controller.SetEnabled(false);
            controller.SetEnabled(true);
            _clock.Advance(TimeSpan.FromHours(1));
            controller.OnGatesChanged();

            Assert.AreEqual(1, _host.StartCalls, "Only a manual start may end the pause.");
            Assert.AreEqual(AutoStartState.PausedByUser, controller.State);
        }

        [TestMethod]
        public void UserStart_EndsThePause()
        {
            AutoStartController controller = Create();
            controller.OnUserStopped();

            _host.Running = true;
            controller.OnUserStarted();
            Assert.AreEqual(AutoStartState.Translating, controller.State);
            Assert.IsFalse(controller.StartedByController, "A manual session is never the controller's.");

            // The app later ends that session; with the pause gone, a trigger after the cooldown auto-starts.
            _host.Running = false;
            controller.OnTranslationStoppedExternally();
            _clock.Advance(Cooldown);
            controller.OnGameForeground(true);
            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Translating, controller.State);
        }

        // ── Rule 10: switching auto-start on and off ────────────────────────

        [TestMethod]
        public void Disable_WhileTranslating_DoesNotStop_AndGivesUpOwnership()
        {
            AutoStartController controller = CreateTranslating();

            controller.SetEnabled(false);
            Assert.AreEqual(0, _host.StopCalls);
            Assert.AreEqual(AutoStartState.Disabled, controller.State);
            Assert.IsFalse(controller.StartedByController);

            controller.SetEnabled(true);
            controller.OnGameRunningChanged(false);
            Assert.AreEqual(0, _host.StopCalls, "Re-enabling does not take back a session it gave up.");
            Assert.AreEqual(AutoStartState.Translating, controller.State);
        }

        [TestMethod]
        public void Enable_StartsAtOnce_OnlyIfTheGameIsInFront()
        {
            AutoStartController controller = Create(enabled: false);
            controller.SetEnabled(true);
            Assert.AreEqual(AutoStartState.WaitingForGame, controller.State);
            Assert.AreEqual(0, _host.StartCalls);

            controller.SetEnabled(false);
            controller.OnGameForeground(true);
            Assert.AreEqual(0, _host.StartCalls);

            controller.SetEnabled(true);
            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Translating, controller.State);
            Assert.IsTrue(controller.StartedByController);
        }

        [TestMethod]
        public void Enable_SoonAfterAFailedStart_StillWaitsOutTheCooldown()
        {
            AutoStartController controller = CreateAfterFailedStart();

            controller.SetEnabled(false);
            controller.SetEnabled(true);
            Assert.AreEqual(1, _host.StartCalls, "Toggling the setting is not a way around the retry cooldown.");

            _clock.Advance(Cooldown);
            controller.SetEnabled(false);
            controller.SetEnabled(true);
            Assert.AreEqual(2, _host.StartCalls);
        }

        // ── Rule 11: the app stopped translation itself ─────────────────────

        [TestMethod]
        public void ExternalStop_IsNotRestartedImmediately_OnlyByATriggerAfterTheCooldown()
        {
            AutoStartController controller = CreateTranslating();
            _host.Running = false;
            _host.Gate = StartGate.Region;

            controller.OnTranslationStoppedExternally();
            Assert.IsFalse(controller.StartedByController);
            Assert.AreEqual(AutoStartState.Blocked, controller.State);
            Assert.AreEqual(StartGate.Region, controller.BlockingGate);

            _host.Gate = StartGate.None;
            controller.OnGatesChanged();
            Assert.AreEqual(1, _host.StartCalls, "The region came back inside the cooldown: no restart yet.");

            _clock.Advance(Cooldown);
            controller.OnGatesChanged();
            Assert.AreEqual(2, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Translating, controller.State);
        }

        // ── Rule 12: someone else is translating ────────────────────────────

        [TestMethod]
        public void SomeoneElseIsTranslating_ReportsTranslating_AndNeverStartsOrStopsIt()
        {
            AutoStartController controller = Create();
            _host.Running = true;

            controller.OnGameForeground(true);
            controller.OnGatesChanged();
            Assert.AreEqual(AutoStartState.Translating, controller.State);
            Assert.AreEqual(0, _host.StartCalls);
            Assert.IsFalse(controller.StartedByController);

            controller.OnGameRunningChanged(false);
            Assert.AreEqual(0, _host.StopCalls);
        }

        // ── Rule 13: callbacks from inside host calls ───────────────────────

        [TestMethod]
        public void FocusBouncingDuringAFailedStart_DoesNotStartTwice()
        {
            AutoStartController controller = Create();
            _host.StartResult = false;
            _host.DuringNextStart = () =>
            {
                // The start path's dialog takes focus, the user closes it, focus returns to the game.
                controller.OnGameForeground(false);
                controller.OnGameForeground(true);
                controller.OnGatesChanged();
            };

            controller.OnGameForeground(true);

            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Blocked, controller.State);
            Assert.IsTrue(controller.IsGameForeground);
        }

        [TestMethod]
        public void CallbacksDuringASuccessfulStart_DoNotStartTwice()
        {
            AutoStartController controller = Create();
            _host.DuringNextStart = () =>
            {
                controller.OnGatesChanged();
                controller.OnGameForeground(false);
                controller.OnGameForeground(true);
                controller.OnGameRunningChanged(true);
            };

            controller.OnGameForeground(true);

            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Translating, controller.State);
            Assert.IsTrue(controller.StartedByController);
            Assert.IsTrue(controller.IsGameRunning, "Watcher edges raised during the call are applied after it.");
        }

        [TestMethod]
        public void GateCheckThatReportsAChange_DoesNotLeadToASecondStart()
        {
            AutoStartController controller = Create();
            _host.DuringNextGateCheck = controller.OnGatesChanged;

            controller.OnGameForeground(true);

            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Translating, controller.State);
        }

        [TestMethod]
        public void GameExitReportedDuringTheStart_StopsTheNewSessionOnceTheStartReturns()
        {
            AutoStartController controller = Create();
            _host.DuringNextStart = () => controller.OnGameRunningChanged(false);

            controller.OnGameForeground(true);

            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(1, _host.StopCalls, "The session it just started belongs to a game that is gone.");
            Assert.AreEqual(AutoStartState.WaitingForGame, controller.State);
            Assert.IsFalse(controller.StartedByController);
            Assert.IsFalse(controller.IsGameForeground);
        }

        [TestMethod]
        public void HostEchoingTheControllersOwnStartAndStop_IsNotMistakenForTheUser()
        {
            AutoStartController controller = Create();
            _host.DuringNextStart = controller.OnUserStarted;
            controller.OnGameForeground(true);
            Assert.IsTrue(controller.StartedByController, "An echo of its own start must not turn the session into a manual one.");

            _host.DuringNextStop = () =>
            {
                controller.OnUserStopped();
                controller.OnTranslationStoppedExternally();
            };
            controller.OnGameRunningChanged(false);
            Assert.AreEqual(1, _host.StopCalls);
            Assert.AreEqual(AutoStartState.WaitingForGame, controller.State, "An echo of its own stop is not a user pause.");

            controller.OnGameRunningChanged(true);
            controller.OnGameForeground(true);
            Assert.AreEqual(2, _host.StartCalls, "Nor an external stop with a cooldown: the relaunched game starts at once.");
        }

        // ── Rule 14: StateChanged ───────────────────────────────────────────

        [TestMethod]
        public void StateChanged_FiresOncePerRealChange_AndNeverForNoOps()
        {
            _host.Gate = StartGate.InitialSync;
            AutoStartController controller = Create();
            var seen = new List<(AutoStartState, StartGate)>();
            controller.StateChanged += (sender, _) =>
            {
                Assert.AreSame(controller, sender);
                seen.Add((controller.State, controller.BlockingGate));
            };

            controller.OnGatesChanged();           // still waiting
            controller.OnGameForeground(false);    // already in the background
            controller.SetEnabled(true);           // already enabled
            controller.OnGameForeground(true);     // -> Blocked(InitialSync)
            controller.OnGameForeground(true);     // repeat
            controller.OnGatesChanged();           // same gate
            _host.Gate = StartGate.Matcher;
            controller.OnGatesChanged();           // only the gate changed
            _host.Gate = StartGate.None;
            controller.OnGatesChanged();           // -> Translating
            controller.OnGatesChanged();           // still translating
            controller.OnGameForeground(false);    // still translating

            CollectionAssert.AreEqual(
                new[]
                {
                    (AutoStartState.Blocked, StartGate.InitialSync),
                    (AutoStartState.Blocked, StartGate.Matcher),
                    (AutoStartState.Translating, StartGate.None),
                },
                seen);
        }

        // ── Full scenario ───────────────────────────────────────────────────

        [TestMethod]
        public void FullSession_PackDownload_ThenTranslation_ThenGameExit()
        {
            AutoStartController controller = Create();
            var seen = new List<AutoStartState>();
            controller.StateChanged += (_, _) => seen.Add(controller.State);
            Assert.AreEqual(AutoStartState.WaitingForGame, controller.State);

            _host.Gate = StartGate.InitialSync;   // the translation pack is still downloading
            controller.OnGameForeground(true);
            Assert.AreEqual(AutoStartState.Blocked, controller.State);
            Assert.AreEqual(StartGate.InitialSync, controller.BlockingGate);
            Assert.AreEqual(0, _host.StartCalls);

            _host.Gate = StartGate.None;          // download finished
            controller.OnGatesChanged();
            Assert.AreEqual(AutoStartState.Translating, controller.State);
            Assert.AreEqual(1, _host.StartCalls);
            Assert.IsTrue(controller.StartedByController);

            controller.OnGameRunningChanged(false);
            Assert.AreEqual(1, _host.StopCalls);
            Assert.AreEqual(AutoStartState.WaitingForGame, controller.State);
            Assert.IsFalse(controller.StartedByController);

            CollectionAssert.AreEqual(
                new[] { AutoStartState.Blocked, AutoStartState.Translating, AutoStartState.WaitingForGame }, seen);
        }

        // ── Watcher order, host failures, clocks ────────────────────────────

        [TestMethod]
        public void GameProcessAppearing_AfterTheWindowCameToFront_Starts()
        {
            // At launch the foreground watcher can beat the process watcher.
            _host.Gate = StartGate.GameRunning;
            AutoStartController controller = Create();
            controller.OnGameForeground(true);
            Assert.AreEqual(StartGate.GameRunning, controller.BlockingGate);

            _host.Gate = StartGate.None;
            controller.OnGameRunningChanged(true);

            Assert.AreEqual(1, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Translating, controller.State);
            Assert.IsTrue(controller.IsGameRunning);
        }

        [TestMethod]
        public void RepeatedGameRunningReports_AreNotTriggers()
        {
            AutoStartController controller = Create();
            controller.OnGameRunningChanged(true);
            _host.StartResult = false;
            controller.OnGameForeground(true);
            _host.StartResult = true;
            _clock.Advance(Cooldown);

            controller.OnGameRunningChanged(true);

            Assert.AreEqual(1, _host.StartCalls, "A process watcher repeating itself must not retry every poll.");
        }

        [TestMethod]
        public void StartThatThrows_CountsAsAFailedAttempt_AndLeavesTheControllerUsable()
        {
            AutoStartController controller = Create();
            _host.DuringNextStart = () => throw new InvalidOperationException("start path crashed");

            Assert.ThrowsExactly<InvalidOperationException>(() => controller.OnGameForeground(true));

            controller.OnGatesChanged();
            Assert.AreEqual(1, _host.StartCalls, "A crashing start is held back by the cooldown like a failed one.");

            _clock.Advance(Cooldown);
            controller.OnGatesChanged();
            Assert.AreEqual(2, _host.StartCalls);
            Assert.AreEqual(AutoStartState.Translating, controller.State);
        }

        [TestMethod]
        public void ClockSteppingBack_DoesNotStretchTheCooldown()
        {
            AutoStartController controller = CreateAfterFailedStart();
            _clock.Now = T0 - TimeSpan.FromHours(1);   // e.g. a time sync correction

            controller.OnGatesChanged();
            Assert.AreEqual(1, _host.StartCalls);

            _clock.Advance(Cooldown);
            controller.OnGatesChanged();
            Assert.AreEqual(2, _host.StartCalls, "After a backwards step the wait is one cooldown, not the size of the step.");
        }

        [TestMethod]
        public void WithoutAClock_TheWallClockDrivesTheCooldown()
        {
            _host.StartResult = false;
            var controller = new AutoStartController(_host, enabled: true);
            controller.OnGameForeground(true);
            _host.StartResult = true;

            controller.OnGatesChanged();

            Assert.AreEqual(1, _host.StartCalls, "Right after a failure the real clock is still inside the cooldown.");
        }

        // ── Fakes ───────────────────────────────────────────────────────────

        private sealed class FakeHost : IAutoStartHost
        {
            // One-shot hooks that run inside the matching host call (re-entrant callbacks, a dialog, a crash).
            public Action DuringNextGateCheck;
            public Action DuringNextStart;
            public Action DuringNextStop;

            public bool Running { get; set; }
            public StartGate Gate { get; set; }
            public bool StartResult { get; set; } = true;
            public int StartCalls { get; private set; }
            public int StopCalls { get; private set; }

            public bool IsTranslationRunning => Running;

            public StartGate FirstClosedGate()
            {
                Fire(ref DuringNextGateCheck);
                return Gate;
            }

            public bool StartThroughGates()
            {
                StartCalls++;
                Fire(ref DuringNextStart);
                Running = StartResult;
                return StartResult;
            }

            public void StopTranslation()
            {
                StopCalls++;
                Running = false;
                Fire(ref DuringNextStop);
            }

            private static void Fire(ref Action hook)
            {
                Action once = hook;
                hook = null;
                once?.Invoke();
            }
        }

        private sealed class FakeClock
        {
            public DateTimeOffset Now { get; set; }

            public void Advance(TimeSpan by) => Now += by;
        }
    }
}
