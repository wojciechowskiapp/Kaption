using System;
using GI_Subtitles.Services.OCR;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test;

/// <summary>
/// A slow DirectML warm-up is mostly kernel compilation, which load elsewhere on the machine stretches.
/// On 2026-11-01 one 3.8 s warm-up on an RTX 5070 Ti moved a whole release to CPU. Only a GPU that is
/// still slow on a compiled pass is quarantined.
/// </summary>
[TestClass]
public class OcrGpuFitnessTests
{
    [TestMethod]
    public void FastWarmUp_NeedsNoProbe()
    {
        Assert.IsFalse(OcrGpuFitness.NeedsSteadyStateProbe(usingGpu: true, TimeSpan.FromMilliseconds(877)));
        Assert.IsFalse(OcrGpuFitness.NeedsSteadyStateProbe(usingGpu: true, OcrGpuFitness.WarmUpBudget));
    }

    [TestMethod]
    public void SlowWarmUp_OnGpu_IsProbed() =>
        Assert.IsTrue(OcrGpuFitness.NeedsSteadyStateProbe(usingGpu: true, TimeSpan.FromSeconds(3.8)));

    [TestMethod]
    public void CpuEngine_IsNeverProbed() =>
        Assert.IsFalse(OcrGpuFitness.NeedsSteadyStateProbe(usingGpu: false, TimeSpan.FromSeconds(30)));

    [TestMethod]
    public void SlowCompile_FastInference_KeepsTheGpu() =>
        Assert.IsFalse(OcrGpuFitness.IsUnfit(TimeSpan.FromSeconds(3.8), TimeSpan.FromMilliseconds(24)));

    [TestMethod]
    public void SlowCompile_SlowInference_Quarantines() =>
        Assert.IsTrue(OcrGpuFitness.IsUnfit(TimeSpan.FromSeconds(3.8), TimeSpan.FromMilliseconds(900)));

    [TestMethod]
    public void WarmUpWithinBudget_NeverQuarantines() =>
        Assert.IsFalse(OcrGpuFitness.IsUnfit(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));

    [TestMethod]
    [DataRow(false, false, true, false, OcrGpuStatus.Ok)]
    [DataRow(true, false, true, true, OcrGpuStatus.Ok)]
    [DataRow(true, false, false, false, OcrGpuStatus.Ok)]
    [DataRow(true, true, true, false, OcrGpuStatus.Quarantined)]
    [DataRow(true, false, true, false, OcrGpuStatus.Unavailable)]
    [DataRow(false, true, true, false, OcrGpuStatus.Ok)]
    public void Status_TellsSlowFromUnavailable(bool requested, bool quarantined, bool engineReady, bool usesGpu, OcrGpuStatus expected) =>
        Assert.AreEqual(expected, OcrGpuFitness.Status(requested, quarantined, engineReady, usesGpu));

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(4, true)]
    public void WarmUpStrike_QuarantinesOnlyTheSecondInARow(int strikesBefore, bool quarantine) =>
        Assert.AreEqual(quarantine, OcrGpuFitness.QuarantineAfterStrike(strikesBefore));

    [TestMethod]
    public void Status_AfterAOneOffStrike_SaysRetryNextLaunch()
    {
        Assert.AreEqual(OcrGpuStatus.RetryNextLaunch,
            OcrGpuFitness.Status(gpuRequested: true, quarantined: false, engineReady: true, engineUsesGpu: false, strikeThisSession: true));
        Assert.AreEqual(OcrGpuStatus.Quarantined,
            OcrGpuFitness.Status(gpuRequested: true, quarantined: true, engineReady: true, engineUsesGpu: false, strikeThisSession: true));
    }

    [TestMethod]
    public void ProbeAtTheBudget_StillCountsAsFit() =>
        Assert.IsFalse(OcrGpuFitness.IsUnfit(TimeSpan.FromSeconds(4), OcrGpuFitness.SteadyStateBudget));
}
