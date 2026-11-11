using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace GI_Test
{
    /// <summary>The system's table of drive-letter mappings, as <see cref="SubstDrive"/> needs it; tests swap in their own.</summary>
    internal interface IDosDevices
    {
        /// <summary>
        /// The letter's lock among test runs on this machine, for the whole time a run uses the letter. Null when
        /// another run holds it or it cannot be taken, with the reason.
        /// </summary>
        IDisposable TryLock(string name, out string whyNot);

        /// <summary>Puts a mapping on top of whatever <paramref name="name"/> already has. Null, or why it failed.</summary>
        string Define(string name, string target);

        /// <summary>Removes the mapping of <paramref name="name"/> to exactly <paramref name="target"/>, wherever it sits in the stack.</summary>
        bool Remove(string name, string target);

        /// <summary>The mappings of <paramref name="name"/>, the one in effect first; empty when there is none.</summary>
        IReadOnlyList<string> Targets(string name);

        /// <summary>Whether the drive's root folder opens.</summary>
        bool Opens(string name);
    }

    /// <summary>
    /// A drive letter mapped to a folder for one test, the way subst.exe does it (<c>DefineDosDevice</c>), made safe next
    /// to other test runs on the same machine. Runs that use this class take turns on a letter (a lock per letter);
    /// towards everything else it takes only a free letter and removes only its own mapping.
    /// </summary>
    internal sealed class SubstDrive : IDisposable
    {
        private readonly IDosDevices _devices;
        // The device target as the system stores it ("\??\C:\folder"): what a removal has to match exactly.
        private readonly string _target;
        private IDisposable _letterLock;

        private SubstDrive(string name, string folder, IDosDevices devices, IDisposable letterLock)
        {
            Name = name;
            _target = TargetOf(folder);
            _devices = devices;
            _letterLock = letterLock;
        }

        /// <summary>The real table: <c>DefineDosDevice</c>, <c>QueryDosDevice</c> and a named mutex per letter.</summary>
        public static IDosDevices Real { get; } = new NativeDosDevices();

        /// <summary>The drive, as <c>X:</c>.</summary>
        public string Name { get; }

        /// <summary>False once another process has mapped the letter over this one, or removed it.</summary>
        public bool IsOurs => string.Equals(_devices.Targets(Name).FirstOrDefault(), _target, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The letters with no device behind them right now. Each process starts at a different one, so two test runs
        /// on one machine seldom reach for the same letter.
        /// </summary>
        public static IReadOnlyList<char> FreeLetters(IDosDevices devices)
        {
            // A and B stay with floppy drives and C with the system, as subst users expect.
            var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(l => devices.Targets(l + ":").Count == 0).ToList();
            int start = free.Count == 0 ? 0 : Environment.ProcessId % free.Count;
            return [.. free.Skip(start), .. free.Take(start)];
        }

        /// <summary>
        /// Runs <paramref name="checks"/> on a drive mapped to <paramref name="folder"/> until they have run on one held
        /// from start to end, then returns null; a failure there is thrown. A process that does not take the letter's
        /// lock (an older copy of these tests, any other program) can still map or remove the letter, so a letter that
        /// is taken, or lost while the checks run (their failure then says nothing), is skipped for the next one. With
        /// no letter left it returns why each was skipped.
        /// </summary>
        public static string RunOnHeldDrive(string folder, Action<string> checks, IDosDevices devices)
        {
            var skipped = new List<string>();
            foreach (char letter in FreeLetters(devices))
            {
                using SubstDrive drive = TryCreate(letter, folder, out string whyNot, devices);
                if (drive == null)
                {
                    skipped.Add($"{letter}: {whyNot}");
                    continue;
                }
                bool lost = false;
                try
                {
                    checks(drive.Name);
                }
                catch (Exception) when (!drive.IsOurs)
                {
                    // The drive went away or changed under the checks. Getting it back later does not undo that:
                    // the checks stopped half way, so this letter never counts.
                    lost = true;
                }
                if (!lost && drive.IsOurs) return null;
                skipped.Add($"{letter}: another process took the letter during the checks");
            }
            return skipped.Count == 0 ? "no letter is free" : string.Join("; ", skipped);
        }

        /// <summary>
        /// Maps <paramref name="letter"/> to <paramref name="folder"/>, or returns null and says why not. The letter's
        /// lock is taken before the letter is looked at and kept until <see cref="Dispose"/>.
        /// </summary>
        public static SubstDrive TryCreate(char letter, string folder, out string whyNot, IDosDevices devices)
        {
            string name = letter + ":";
            IDisposable letterLock = devices.TryLock(name, out whyNot);
            if (letterLock == null) return null;
            var drive = new SubstDrive(name, folder, devices, letterLock);
            whyNot = devices.Targets(name).Count > 0 ? "the letter is in use" : devices.Define(name, drive._target);
            // Defining never refuses a letter, it stacks. A process without the lock that took the letter a moment
            // ago is now under our mapping (and would see our folder) or on top of it: either way it is theirs.
            whyNot ??= !(drive.IsOurs && devices.Targets(name).Count == 1) ? "another process took the letter at the same moment"
                : !devices.Opens(name) ? "the drive was defined but does not open"
                : null;
            if (whyNot == null) return drive;
            drive.Dispose();
            return null;
        }

        /// <summary>Removes this mapping and no other (a letter someone else holds now stays theirs), then frees the letter's lock.</summary>
        public void Dispose()
        {
            _devices.Remove(Name, _target);
            Interlocked.Exchange(ref _letterLock, null)?.Dispose();
        }

        internal static string TargetOf(string folder) => @"\??\" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

        private sealed class NativeDosDevices : IDosDevices
        {
            private const uint RawTargetPath = 0x1;        // DDD_RAW_TARGET_PATH
            private const uint RemoveDefinition = 0x2;     // DDD_REMOVE_DEFINITION
            private const uint ExactMatchOnRemove = 0x4;   // DDD_EXACT_MATCH_ON_REMOVE
            private const uint NoBroadcastSystem = 0x8;    // DDD_NO_BROADCAST_SYSTEM: no WM_DEVICECHANGE to every window
            private const int ErrorFileNotFound = 2;
            private const int MaxPath = 32_768;            // the longest extended-length path
            // Global: a service account's letters are machine-wide, so its runs and a desktop run share one lock.
            private const string LockPrefix = @"Global\kaption-tests-drive-";

            public IDisposable TryLock(string name, out string whyNot)
            {
                Mutex mutex = null;
                try
                {
                    mutex = new Mutex(initiallyOwned: false, LockPrefix + name.TrimEnd(':'));
                    bool held;
                    try
                    {
                        held = mutex.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        // Its last holder died mid-test; the wait still gave it to this thread.
                        held = true;
                    }
                    if (held)
                    {
                        whyNot = null;
                        return new HeldMutex(mutex);
                    }
                    whyNot = "another test run is using the letter";
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
                {
                    // Another account's run owns the lock object, or this account may not create one.
                    whyNot = "the letter's lock cannot be taken: " + ex.Message;
                }
                mutex?.Dispose();
                return null;
            }

            public string Define(string name, string target) =>
                DefineDosDeviceW(RawTargetPath | NoBroadcastSystem, name, target)
                    ? null
                    : "DefineDosDevice: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;

            public bool Remove(string name, string target) =>
                DefineDosDeviceW(RawTargetPath | NoBroadcastSystem | RemoveDefinition | ExactMatchOnRemove, name, target);

            public IReadOnlyList<string> Targets(string name)
            {
                var buffer = new char[MaxPath];
                uint length = QueryDosDeviceW(name, buffer, (uint)buffer.Length);
                if (length == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    // Only "no such device" means free; a letter that cannot be queried is treated as someone else's.
                    return error == ErrorFileNotFound ? [] : ["?" + error];
                }
                // The mapping in effect, then the earlier ones it covers, each ended by a null.
                return new string(buffer, 0, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }

            public bool Opens(string name) => Directory.Exists(name + Path.DirectorySeparatorChar);

            /// <summary>A mutex belongs to the thread that took it: release it there, as a test's using block does.</summary>
            private sealed class HeldMutex(Mutex mutex) : IDisposable
            {
                public void Dispose()
                {
                    mutex.ReleaseMutex();
                    mutex.Dispose();
                }
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool DefineDosDeviceW(uint flags, string deviceName, string targetPath);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern uint QueryDosDeviceW(string deviceName, [Out] char[] targetPath, uint max);
        }
    }
}
