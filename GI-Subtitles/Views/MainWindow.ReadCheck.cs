using System;
using System.Windows.Controls;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.Diagnostics;
using Logger = GI_Subtitles.Common.Logger;

namespace GI_Subtitles.Views
{
    /// <summary>
    /// Subtitles over the game's own text, the part nobody sees: with "Overlay.Placement" set to "OverText" (and
    /// recordings off) this window is left out of Desktop Duplication, the screen read the dialogue is read with, and
    /// the read check (Services/Capture/OverlayReadCheck.cs) proves by reading that the app no longer sees its own
    /// panel. Only then, and only for reads through the reader it was proven for, does the panel stop being blacked
    /// out (Services/Capture/OverlayReadProof.cs holds that). Windows saying "accepted" is never enough: the picture
    /// Desktop Duplication hands out changes only where the screen is drawn again. The placement itself and its place
    /// on the look page come with the next piece of work; until then the key has no control.
    /// Design and measurements: .plan/research/s68/OVERLAY-OVER-TEXT.md.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// A check holds the capture tick while it runs, so it gives up soon. Over a picture that is drawing, a switch
        /// reached the frames in 36 ms at most, and a whole check took 0.1 to 0.2 s in the spike, when it looked at a
        /// plain screen copy once (it now looks once per counted frame, three small copies more). The benchmark's own,
        /// slower limits are <see cref="OverlayReadLimits"/>'s defaults.
        /// </summary>
        private static readonly OverlayReadLimits ReadCheckLimits = new() { StepMs = 400, TotalMs = 1200 };

        private readonly OverlayReadProof _readProof = new();
        private readonly SelfReadTripwire _selfRead = new();
        private OverlayReadHost _readCheckHost;
        /// <summary>What Windows was last told about Desktop Duplication, and accepted.</summary>
        private bool _outOfDuplication;
        /// <summary>The check asked Windows to show the overlay to screen capture again, and the hiding is owed back.</summary>
        private bool _windowsHidingLiftedForCheck;

        private void InitOverlayReadCheck()
        {
            DiagnosticBundle.RegisterStateProvider("Subtitles over the game's text", DescribeOverlayReadCheck);
            Closed += (_, __) => _readCheckHost?.Dispose();
        }

        private string DescribeOverlayReadCheck() =>
            !_readProof.Wanted ? "off" : $"read check: {_readProof.State}, checks without proof: {_readProof.ChecksWithoutProof}";

        /// <summary>Which setup of a reader a proof is about: Desktop Duplication counts its restarts, a screen copy has none.</summary>
        private static long ReaderEpoch(IScreenCapture reader) => reader is DxgiScreenCapture duplication ? duplication.Restarts : 0;

        /// <summary>
        /// Whether a read through the capture backend is free of the subtitle panel right now. Asked at every such
        /// read, after its capture: translation is running (so the check and the tripwire are watching), and the proof
        /// is for the very reader in use, in the setup it is in.
        /// </summary>
        private bool PanelProvenOutOfReads =>
            OCRTimer.IsEnabled && _readProof.Holds(_captureBackend, ReaderEpoch(_captureBackend));

        /// <summary>
        /// The attribute follows the settings: on only for subtitles over the text with recordings off. A window that
        /// never asked for it is never touched, so nothing changes for a player without the key.
        /// </summary>
        private void ApplyDuplicationExclusion()
        {
            bool wanted = CaptureVisibility.LeavesOutOfDuplication(CaptureVisibility.Placement, _showInRecordings);
            long now = Environment.TickCount64;
            // The proof goes with the wish, whether or not Windows has to be told anything.
            bool changed = _readProof.SettingsSay(wanted, now);
            if (wanted == _outOfDuplication || _mainWindowHandle == IntPtr.Zero) return;
            bool accepted = CaptureExclusion.TrySetOutOfDesktopDuplication(_mainWindowHandle, wanted);
            if (accepted) _outOfDuplication = wanted;
            Logger.Log.Info($"Subtitles over the game's text: the overlay is {(wanted ? "left out of" : "back in")} Desktop Duplication " +
                            $"(Windows {(accepted ? "accepted" : "refused")}).");
            // The same wish told to Windows anew (it had refused before): what was proven meanwhile does not count.
            if (!changed) _readProof.Forget(now);
        }

        /// <summary>
        /// Translation starts: nothing proven earlier counts, and the first capture tick runs the check before its
        /// reads. Not here, in the start path: the check lets the window draw between its looks, which also lets a
        /// hotkey through, and it needs the game drawing under the overlay, which a start from Settings does not have.
        /// </summary>
        private void OnTranslationStarting()
        {
            ApplyDuplicationExclusion();
            _selfRead.Reset();
            _readProof.TranslationStarted(Environment.TickCount64);
        }

        /// <summary>
        /// On the capture tick, so with the game in front and before the tick's reads: the check after a start, a check
        /// that proved nothing tried again (a still picture, something over the overlay), and a reader that was
        /// replaced or set up again checked again. One comparison when nothing is asked for.
        /// </summary>
        /// <returns>
        /// True when a check ran. It lets input through while it waits (a Stop, another window coming to the front), so
        /// the tick that ran it must read nothing; the next tick starts from its own guards. Never throws: the caller
        /// clears the tick's busy flag only after this returns.
        /// </returns>
        private bool RunOverlayReadCheckIfDue()
        {
            if (!_readProof.Wanted) return false;
            try
            {
                long now = Environment.TickCount64;
                // The masks ask Holds at every read; this only gets the next check going when the proof no longer holds.
                if (_readProof.State == OverlayReadState.OutOfFrame && !_readProof.Holds(_captureBackend, ReaderEpoch(_captureBackend)))
                    _readProof.Forget(now);
                if (!_readProof.Due(now)) return false;
                RunOverlayReadCheck();
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Overlay read check failed: {ex.Message}");
            }
            return true;
        }

        /// <summary>The capture backend was replaced (Desktop Duplication lost or back): nothing is known about the new one.</summary>
        private void OnOverlayReaderReplaced()
        {
            if (_readProof.Wanted) _readProof.Forget(Environment.TickCount64);
        }

        private void RunOverlayReadCheck()
        {
            OverlayReadState before = _readProof.State;
            bool first = _readProof.FirstSinceStart;
            long ticket = _readProof.Begin();
            IScreenCapture reader = _captureBackend;
            long epoch = ReaderEpoch(reader);
            OverlayReadResult result = null;
            try
            {
                OverlayReadHost host = ReadCheckHost();
                if (host != null) result = OverlayReadCheck.Run(host, ReadCheckLimits);
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"Overlay read check failed: {ex.Message}");
            }
            try
            {
                LeaveOverlayAsTheSettingsSay();
            }
            catch (Exception ex)
            {
                // What the window is hidden from is not known now, so neither is what the check found.
                result = null;
                Logger.Log.Warn($"Overlay read check: the overlay could not be left as the settings say: {ex.Message}");
            }

            OverlayReadState state = result?.State ?? OverlayReadState.Unknown;
            string detail = result == null ? "the check could not be run to its end" : result.Reason;
            // The reader the check looked through must still be the one in use, in the setup it had.
            if (state == OverlayReadState.OutOfFrame && (!ReferenceEquals(reader, _captureBackend) || epoch != ReaderEpoch(_captureBackend)))
            {
                state = OverlayReadState.Unknown;
                detail = "the reader was replaced or set up again during the check";
            }
            // False: a setting changed or a new reader came while the check ran, and the check that asked for is due.
            if (!_readProof.End(ticket, state, reader, epoch, Environment.TickCount64)) return;

            // One line per start; after that only when the answer changed.
            if (first || state != before)
            {
                Logger.Log.Info($"Overlay read check {(first ? "after a start" : "while translating")}: {state}" +
                                (result != null ? $" in {result.ElapsedMs} ms" : "") +
                                (state == OverlayReadState.OutOfFrame ? "." : $": {detail}. The panel stays blacked out of the app's reads."));
            }
        }

        private OverlayReadHost ReadCheckHost()
        {
            if (_readCheckHost != null) return _readCheckHost;
            if (Content is not Panel root || _mainWindowHandle == IntPtr.Zero) return null;
            return _readCheckHost = new OverlayReadHost(this, root, () => _captureBackend, SetOverlayHiddenForCheck, KeepOverlayOnTop);
        }

        /// <summary>The app's tick does this too: a window that was activated can go above the overlay.</summary>
        private void KeepOverlayOnTop() => SetWindowPos(_mainWindowHandle, -1, 0, 0, 0, 0, TopmostNoActivate);

        /// <summary>
        /// The check's switch. Shown (false): in every screen read, so the strip can be seen; Windows' own hiding is
        /// lifted for the length of the check, and when Windows will not lift it nothing can be checked. Hidden (true):
        /// out of Desktop Duplication only, so that a plain screen copy still shows the strip the reader must miss.
        /// </summary>
        private bool SetOverlayHiddenForCheck(bool hidden)
        {
            if (hidden)
            {
                if (CaptureExclusion.TrySetOutOfDesktopDuplication(_mainWindowHandle, true)) _outOfDuplication = true;
                return true;
            }
            if (CaptureExclusion.TrySetOutOfDesktopDuplication(_mainWindowHandle, false)) _outOfDuplication = false;
            if (_windowsHideAnswer is WindowsHideAnswer.Hidden or WindowsHideAnswer.StillHidden)
            {
                if (!TryShowInCapture(_mainWindowHandle)) return false;
                _windowsHideAnswer = WindowsHideAnswer.NotAsked;
                _windowsHidingLiftedForCheck = true;
            }
            return true;
        }

        /// <summary>However the check ended, the window is left as the settings say.</summary>
        private void LeaveOverlayAsTheSettingsSay()
        {
            if (_mainWindowHandle == IntPtr.Zero) return;
            if (_windowsHidingLiftedForCheck)
            {
                _windowsHidingLiftedForCheck = false;
                ApplyWindowsHiding();
            }
            if (_outOfDuplication != _readProof.Wanted
                && CaptureExclusion.TrySetOutOfDesktopDuplication(_mainWindowHandle, _readProof.Wanted))
                _outOfDuplication = _readProof.Wanted;
        }

        /// <summary>
        /// Between checks: the text just read against the line on screen (Services/Capture/SelfRead.cs). Two own lines
        /// in a row and the panel is blacked out of the reads again; the next start checks anew.
        /// </summary>
        private void WatchForOwnLine()
        {
            if (_readProof.State != OverlayReadState.OutOfFrame) return;
            if (!_selfRead.Observe(ocrText, lastContent)) return;
            _selfRead.Reset();
            _readProof.Tripped();
            Logger.Log.Warn("Overlay read check: the app read its own subtitle twice in a row. The panel is blacked out of its reads again until the next start.");
        }
    }
}
