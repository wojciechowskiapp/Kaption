using System;

namespace GI_Subtitles.Services.OCR;

/// <summary>Why OCR runs on the CPU although the GPU was requested.</summary>
public enum OcrGpuStatus
{
    Ok,
    /// <summary>Measured too slow; persisted per version by <see cref="OcrGpuQuarantine"/>.</summary>
    Quarantined,
    /// <summary>DirectML did not start at all (old driver, unsupported device, missing DirectML.dll).</summary>
    Unavailable,
    /// <summary>One failed or unfit warm-up: CPU for this session only, DirectML retried at the next launch.</summary>
    RetryNextLaunch,
}

/// <summary>
/// Decides whether DirectML is unfit after warm-up. Warm-up time is mostly kernel compilation,
/// which CPU and GPU load elsewhere stretches (a game loading, another GPU job), so a slow warm-up
/// alone is not evidence. A timed pass over the already-compiled shapes is what each frame costs.
/// </summary>
public static class OcrGpuFitness
{
    /// <summary>Two shape compiles plus device init; see the history in SettingsWindow.LoadEngine.</summary>
    public static readonly TimeSpan WarmUpBudget = TimeSpan.FromSeconds(3);

    /// <summary>One compiled detector + recogniser pass. A GPU slower than this has no edge over the CPU.</summary>
    public static readonly TimeSpan SteadyStateBudget = TimeSpan.FromMilliseconds(250);

    public static bool NeedsSteadyStateProbe(bool usingGpu, TimeSpan warmUp) => usingGpu && warmUp > WarmUpBudget;

    public static bool IsUnfit(TimeSpan warmUp, TimeSpan steadyState) =>
        warmUp > WarmUpBudget && steadyState > SteadyStateBudget;

    /// <summary>A failed or unfit warm-up is a strike; only the second in a row for a version quarantines.</summary>
    public static bool QuarantineAfterStrike(int strikesBefore) => strikesBefore >= 1;

    public static OcrGpuStatus Status(bool gpuRequested, bool quarantined, bool engineReady, bool engineUsesGpu,
        bool strikeThisSession = false) =>
        !gpuRequested ? OcrGpuStatus.Ok
        : quarantined ? OcrGpuStatus.Quarantined
        : !engineReady || engineUsesGpu ? OcrGpuStatus.Ok
        : strikeThisSession ? OcrGpuStatus.RetryNextLaunch
        : OcrGpuStatus.Unavailable;
}
