using System;
using System.Runtime.InteropServices;
using GI_Subtitles.Services.OCR;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// A lost GPU device (Sentry KAPTION-DESKTOP-1, GitHub #261) failed every read until a restart. These pin how the
    /// error is recognised and the rebuild-once-then-cooldown rule, without a GPU.
    /// </summary>
    [TestClass]
    public class OcrDeviceLossTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 12, 17, 34, 40, DateTimeKind.Utc);

        // The event's message word for word, Polish Windows text included: only the code is stable across languages.
        private const string SentryMessage =
            @"[ErrorCode:Fail] E:\_work\1\s\onnxruntime\core\providers\dml\DmlExecutionProvider\src\ExecutionProvider.cpp(952)" +
            @"\onnxruntime.dll!00007FFDCFBF1449: (caller: 00007FFDCFBF181B) Exception(277) tid(278c) 887A0005 " +
            "Wystąpienie urządzenia GPU zostało zawieszone. Użyj obiektu GetDeviceRemovedReason, aby określić odpowiednią akcję.";

        [TestMethod]
        public void IsDeviceLost_TheReportedMessage_IsRecognised()
        {
            Assert.IsTrue(OcrDeviceLoss.IsDeviceLost(new Exception(SentryMessage)));
        }

        [TestMethod]
        [DataRow("887A0005")] // DXGI_ERROR_DEVICE_REMOVED
        [DataRow("887a0006")] // DXGI_ERROR_DEVICE_HUNG
        [DataRow("0x887A0007")] // DXGI_ERROR_DEVICE_RESET
        [DataRow("887A0020")] // DXGI_ERROR_DRIVER_INTERNAL_ERROR
        public void IsDeviceLost_EachDeviceGoneCode_IsRecognised(string code)
        {
            Assert.IsTrue(OcrDeviceLoss.IsDeviceLost(new Exception($"[ErrorCode:Fail] Exception(1) tid(10) {code} text")));
        }

        [TestMethod]
        public void IsDeviceLost_LooksThroughWrappers()
        {
            var inner = new Exception(SentryMessage);
            Assert.IsTrue(OcrDeviceLoss.IsDeviceLost(new InvalidOperationException("read failed", inner)));
            Assert.IsTrue(OcrDeviceLoss.IsDeviceLost(new AggregateException(new Exception("other"), inner)));
        }

        [TestMethod]
        public void IsDeviceLost_SeenThroughTheWatchdogCancellation()
        {
            // A hung GPU can throw only after the live-frame watchdog fired; DetectTextFromMat then wraps it like this.
            var cancelled = new OperationCanceledException(
                "OCR inference was cancelled during detection.", new Exception(SentryMessage));
            Assert.IsTrue(OcrDeviceLoss.IsDeviceLost(cancelled));
        }

        [TestMethod]
        public void IsDeviceLost_ReadsTheResultCode()
        {
            Assert.IsTrue(OcrDeviceLoss.IsDeviceLost(new COMException("removed", unchecked((int)0x887A0005))));
            Assert.IsFalse(OcrDeviceLoss.IsDeviceLost(new COMException("invalid call", unchecked((int)0x887A0001))));
        }

        [TestMethod]
        [DataRow("[ErrorCode:Fail] Exception(277) tid(278c) 887A0001 invalid call")] // DXGI_ERROR_INVALID_CALL: our bug, not a lost device
        [DataRow("[ErrorCode:Fail] Non-zero status code returned while running Conv node.")]
        [DataRow("buffer at 0x1887A00050 overflowed")] // the code inside a longer hex number
        [DataRow("")]
        public void IsDeviceLost_OtherFailures_AreNot(string message)
        {
            Assert.IsFalse(OcrDeviceLoss.IsDeviceLost(new Exception(message)));
        }

        [TestMethod]
        public void IsDeviceLost_TimeoutsAndNull_AreNot()
        {
            Assert.IsFalse(OcrDeviceLoss.IsDeviceLost(null));
            Assert.IsFalse(OcrDeviceLoss.IsDeviceLost(new OperationCanceledException()));
            Assert.IsFalse(OcrDeviceLoss.IsDeviceLost(new TimeoutException("OCR warm-up exceeded 30s")));
        }

        [TestMethod]
        public void Decide_FirstLoss_RebuildsOnTheGpu()
        {
            var policy = new OcrDeviceLossPolicy();
            Assert.AreEqual(OcrDeviceLossAction.RebuildOnGpu, policy.Decide(T0));
        }

        [TestMethod]
        public void Decide_SecondLossInsideCooldown_GoesToTheProcessor()
        {
            var policy = new OcrDeviceLossPolicy();
            policy.Decide(T0);

            Assert.AreEqual(OcrDeviceLossAction.RebuildOnProcessor,
                policy.Decide(T0 + OcrDeviceLossPolicy.Cooldown - TimeSpan.FromSeconds(1)));
        }

        [TestMethod]
        public void Decide_LossAfterCooldown_RebuildsOnTheGpuAgain()
        {
            // A laptop that sleeps every evening loses its device each time; each time is a fresh first loss.
            var policy = new OcrDeviceLossPolicy();
            policy.Decide(T0);

            Assert.AreEqual(OcrDeviceLossAction.RebuildOnGpu, policy.Decide(T0 + OcrDeviceLossPolicy.Cooldown));
            Assert.AreEqual(OcrDeviceLossAction.RebuildOnProcessor,
                policy.Decide(T0 + OcrDeviceLossPolicy.Cooldown + TimeSpan.FromMinutes(1)));
        }

        [TestMethod]
        public void Decide_ProcessorAnswer_DoesNotRestartTheCooldown()
        {
            var policy = new OcrDeviceLossPolicy();
            policy.Decide(T0);
            policy.Decide(T0 + TimeSpan.FromMinutes(5));

            Assert.AreEqual(OcrDeviceLossAction.RebuildOnGpu, policy.Decide(T0 + OcrDeviceLossPolicy.Cooldown));
        }

        [TestMethod]
        public void Decide_ClockWentBackwards_CountsAsInsideTheCooldown()
        {
            var policy = new OcrDeviceLossPolicy();
            policy.Decide(T0);

            Assert.AreEqual(OcrDeviceLossAction.RebuildOnProcessor, policy.Decide(T0 - TimeSpan.FromHours(1)));
        }
    }
}
