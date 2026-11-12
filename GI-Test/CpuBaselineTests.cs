using System;
using GI_Subtitles.Services.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// The CPU check behind the start-up line in app.log, and the one an older build would run before taking an update
    /// that needs a newer CPU. The CPUID bits are Intel's (SDM vol. 2A, CPUID) and the levels Microsoft's .NET 11 list.
    /// </summary>
    [TestClass]
    public class CpuBaselineTests
    {
        // CPUID leaf 1 ECX, leaf 7 EBX and leaf 0x80000001 ECX bits.
        private const int Sse3 = 1 << 0, Ssse3 = 1 << 9, Fma = 1 << 12, Cx16 = 1 << 13, Sse41 = 1 << 19, Sse42 = 1 << 20,
            Movbe = 1 << 22, Popcnt = 1 << 23, OsXsave = 1 << 27, Avx = 1 << 28, F16c = 1 << 29;
        private const int Bmi1 = 1 << 3, Avx2 = 1 << 5, Bmi2 = 1 << 8;
        private const int Lzcnt = 1 << 5;

        private const int V2Leaf1 = Sse3 | Ssse3 | Cx16 | Sse41 | Sse42 | Popcnt;
        private const int HaswellLeaf1 = V2Leaf1 | Fma | Movbe | OsXsave | Avx | F16c;
        private const int HaswellLeaf7 = Bmi1 | Avx2 | Bmi2;

        [TestMethod]
        public void Core2_Penryn_HasSse41ButNotSse42_IsV1()
        {
            var cpu = CpuFeatures.FromCpuid(Sse3 | Ssse3 | Cx16 | Sse41, 0, 0);
            Assert.AreEqual(X86Level.V1, cpu.Level);
            Assert.IsFalse(cpu.Meets(X86Level.V2));
        }

        [TestMethod]
        public void AmdK10_HasPopcntButNotSsse3_IsV1()
        {
            var cpu = CpuFeatures.FromCpuid(Sse3 | Cx16 | Popcnt, 0, Lzcnt);
            Assert.AreEqual(X86Level.V1, cpu.Level);
        }

        [TestMethod]
        public void IvyBridge_AvxWithoutAvx2_IsV2()
        {
            var cpu = CpuFeatures.FromCpuid(V2Leaf1 | OsXsave | Avx | F16c, 0, 0);
            Assert.AreEqual(X86Level.V2, cpu.Level);
            Assert.IsTrue(cpu.Meets(X86Level.V2));
            Assert.IsFalse(cpu.Meets(X86Level.V3));
        }

        [TestMethod]
        public void PentiumCometLake_Sse42Only_IsV2()
        {
            Assert.AreEqual(X86Level.V2, CpuFeatures.FromCpuid(V2Leaf1 | Movbe, 0, Lzcnt).Level);
        }

        [TestMethod]
        public void Haswell_IsV3()
        {
            var cpu = CpuFeatures.FromCpuid(HaswellLeaf1, HaswellLeaf7, Lzcnt);
            Assert.AreEqual(X86Level.V3, cpu.Level);
            Assert.IsTrue(cpu.Meets(X86Level.V3));
        }

        [TestMethod]
        public void Haswell_WithoutOsAvxState_IsV2()
        {
            Assert.AreEqual(X86Level.V2, CpuFeatures.FromCpuid(HaswellLeaf1 & ~OsXsave, HaswellLeaf7, Lzcnt).Level);
        }

        [TestMethod]
        public void EachV2Bit_IsRequired()
        {
            foreach (int bit in new[] { Sse3, Ssse3, Cx16, Sse41, Sse42, Popcnt })
                Assert.IsFalse(CpuFeatures.FromCpuid(V2Leaf1 & ~bit, 0, 0).MeetsV2, $"bit 0x{bit:X} dropped");
        }

        [TestMethod]
        public void EachV3Bit_IsRequired()
        {
            foreach (int bit in new[] { Fma, Movbe, OsXsave, Avx, F16c })
                Assert.IsFalse(CpuFeatures.FromCpuid(HaswellLeaf1 & ~bit, HaswellLeaf7, Lzcnt).MeetsV3, $"leaf 1 bit 0x{bit:X}");
            foreach (int bit in new[] { Bmi1, Avx2, Bmi2 })
                Assert.IsFalse(CpuFeatures.FromCpuid(HaswellLeaf1, HaswellLeaf7 & ~bit, Lzcnt).MeetsV3, $"leaf 7 bit 0x{bit:X}");
            Assert.IsFalse(CpuFeatures.FromCpuid(HaswellLeaf1, HaswellLeaf7, 0).MeetsV3, "LZCNT");
        }

        [TestMethod]
        public void ThisMachine_RunsTheTestsSoMeetsTheRuntimesBaseline()
        {
            var hw = CpuBaseline.ReadHardware();
            Assert.IsNotNull(hw, "GI-Test runs as an x64 process");
            // .NET 11 refuses to start below v2, so a running test host proves the hardware reports it.
            if (Environment.Version.Major >= 11) Assert.IsTrue(hw.Value.MeetsV2);
            Assert.IsFalse(string.IsNullOrWhiteSpace(CpuBaseline.DescribeCurrent()));
        }

        [TestMethod]
        public void Describe_AnAvx2Cpu_NamesLevelAndRuntimeWithoutAWarning()
        {
            var hw = CpuFeatures.FromCpuid(HaswellLeaf1, HaswellLeaf7, Lzcnt);
            string line = CpuBaseline.Describe("Intel(R) Core(TM) i9-10850K CPU @ 3.60GHz  ", hw, true, true, new Version(11, 0, 0));
            Assert.AreEqual("CPU: Intel(R) Core(TM) i9-10850K CPU @ 3.60GHz; level x86-64-v3; AVX yes, AVX2 yes; runtime .NET 11.0.0", line);
        }

        [TestMethod]
        public void Describe_NoAvx2OnNet11_SaysStartIsSlower()
        {
            var hw = CpuFeatures.FromCpuid(V2Leaf1 | OsXsave | Avx | F16c, 0, 0);
            string line = CpuBaseline.Describe("Intel(R) Core(TM) i5-3570 CPU @ 3.40GHz", hw, true, false, new Version(11, 0, 0));
            StringAssert.StartsWith(line, "CPU: Intel(R) Core(TM) i5-3570 CPU @ 3.40GHz; level x86-64-v2; AVX yes, AVX2 no; runtime .NET 11.0.0.");
            StringAssert.Contains(line, "slower start");
        }

        [TestMethod]
        public void Describe_NoAvx2OnNet10_HasNoWarning()
        {
            // .NET 10's precompiled code targets v2, so such a CPU starts as fast as any other there.
            var hw = CpuFeatures.FromCpuid(V2Leaf1, 0, 0);
            string line = CpuBaseline.Describe("", hw, false, false, new Version(10, 0, 15));
            Assert.AreEqual("CPU: (no name); level x86-64-v2; AVX no, AVX2 no; runtime .NET 10.0.15", line);
        }

        [TestMethod]
        public void Describe_Avx2SwitchedOff_SaysTheHardwareHasIt()
        {
            var hw = CpuFeatures.FromCpuid(HaswellLeaf1, HaswellLeaf7, Lzcnt);
            string line = CpuBaseline.Describe("x", hw, true, false, new Version(11, 0, 0));
            StringAssert.Contains(line, "AVX2 no (the CPU has it; turned off for this process)");
        }

        [TestMethod]
        public void Describe_NoCpuid_SaysUnknown()
        {
            StringAssert.Contains(CpuBaseline.Describe("", null, false, false, new Version(11, 0, 0)), "level unknown");
        }
    }
}
