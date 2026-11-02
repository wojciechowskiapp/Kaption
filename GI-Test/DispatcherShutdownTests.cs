// ─────────────────────────────────────────────────────────────────────────────
//  DispatcherShutdownTests.cs
//  ---------------------------------------------------------------------------
//  GlitchTip 7891420: "DialogueContextEngine load failed: A task was canceled."
//  The user closed Kaption while the dialogue engine was still preparing, and
//  WPF aborted the background thread's Dispatcher.Invoke. These tests pin that
//  behaviour on a real dispatcher, and the predicate SettingsWindow uses to log
//  it as Info instead of sending it to GlitchTip.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using GI_Subtitles.Core.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    [TestClass]
    public class DispatcherShutdownTests
    {
        [TestMethod]
        public void IsShutdownCancellation_CancellationDuringShutdown_IsExpected()
        {
            Assert.IsTrue(DispatcherShutdown.IsShutdownCancellation(new TaskCanceledException(), hasShutdownStarted: true));
            Assert.IsTrue(DispatcherShutdown.IsShutdownCancellation(new OperationCanceledException(), hasShutdownStarted: true));
        }

        [TestMethod]
        public void IsShutdownCancellation_CancellationWhileRunning_IsARealFailure()
        {
            Assert.IsFalse(DispatcherShutdown.IsShutdownCancellation(new TaskCanceledException(), hasShutdownStarted: false));
        }

        [TestMethod]
        public void IsShutdownCancellation_OtherErrorsDuringShutdown_AreRealFailures()
        {
            Assert.IsFalse(DispatcherShutdown.IsShutdownCancellation(new InvalidOperationException(), hasShutdownStarted: true));
            Assert.IsFalse(DispatcherShutdown.IsShutdownCancellation(null, hasShutdownStarted: true));
        }

        [TestMethod]
        public void Invoke_AfterShutdown_ThrowsTheCancellationTheGuardRecognises()
        {
            Dispatcher dispatcher = StartThenShutDownDispatcher();

            Exception invoke = Capture(() => dispatcher.Invoke(() => { }));
            Exception invokeAsync = Capture(() => dispatcher.InvokeAsync(() => { }).Task.GetAwaiter().GetResult());

            Assert.IsInstanceOfType(invoke, typeof(TaskCanceledException), "Dispatcher.Invoke after shutdown");
            Assert.IsInstanceOfType(invokeAsync, typeof(TaskCanceledException), "awaited Dispatcher.InvokeAsync after shutdown");
            Assert.IsTrue(DispatcherShutdown.IsShutdownCancellation(invoke, dispatcher.HasShutdownStarted));
            Assert.IsTrue(DispatcherShutdown.IsShutdownCancellation(invokeAsync, dispatcher.HasShutdownStarted));
        }

        [TestMethod]
        public void BeginInvoke_AfterShutdown_IsDroppedWithoutThrowing()
        {
            Dispatcher dispatcher = StartThenShutDownDispatcher();

            DispatcherOperation op = dispatcher.BeginInvoke(new Action(() => { }));

            Assert.AreEqual(DispatcherOperationStatus.Aborted, op.Status);
        }

        private static Dispatcher StartThenShutDownDispatcher()
        {
            Dispatcher dispatcher = null;
            using (var ready = new ManualResetEventSlim())
            {
                var thread = new Thread(() =>
                {
                    dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                ready.Wait();

                dispatcher.InvokeShutdown();
                Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "Dispatcher thread did not exit after shutdown.");
            }
            Assert.IsTrue(dispatcher.HasShutdownStarted);
            return dispatcher;
        }

        private static Exception Capture(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }
}
