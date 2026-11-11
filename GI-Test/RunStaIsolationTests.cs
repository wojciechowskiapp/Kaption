using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static GI_Test.RenderTestKit;
using MachineUiLock = GI_Test.RenderTestKit.MachineUiLock;

namespace GI_Test
{
    /// <summary>
    /// Keys, text and clicks from the desktop never reach a window a UI test shows. The input is injected through WPF's
    /// InputManager, never SendInput, so nothing goes to whatever app the person at the desktop is using.
    /// </summary>
    [TestClass]
    public class RunStaDesktopInputTests
    {
        [TestMethod]
        public void KeysTextAndClicksFromTheDesktop_NeverReachATestWindow()
        {
            RunSta(() =>
            {
                var box = new TextBox { Text = "Linia A." };
                // Off screen and never activated: this test must not take the focus from anyone at the desktop.
                var window = new Window
                {
                    Content = box, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 200, Height = 60,
                };
                var arrived = new List<string>();
                box.PreviewTextInput += (s, e) => arrived.Add("text " + e.Text);
                box.PreviewKeyDown += (s, e) => arrived.Add("key " + e.Key);
                box.PreviewMouseDown += (s, e) => arrived.Add("click " + e.ChangedButton);
                window.Show();
                try
                {
                    box.CaretIndex = box.Text.Length;
                    Desktop(box);
                    Assert.AreEqual(0, arrived.Count, "reached the box: " + string.Join(", ", arrived));
                    Assert.AreEqual("Linia A.", box.Text);

                    // The control: without the filter the same input arrives, so the check above means something.
                    InputManager.Current.PreProcessInput -= DropDesktopInput;
                    try { Desktop(box); }
                    finally { InputManager.Current.PreProcessInput += DropDesktopInput; }
                    CollectionAssert.AreEqual(new[] { "text /", "key Back", "click Left" }, arrived);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        // A typed "/", a Backspace and a left click, as the desktop delivers them to the window under the input.
        private static void Desktop(TextBox box)
        {
            var input = InputManager.Current;
            input.ProcessInput(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(input, box, "/"))
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent, Source = box });
            input.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), Environment.TickCount, Key.Back)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = box });
            input.ProcessInput(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = box });
            DoEvents();
        }
    }

    /// <summary>
    /// The machine-wide lock around WPF UI tests. A mutex belongs to a thread, so a second thread here contends the way
    /// a second test process does. Each test uses its own name, never the real lock.
    /// </summary>
    [TestClass]
    public class MachineUiLockTests
    {
        private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

        private static string UniqueName() => @"Global\KaptionGiTestLockTest-" + Guid.NewGuid().ToString("N");

        [TestMethod]
        public void ASecondHolder_WaitsUntilTheFirstReleases()
        {
            string name = UniqueName();
            var first = MachineUiLock.Acquire(name, Generous);
            long secondGotItAt = -1;
            var clock = Stopwatch.StartNew();
            var other = new Thread(() =>
            {
                using (MachineUiLock.Acquire(name, Generous)) secondGotItAt = clock.ElapsedMilliseconds;
            });
            other.Start();
            Thread.Sleep(300);
            Assert.AreEqual(-1, Interlocked.Read(ref secondGotItAt), "the second holder waits while the first holds the lock");
            long releasedAt = clock.ElapsedMilliseconds;
            first.Dispose();
            Assert.IsTrue(other.Join(Generous), "the second holder gets the lock once it is released");
            Assert.IsTrue(secondGotItAt >= releasedAt, $"got it at {secondGotItAt} ms, released at {releasedAt} ms");
        }

        [TestMethod]
        public void AWaitThatRunsOut_FailsAndSaysWhy()
        {
            string name = UniqueName();
            using var held = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                using (MachineUiLock.Acquire(name, Generous))
                {
                    held.Set();
                    done.Wait();
                }
            });
            holder.Start();
            held.Wait();
            try
            {
                var e = Assert.ThrowsExactly<AssertFailedException>(() => MachineUiLock.Acquire(name, TimeSpan.FromMilliseconds(50)));
                StringAssert.Contains(e.Message, name, "names the lock");
                StringAssert.Contains(e.Message, "hung", "says what to look for");
            }
            finally
            {
                done.Set();
                holder.Join();
            }
            using (MachineUiLock.Acquire(name, Generous)) { } // a failed wait leaves nothing held
        }

        [TestMethod]
        public void ALockWhoseHolderDied_IsTakenOver()
        {
            string name = UniqueName();
            // The holder's thread ends without releasing: Windows marks the mutex abandoned, as when a testhost is killed.
            var dead = new Thread(() => MachineUiLock.Acquire(name, Generous));
            dead.Start();
            dead.Join();
            var taken = MachineUiLock.Acquire(name, Generous);
            taken.Dispose();
            var again = new Thread(() => { using (MachineUiLock.Acquire(name, TimeSpan.FromSeconds(5))) { } });
            again.Start();
            Assert.IsTrue(again.Join(Generous), "released properly after the takeover");
        }

        [TestMethod]
        public void ReleasingOnAnotherThread_IsRefused()
        {
            string name = UniqueName();
            var held = MachineUiLock.Acquire(name, Generous);
            Exception error = null;
            var other = new Thread(() =>
            {
                try { held.Dispose(); }
                catch (Exception e) { error = e; }
            });
            other.Start();
            other.Join();
            Assert.IsInstanceOfType<InvalidOperationException>(error);
            held.Dispose();
        }
    }
}
