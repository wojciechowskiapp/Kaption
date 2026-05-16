using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GI_Subtitles.Core.Runtime
{
    /// <summary>
    /// RAM diagnostic helpers — checkpoint logging + forced working-set trim.
    ///
    /// All calls are cheap and safe on the UI thread. <see cref="LogCheckpoint"/>
    /// emits one log line per call; <see cref="TrimWorkingSet"/> hands Windows a
    /// hint that the process can be paged down after a heavy allocation burst
    /// (matcher build, dialog graph materialisation) — real RAM stays in the
    /// OS file cache or page file, Task Manager stops double-counting it.
    /// </summary>
    internal static class RamDiag
    {
        // EmptyWorkingSet equivalent: SetProcessWorkingSetSize with (-1, -1)
        // tells Windows to trim the process's working set down to just the
        // pages it's actively referencing. The pages are not discarded — they
        // move to the standby / modified list and are paged back in on access.
        // This has no effect on commit size; it only affects what Task Manager
        // reports as "Working Set".
        //
        // Documented trade-off: first access after a trim incurs a soft page
        // fault (microseconds). We call this at quiet points (post-matcher-build,
        // post-graph-load) so the cost is unnoticeable.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimum, IntPtr dwMaximum);

        private static readonly IntPtr TrimSentinel = new IntPtr(-1);

        /// <summary>
        /// Emits one INFO log line with working-set, commit size, and GC
        /// generation counts. Use at before/after boundaries of heavy
        /// allocation phases to pinpoint where RSS grows.
        /// </summary>
        public static void LogCheckpoint(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "(unnamed)";
            try
            {
                using (var proc = Process.GetCurrentProcess())
                {
                    long wsMb = proc.WorkingSet64 / (1024L * 1024L);
                    long privMb = proc.PrivateMemorySize64 / (1024L * 1024L);
                    long pagedMb = proc.PagedMemorySize64 / (1024L * 1024L);
                    int gen0 = GC.CollectionCount(0);
                    int gen1 = GC.CollectionCount(1);
                    int gen2 = GC.CollectionCount(2);
                    long heapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024L * 1024L);
                    Common.Logger.Log.Info(
                        $"[RAM] {name}: WS={wsMb} MB · Private={privMb} MB · Paged={pagedMb} MB · GCheap={heapMb} MB · Gen0={gen0} Gen1={gen1} Gen2={gen2}");
                }
            }
            catch (Exception ex)
            {
                // Never let diagnostic code break the app.
                try { Common.Logger.Log.Debug($"[RAM] {name}: checkpoint failed: {ex.Message}"); } catch { /* swallow */ }
            }
        }

        /// <summary>
        /// Asks Windows to trim the process working set. Safe to call repeatedly;
        /// returns true if the OS accepted the request. Does NOT force a managed
        /// GC — the caller should already have collected if they want GCheap
        /// reduced before the trim (otherwise GCheap pages stay resident).
        /// </summary>
        public static bool TrimWorkingSet()
        {
            try
            {
                using (var proc = Process.GetCurrentProcess())
                {
                    bool ok = SetProcessWorkingSetSize(proc.Handle, TrimSentinel, TrimSentinel);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        Common.Logger.Log.Debug($"[RAM] TrimWorkingSet: SetProcessWorkingSetSize failed (win32 {err}).");
                    }
                    return ok;
                }
            }
            catch (Exception ex)
            {
                try { Common.Logger.Log.Debug($"[RAM] TrimWorkingSet failed: {ex.Message}"); } catch { /* swallow */ }
                return false;
            }
        }

        /// <summary>
        /// Full release hint: forces a Gen2 GC + LOH compact + working-set trim.
        /// Use at transitional moments (OCR stop, game switch, settings close)
        /// where the user is not actively translating — blocking Gen2 here is
        /// acceptable. Never call during an OCR tick.
        /// </summary>
        public static void AggressiveReclaim(string reason)
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                TrimWorkingSet();
                LogCheckpoint($"after AggressiveReclaim ({reason})");
            }
            catch (Exception ex)
            {
                try { Common.Logger.Log.Debug($"[RAM] AggressiveReclaim({reason}) failed: {ex.Message}"); } catch { /* swallow */ }
            }
        }

        // ── Background memory watchdog ────────────────────────────────────
        //
        // Runs ONE long-lived background thread that:
        //   (1) Sits on GC.WaitForFullGCApproach / Complete so we react to
        //       .NET's natural full-GC triggers (Gen2 threshold, OS low-mem
        //       pressure) — NOT our own AggressiveReclaim calls.
        //   (2) After a natural full GC completes, trims the working set so
        //       Task Manager reflects the reclaim instead of showing the
        //       pre-GC number indefinitely.
        //   (3) Separately, on a cheap timer, emits a [RAM] checkpoint every
        //       N seconds so long-running sessions have a trend log.
        //
        // Zero cost when GC is idle — `WaitForFullGCApproach` blocks without
        // polling. The periodic timer is a simple Timer that fires on a
        // ThreadPool thread; its overhead is a single log line per interval.

        private static int _monitorStarted;           // 0 = not started, 1 = started
        private static System.Threading.Timer _periodicLogTimer;
        private const int PeriodicLogIntervalMs = 120_000;   // 2 min
        private const int MaxNotificationThreshold = 99;     // .NET accepts 1..99
        private const int MinNotificationThreshold = 25;     // trigger when heap is 25% toward Gen2 budget

        /// <summary>
        /// Starts the background memory watchdog. Safe to call once; repeat
        /// calls are no-ops. Idempotent across app domain lifetimes.
        /// </summary>
        public static void StartBackgroundMonitor()
        {
            if (System.Threading.Interlocked.Exchange(ref _monitorStarted, 1) != 0)
            {
                return; // already running
            }

            try
            {
                // Register for full GC notifications. The two numbers are
                // thresholds for "approach" and "complete" — low values mean
                // we get notified earlier. CLR docs say 1..99.
                GC.RegisterForFullGCNotification(MinNotificationThreshold, MinNotificationThreshold);

                var waiter = new System.Threading.Thread(FullGcWaiterLoop)
                {
                    IsBackground = true,
                    Name = "Kaption-RamDiag-GCWaiter",
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                };
                waiter.Start();

                _periodicLogTimer = new System.Threading.Timer(
                    _ => LogCheckpoint("periodic"),
                    state: null,
                    dueTime: PeriodicLogIntervalMs,
                    period: PeriodicLogIntervalMs);

                Common.Logger.Log.Info($"[RAM] Background monitor started (full-GC watcher + {PeriodicLogIntervalMs / 1000}s periodic log).");
            }
            catch (Exception ex)
            {
                try { Common.Logger.Log.Warn($"[RAM] StartBackgroundMonitor failed: {ex.Message}"); } catch { /* swallow */ }
                System.Threading.Interlocked.Exchange(ref _monitorStarted, 0);
            }
        }

        /// <summary>
        /// Stops the background watchdog. Call on app shutdown so the
        /// waiter thread doesn't block the CLR teardown.
        /// </summary>
        public static void StopBackgroundMonitor()
        {
            try
            {
                GC.CancelFullGCNotification();
                var t = _periodicLogTimer;
                _periodicLogTimer = null;
                t?.Dispose();
            }
            catch (Exception ex)
            {
                try { Common.Logger.Log.Debug($"[RAM] StopBackgroundMonitor: {ex.Message}"); } catch { /* swallow */ }
            }
        }

        private static void FullGcWaiterLoop()
        {
            while (System.Threading.Volatile.Read(ref _monitorStarted) == 1)
            {
                GCNotificationStatus approach;
                try
                {
                    // Block until the CLR says a full GC is approaching, or
                    // notifications are cancelled. No polling — returns
                    // immediately on event.
                    approach = GC.WaitForFullGCApproach();
                }
                catch (InvalidOperationException)
                {
                    // Notifications were cancelled while we were waiting.
                    break;
                }
                catch (Exception ex)
                {
                    try { Common.Logger.Log.Debug($"[RAM] WaitForFullGCApproach: {ex.Message}"); } catch { /* swallow */ }
                    break;
                }

                if (approach == GCNotificationStatus.Canceled || approach == GCNotificationStatus.NotApplicable)
                {
                    break;
                }

                if (approach == GCNotificationStatus.Succeeded)
                {
                    // Full GC is imminent. We don't try to front-run it —
                    // just log for visibility and let it happen.
                    try { Common.Logger.Log.Info("[RAM] auto: full GC approaching"); } catch { /* swallow */ }

                    try
                    {
                        GCNotificationStatus complete = GC.WaitForFullGCComplete();
                        if (complete == GCNotificationStatus.Succeeded)
                        {
                            // Natural Gen2 collected but didn't LOH-compact or
                            // trim WS. Do both so Task Manager reflects the
                            // reclaim and LOH fragmentation gets swept.
                            try
                            {
                                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                            }
                            catch { /* swallow — best effort */ }
                            TrimWorkingSet();
                            LogCheckpoint("auto after full GC");
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Notifications cancelled between approach and complete.
                        break;
                    }
                    catch (Exception ex)
                    {
                        try { Common.Logger.Log.Debug($"[RAM] WaitForFullGCComplete: {ex.Message}"); } catch { /* swallow */ }
                    }
                }
            }
        }
    }
}
