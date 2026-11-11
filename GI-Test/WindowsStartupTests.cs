using System;
using System.Collections.Generic;
using System.Linq;
using GI_Subtitles.Services.Startup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// "Start with Windows": the command that is written, which stale entries are repaired or removed, and what a tray
    /// start is. The Run key itself sits behind <see cref="IRunKeyStore"/>, so nothing here touches the real registry.
    /// </summary>
    [TestClass]
    public class WindowsStartupTests
    {
        private const string Installed = @"C:\Users\Ola Nowak\AppData\Local\Kaption\current\Kaption.exe";
        private static readonly string Wanted = "\"" + Installed + "\" --tray";

        /// <summary>An in-memory Run key that records every write.</summary>
        private sealed class FakeRunKey : IRunKeyStore
        {
            public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Log = new();
            public IReadOnlyDictionary<string, string> ReadAll() => new Dictionary<string, string>(Values, StringComparer.OrdinalIgnoreCase);
            public void Set(string name, string command) { Values[name] = command; Log.Add("set " + name); }
            public void Delete(string name) { Values.Remove(name); Log.Add("delete " + name); }
        }

        private static Dictionary<string, string> Key(params (string Name, string Command)[] values) =>
            values.ToDictionary(v => v.Name, v => v.Command, StringComparer.OrdinalIgnoreCase);

        // ── The command ─────────────────────────────────────────────────────

        [TestMethod]
        public void Command_IsTheQuotedExecutable_WithTheTraySwitch()
        {
            Assert.AreEqual("\"C:\\Users\\Ola Nowak\\AppData\\Local\\Kaption\\current\\Kaption.exe\" --tray", WindowsStartup.CommandFor(Installed));
        }

        [TestMethod]
        public void Command_RoundTrips_ThroughTheTargetParser()
        {
            Assert.AreEqual(Installed, WindowsStartup.TargetOf(WindowsStartup.CommandFor(Installed)));
        }

        [TestMethod]
        [DataRow(new[] { "--tray" }, true)]
        [DataRow(new[] { "--TRAY" }, true)]
        [DataRow(new[] { "--e2e", " --tray " }, true)]
        [DataRow(new string[0], false)]
        [DataRow(new[] { "--veloapp-updated", "2.6.0" }, false)]
        [DataRow(new[] { "tray" }, false)]
        [DataRow(new[] { "--tray=1" }, false)]
        public void TrayStart_IsTheExactSwitch(string[] args, bool expected)
        {
            Assert.AreEqual(expected, StartupArguments.IsTrayStart(args));
        }

        [TestMethod]
        public void TrayStart_NoArgs_IsNotOne() => Assert.IsFalse(StartupArguments.IsTrayStart(null));

        // ── Which executable ────────────────────────────────────────────────

        [TestMethod]
        public void Executable_IsTheProcessItself_WhenItIsKaption()
        {
            Assert.AreEqual(Installed, WindowsStartup.ResolveExecutable(Installed, @"C:\x\Kaption.dll", _ => false));
        }

        [TestMethod]
        public void Executable_UnderDotnet_IsTheAppHostBesideTheAssembly()
        {
            string exe = WindowsStartup.ResolveExecutable(@"C:\Program Files\dotnet\dotnet.exe", @"D:\build\Kaption.dll", p => p == @"D:\build\Kaption.exe");
            Assert.AreEqual(@"D:\build\Kaption.exe", exe);
        }

        [TestMethod]
        public void Executable_UnderDotnet_WithoutAnAppHost_IsNone()
        {
            Assert.IsNull(WindowsStartup.ResolveExecutable(@"C:\Program Files\dotnet\dotnet.exe", @"D:\build\Kaption.dll", _ => false));
        }

        [TestMethod]
        public void Executable_OfAnotherHost_IsNeverRegistered()
        {
            // A test host or a tool that loads the assembly must not put itself into the Run key.
            Assert.IsNull(WindowsStartup.ResolveExecutable(@"C:\tools\testhost.exe", @"C:\tools\testhost.dll", _ => true));
            Assert.IsNull(WindowsStartup.ResolveExecutable(null, null, _ => true));
        }

        // ── Reading an existing entry ───────────────────────────────────────

        [TestMethod]
        [DataRow("\"C:\\A B\\Kaption.exe\" --tray", @"C:\A B\Kaption.exe")]
        [DataRow(@"C:\A B\Kaption.dll", @"C:\A B\Kaption.dll")]
        [DataRow(@"C:\A B\Kaption.exe --tray", @"C:\A B\Kaption.exe")]
        [DataRow(@"C:\Apps\OneDrive.exe /background", @"C:\Apps\OneDrive.exe")]
        [DataRow("\"C:\\unterminated\\Kaption.exe", @"C:\unterminated\Kaption.exe")]
        [DataRow("", "")]
        public void Target_IsTheProgramTheCommandStarts(string command, string expected)
        {
            Assert.AreEqual(expected, WindowsStartup.TargetOf(command));
        }

        [TestMethod]
        [DataRow("Kaption", @"C:\anything\else.exe", true, DisplayName = "our value name")]
        [DataRow("kaption", "", true, DisplayName = "value names are case-insensitive")]
        [DataRow("GI-Subtitles", @"C:\old\GI-Subtitles.exe", true, DisplayName = "the name before the rename")]
        [DataRow("dotnet", @"D:\build\Kaption.dll", true, DisplayName = "an old build run through dotnet")]
        [DataRow("Whatever", "\"C:\\x\\Kaption.exe\"", true, DisplayName = "any name that starts Kaption")]
        [DataRow("OneDrive", @"C:\Apps\OneDrive.exe /background", false, DisplayName = "someone else's")]
        [DataRow("Studio", @"C:\x\Kaption.PublishStudio.exe", false, DisplayName = "another Kaption tool")]
        [DataRow("Notes", @"C:\Kaption\notes.exe", false, DisplayName = "a folder named Kaption")]
        public void Ours_IsByNameOrByTarget(string name, string command, bool expected)
        {
            Assert.AreEqual(expected, WindowsStartup.IsOurs(name, command));
        }

        // ── The plan ────────────────────────────────────────────────────────

        [TestMethod]
        public void On_EmptyKey_WritesTheCommand()
        {
            var plan = WindowsStartup.Plan(true, Installed, Key());
            Assert.AreEqual(0, plan.Delete.Count);
            Assert.AreEqual(Wanted, plan.SetCommand);
        }

        [TestMethod]
        public void On_AlreadyRight_ChangesNothing()
        {
            var plan = WindowsStartup.Plan(true, Installed, Key(("Kaption", Wanted)));
            Assert.IsTrue(plan.IsNoOp);
        }

        [TestMethod]
        public void On_OldDllEntry_IsRepaired()
        {
            // What 2.5 wrote: the assembly, unquoted, which Windows cannot start.
            var plan = WindowsStartup.Plan(true, Installed, Key(("Kaption", @"C:\Users\Ola Nowak\AppData\Local\Kaption\current\Kaption.dll")));
            Assert.AreEqual(0, plan.Delete.Count, "the value keeps its name, only its command changes");
            Assert.AreEqual(Wanted, plan.SetCommand);
        }

        [TestMethod]
        public void On_UnquotedOrSwitchlessEntry_IsRepaired()
        {
            Assert.AreEqual(Wanted, WindowsStartup.Plan(true, Installed, Key(("Kaption", Installed + " --tray"))).SetCommand, "no quotes");
            Assert.AreEqual(Wanted, WindowsStartup.Plan(true, Installed, Key(("Kaption", "\"" + Installed + "\""))).SetCommand, "no --tray");
        }

        [TestMethod]
        public void On_EntryForAnotherFolder_FollowsThisInstall()
        {
            var plan = WindowsStartup.Plan(true, Installed, Key(("Kaption", "\"D:\\old place\\Kaption.exe\" --tray")));
            Assert.AreEqual(Wanted, plan.SetCommand);
        }

        [TestMethod]
        public void On_OldNames_AreRemoved_AndTheFixedNameWritten()
        {
            var plan = WindowsStartup.Plan(true, Installed, Key(
                ("GI-Subtitles", @"C:\old\GI-Subtitles.exe"),
                ("dotnet", @"D:\build\Kaption.dll"),
                ("OneDrive", @"C:\Apps\OneDrive.exe /background")));
            CollectionAssert.AreEquivalent(new[] { "GI-Subtitles", "dotnet" }, plan.Delete.ToList());
            Assert.AreEqual(Wanted, plan.SetCommand);
        }

        [TestMethod]
        public void On_RightEntryPlusAnOldName_OnlyRemovesTheOldName()
        {
            var plan = WindowsStartup.Plan(true, Installed, Key(("Kaption", Wanted), ("GI-Subtitles", @"C:\old\GI-Subtitles.exe")));
            CollectionAssert.AreEqual(new[] { "GI-Subtitles" }, plan.Delete.ToList());
            Assert.IsNull(plan.SetCommand);
        }

        [TestMethod]
        public void On_WithoutAnExecutable_LeavesTheKeyAlone()
        {
            var plan = WindowsStartup.Plan(true, null, Key(("Kaption", @"C:\x\Kaption.dll")));
            Assert.IsTrue(plan.IsNoOp, "nothing right to write, so nothing is touched");
        }

        [TestMethod]
        public void Off_RemovesEveryEntryOfOurs_AndNothingElse()
        {
            var plan = WindowsStartup.Plan(false, Installed, Key(
                ("Kaption", Wanted),
                ("GI-Subtitles", @"C:\old\GI-Subtitles.exe"),
                ("dotnet", @"D:\build\Kaption.dll"),
                ("OneDrive", @"C:\Apps\OneDrive.exe /background"),
                ("Tray", "\"C:\\Apps\\Other.exe\" --tray")));
            CollectionAssert.AreEquivalent(new[] { "Kaption", "GI-Subtitles", "dotnet" }, plan.Delete.ToList());
            Assert.IsNull(plan.SetCommand);
        }

        [TestMethod]
        public void Off_WithNothingOfOurs_ChangesNothing()
        {
            Assert.IsTrue(WindowsStartup.Plan(false, Installed, Key(("OneDrive", @"C:\Apps\OneDrive.exe /background"))).IsNoOp);
            Assert.IsTrue(WindowsStartup.Plan(false, null, null).IsNoOp);
        }

        // ── Carrying it out ─────────────────────────────────────────────────

        [TestMethod]
        public void Apply_On_ThenAgain_WritesOnce()
        {
            var key = new FakeRunKey();
            key.Values["GI-Subtitles"] = @"C:\old\GI-Subtitles.exe";
            key.Values["OneDrive"] = @"C:\Apps\OneDrive.exe /background";

            WindowsStartup.Apply(true, Installed, key);
            CollectionAssert.AreEqual(new[] { "delete GI-Subtitles", "set Kaption" }, key.Log);
            Assert.AreEqual(Wanted, key.Values["Kaption"]);
            Assert.AreEqual(2, key.Values.Count, "ours and OneDrive's");

            key.Log.Clear();
            Assert.IsTrue(WindowsStartup.Apply(true, Installed, key).IsNoOp, "the launch repair finds nothing to do");
            Assert.AreEqual(0, key.Log.Count);
        }

        [TestMethod]
        public void Apply_Off_LeavesNothingBehind()
        {
            var key = new FakeRunKey();
            key.Values["Kaption"] = Wanted;
            key.Values["dotnet"] = @"D:\build\Kaption.dll";
            key.Values["OneDrive"] = @"C:\Apps\OneDrive.exe /background";

            WindowsStartup.Apply(false, Installed, key);
            CollectionAssert.AreEqual(new[] { "OneDrive" }, key.Values.Keys.ToList());
        }

        [TestMethod]
        public void Uninstall_RemovesOurEntries_AndNeverThrows()
        {
            var key = new FakeRunKey();
            key.Values["Kaption"] = Wanted;
            key.Values["GI-Subtitles"] = @"C:\old\GI-Subtitles.exe";
            key.Values["OneDrive"] = @"C:\Apps\OneDrive.exe /background";

            WindowsStartup.RemoveForUninstall(e2eMode: false, Installed, () => key);
            CollectionAssert.AreEqual(new[] { "OneDrive" }, key.Values.Keys.ToList());

            // A registry that refuses (policy, a profile being torn down) must not stop the uninstall.
            WindowsStartup.RemoveForUninstall(e2eMode: false, Installed, () => new ThrowingRunKey());
            WindowsStartup.RemoveForUninstall(e2eMode: false, Installed, () => throw new UnauthorizedAccessException("locked"));
        }

        [TestMethod]
        public void Uninstall_InTestModeOrAnotherHost_NeverOpensTheRealKey()
        {
            int opened = 0;
            IRunKeyStore Open() { opened++; return new FakeRunKey(); }

            WindowsStartup.RemoveForUninstall(e2eMode: true, Installed, Open);
            WindowsStartup.RemoveForUninstall(e2eMode: false, executablePath: null, Open);
            Assert.AreEqual(0, opened, "the end-to-end suite and a host that is not Kaption leave the player's entry alone");

            WindowsStartup.RemoveForUninstall(e2eMode: false, Installed, Open);
            Assert.AreEqual(1, opened);
        }

        private sealed class ThrowingRunKey : IRunKeyStore
        {
            public IReadOnlyDictionary<string, string> ReadAll() => throw new UnauthorizedAccessException("locked");
            public void Set(string name, string command) => throw new UnauthorizedAccessException("locked");
            public void Delete(string name) => throw new UnauthorizedAccessException("locked");
        }

        [TestMethod]
        public void RunKey_IsOnlyForThePlayersOwnKaption()
        {
            Assert.IsTrue(WindowsStartup.MayTouchRunKey(e2eMode: false, executablePath: @"C:\Users\x\AppData\Local\Kaption\current\Kaption.exe"));
            Assert.IsFalse(WindowsStartup.MayTouchRunKey(e2eMode: true, executablePath: @"C:\Users\x\AppData\Local\Kaption\current\Kaption.exe"),
                "the end-to-end suite keeps its own settings and must not remove the player's entry");
            Assert.IsFalse(WindowsStartup.MayTouchRunKey(e2eMode: false, executablePath: null), "a test host that loads the assembly is not Kaption");
        }
    }
}
