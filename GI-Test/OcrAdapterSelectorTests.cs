using System.Linq;
using GI_Subtitles.Services.Capture;
using GI_Subtitles.Services.OCR;
using Microsoft.ML.OnnxRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Which GPU DirectML runs OCR on. Field report KAP-EF8C-C5CF: a hybrid laptop ran OCR on its Intel UHD
    /// (adapter 0, it drives the panel), which read nothing, while its RTX 3050 Ti sat idle.
    /// </summary>
    [TestClass]
    public class OcrAdapterSelectorTests
    {
        private const long IntelLuid = 0x1111, NvidiaLuid = 0x2222, BasicLuid = 0x3333;

        private static GpuAdapterInfo Intel(int index) =>
            new(index, "Intel(R) UHD Graphics", 0x8086, 0x9A60, 128, IntelLuid, IsSoftware: false);

        private static GpuAdapterInfo Nvidia(int index) =>
            new(index, "NVIDIA GeForce RTX 3050 Ti Laptop GPU", 0x10DE, 0x25A0, 4096, NvidiaLuid, IsSoftware: false);

        private static GpuAdapterInfo Basic(int index) =>
            new(index, "Microsoft Basic Render Driver", 0x1414, 0x8C, 0, BasicLuid, IsSoftware: true);

        [TestMethod]
        public void HybridLaptop_UsesTheHighPerformanceAdapter()
        {
            var choice = OcrAdapterSelector.Choose(
                new[] { Intel(0), Nvidia(1), Basic(2) },
                new[] { NvidiaLuid, IntelLuid, BasicLuid },
                DxgiGpuPreference.HighPerformance);

            Assert.AreEqual(1, choice.Index, choice.Reason);
        }

        [TestMethod]
        public void SingleGpu_KeepsAdapterZero()
        {
            var choice = OcrAdapterSelector.Choose(
                new[] { Nvidia(0), Basic(1) }, new[] { NvidiaLuid, BasicLuid }, DxgiGpuPreference.HighPerformance);

            Assert.AreEqual(0, choice.Index);
        }

        [TestMethod]
        public void DiscreteAlreadyFirst_KeepsAdapterZero()
        {
            var choice = OcrAdapterSelector.Choose(
                new[] { Nvidia(0), Intel(1), Basic(2) },
                new[] { NvidiaLuid, IntelLuid, BasicLuid },
                DxgiGpuPreference.HighPerformance);

            Assert.AreEqual(0, choice.Index);
        }

        [TestMethod]
        public void PowerSavingRanking_IsFollowedEvenWhenItIsNotAdapterZero()
        {
            // Whatever order EnumAdapters1 uses, the ranking Windows returns decides.
            var choice = OcrAdapterSelector.Choose(
                new[] { Nvidia(0), Intel(1), Basic(2) },
                new[] { IntelLuid, NvidiaLuid, BasicLuid },
                DxgiGpuPreference.MinimumPower);

            Assert.AreEqual(1, choice.Index);
        }

        [TestMethod]
        public void NoRanking_KeepsAdapterZero()
        {
            var choice = OcrAdapterSelector.Choose(
                new[] { Intel(0), Nvidia(1) }, null, DxgiGpuPreference.HighPerformance);

            Assert.AreEqual(0, choice.Index);
        }

        [TestMethod]
        public void NoAdapters_KeepsAdapterZero()
        {
            Assert.AreEqual(0, OcrAdapterSelector.Choose(
                new GpuAdapterInfo[0], new[] { NvidiaLuid }, DxgiGpuPreference.HighPerformance).Index);
            Assert.AreEqual(0, OcrAdapterSelector.Choose(
                null, null, DxgiGpuPreference.HighPerformance).Index);
        }

        [TestMethod]
        public void RankingSkipsSoftwareAndUnknownAdapters()
        {
            var choice = OcrAdapterSelector.Choose(
                new[] { Intel(0), Nvidia(1), Basic(2) },
                new[] { 0x9999L, BasicLuid, NvidiaLuid },
                DxgiGpuPreference.HighPerformance);

            Assert.AreEqual(1, choice.Index);
        }

        [TestMethod]
        public void SoftwareOnly_KeepsAdapterZero()
        {
            var choice = OcrAdapterSelector.Choose(
                new[] { Basic(0) }, new[] { BasicLuid }, DxgiGpuPreference.HighPerformance);

            Assert.AreEqual(0, choice.Index);
        }

        [TestMethod]
        [DataRow("GpuPreference=1;", 1)]
        [DataRow("GpuPreference=2;", 2)]
        [DataRow("GpuPreference=0;", 0)]
        [DataRow("SwapEffectUpgradeEnable=1;GpuPreference=2;", 2)]
        [DataRow("GpuPreference=1;AutoHDREnable=1;", 1)]
        public void UserGpuPreference_IsParsedFromTheRegistryValue(string value, int expected)
        {
            Assert.AreEqual(expected, OcrAdapterSelector.ParseUserGpuPreference(value));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("SwapEffectUpgradeEnable=1;")]
        [DataRow("GpuPreference=;")]
        public void UserGpuPreference_AbsentIsNull(string value)
        {
            Assert.IsNull(OcrAdapterSelector.ParseUserGpuPreference(value));
        }

        [TestMethod]
        public void PowerSavingSetByTheUser_IsHonoured_EverythingElsePrefersPerformance()
        {
            Assert.AreEqual(DxgiGpuPreference.MinimumPower, OcrAdapterSelector.PreferenceFor(1));
            Assert.AreEqual(DxgiGpuPreference.HighPerformance, OcrAdapterSelector.PreferenceFor(2));
            Assert.AreEqual(DxgiGpuPreference.HighPerformance, OcrAdapterSelector.PreferenceFor(0));
            Assert.AreEqual(DxgiGpuPreference.HighPerformance, OcrAdapterSelector.PreferenceFor(null));
        }

        [TestMethod]
        public void SoftwareAdapters_AreTheOnesOnnxRuntimeRefuses()
        {
            Assert.IsTrue(GpuAdapterInfo.LooksSoftware(flags: 2, vendorId: 0x10DE, deviceId: 1));
            Assert.IsTrue(GpuAdapterInfo.LooksSoftware(flags: 0, vendorId: 0x1414, deviceId: 0x8C));
            Assert.IsFalse(GpuAdapterInfo.LooksSoftware(flags: 0, vendorId: 0x8086, deviceId: 0x9A60));
            Assert.IsFalse(GpuAdapterInfo.LooksSoftware(flags: 0, vendorId: 0x1414, deviceId: 0x1));
        }

        // ── Contracts against this machine's DXGI and ONNX Runtime ──────────────

        [TestMethod]
        public void Dxgi_RankingListsTheSameAdaptersAsEnumAdapters1()
        {
            var adapters = DxgiScreenCapture.EnumerateAdapters();
            if (adapters.Count == 0) Assert.Inconclusive("DXGI listed no adapters on this machine.");
            // EnumAdapters1 order; an adapter whose description fails is skipped, so only "increasing" is guaranteed.
            for (int i = 1; i < adapters.Count; i++)
                Assert.IsTrue(adapters[i].Index > adapters[i - 1].Index, string.Join(" · ", adapters));

            foreach (var preference in new[] { DxgiGpuPreference.HighPerformance, DxgiGpuPreference.MinimumPower })
            {
                var ranking = DxgiScreenCapture.EnumerateAdapterLuidsByPreference(preference);
                if (ranking == null) Assert.Inconclusive("IDXGIFactory6 is not available on this machine.");
                // A wrong vtable slot would crash or return garbage; the same adapter set proves the call.
                CollectionAssert.AreEquivalent(adapters.Select(a => a.Luid).ToList(), ranking.ToList(), preference.ToString());
            }
        }

        [TestMethod]
        public void Dxgi_ChoiceForThisProcess_IsAHardwareAdapter()
        {
            var adapters = DxgiScreenCapture.EnumerateAdapters();
            if (!adapters.Any(a => !a.IsSoftware)) Assert.Inconclusive("No hardware GPU on this machine.");

            var choice = OcrAdapterSelector.ChooseForThisProcess();

            GpuAdapterInfo chosen = adapters.Single(a => a.Index == choice.Index);
            Assert.IsFalse(chosen.IsSoftware, $"{chosen}: {choice.Reason}");
        }

        [TestMethod]
        [TestCategory("DirectML")]
        public void Ort_DirectMLDeviceId_IsAnEnumAdapters1Index()
        {
            // dml_provider_factory.cc (v1.24.4) turns device_id into EnumAdapters1(device_id) and refuses
            // the software adapter. If that ever changes, OcrAdapterSelector's index means something else.
            if (!DirectMLTestSupport.TryEnable(out string why)) Assert.Inconclusive(why);
            var adapters = DxgiScreenCapture.EnumerateAdapters();
            GpuAdapterInfo software = adapters.FirstOrDefault(a => a.IsSoftware);
            GpuAdapterInfo hardware = adapters.FirstOrDefault(a => !a.IsSoftware);
            if (software == null || hardware == null)
                Assert.Inconclusive("Needs one hardware and one software adapter (the Basic Render Driver).");

            using (var options = new SessionOptions())
                options.AppendExecutionProvider_DML(hardware.Index);

            using var refused = new SessionOptions();
            Assert.Throws<OnnxRuntimeException>(() => refused.AppendExecutionProvider_DML(software.Index));
        }
    }
}
