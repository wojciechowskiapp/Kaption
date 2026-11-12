using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace GI_Subtitles.Services.Diagnostics
{
    /// <summary>
    /// The x86-64 levels .NET names in its hardware rules: .NET 11 refuses to start below <see cref="V2"/>, and its
    /// precompiled (ReadyToRun) framework code targets <see cref="V3"/>, so a v2 CPU compiles that code again at start.
    /// </summary>
    public enum X86Level
    {
        /// <summary>Not an x86/x64 process, or CPUID could not be read.</summary>
        Unknown = 0,
        /// <summary>CMOV, CX8, SSE, SSE2: every x64 CPU.</summary>
        V1 = 1,
        /// <summary>Adds CX16, POPCNT, SSE3, SSSE3, SSE4.1, SSE4.2 (.NET 11's minimum).</summary>
        V2 = 2,
        /// <summary>Adds AVX, AVX2, BMI1, BMI2, F16C, FMA, LZCNT, MOVBE (.NET 11's ReadyToRun target on Windows).</summary>
        V3 = 3,
    }

    /// <summary>The instruction sets the hardware reports through CPUID, independent of any DOTNET_Enable* switch.</summary>
    public readonly record struct CpuFeatures(
        bool Cx16, bool Popcnt, bool Sse3, bool Ssse3, bool Sse41, bool Sse42,
        bool OsXsave, bool Avx, bool Avx2, bool Bmi1, bool Bmi2, bool F16c, bool Fma, bool Lzcnt, bool Movbe)
    {
        /// <summary>Microsoft's list for x86-64-v2 (the .NET 11 breaking change "Minimum hardware requirements updated").</summary>
        public bool MeetsV2 => Cx16 && Popcnt && Sse3 && Ssse3 && Sse41 && Sse42;

        /// <summary>v3 also needs the OS to save AVX state; OSXSAVE is the CPUID side of that (x64 Windows 7 SP1+ enables it).</summary>
        public bool MeetsV3 => MeetsV2 && OsXsave && Avx && Avx2 && Bmi1 && Bmi2 && F16c && Fma && Lzcnt && Movbe;

        public X86Level Level => MeetsV3 ? X86Level.V3 : MeetsV2 ? X86Level.V2 : X86Level.V1;

        /// <summary>True when this CPU can run a build whose runtime needs <paramref name="required"/>.</summary>
        public bool Meets(X86Level required) => required <= X86Level.V1 || Level >= required;

        /// <summary>
        /// Decodes the CPUID registers that carry these bits: leaf 1 ECX, leaf 7 (sub-leaf 0) EBX and leaf 0x80000001
        /// ECX. A leaf the CPU does not report is passed as 0.
        /// </summary>
        public static CpuFeatures FromCpuid(int leaf1Ecx, int leaf7Ebx, int ext1Ecx) => new(
            Cx16: Bit(leaf1Ecx, 13), Popcnt: Bit(leaf1Ecx, 23), Sse3: Bit(leaf1Ecx, 0), Ssse3: Bit(leaf1Ecx, 9),
            Sse41: Bit(leaf1Ecx, 19), Sse42: Bit(leaf1Ecx, 20), OsXsave: Bit(leaf1Ecx, 27), Avx: Bit(leaf1Ecx, 28),
            Avx2: Bit(leaf7Ebx, 5), Bmi1: Bit(leaf7Ebx, 3), Bmi2: Bit(leaf7Ebx, 8), F16c: Bit(leaf1Ecx, 29),
            Fma: Bit(leaf1Ecx, 12), Lzcnt: Bit(ext1Ecx, 5), Movbe: Bit(leaf1Ecx, 22));

        private static bool Bit(int register, int bit) => (register & (1 << bit)) != 0;
    }

    /// <summary>
    /// What the CPU offers that matters to the .NET runtime Kaption ships, read once at start for the local log. The
    /// same check is what an app on an older runtime would use before taking an update that needs a newer baseline
    /// (inside a .NET 11 build it can never report less than v2: the runtime would not have started).
    /// </summary>
    public static class CpuBaseline
    {
        // Gated on the architecture, not X86Base.IsSupported: DOTNET_EnableHWIntrinsic=0 turns IsSupported false while
        // CpuId still answers (checked on .NET 11.0.0), and that switch is just what a slow-start report may carry.
        private static bool IsX86 => RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;

        /// <summary>The hardware's own answer through CPUID, or null outside x86/x64 or when CPUID is unavailable.</summary>
        public static CpuFeatures? ReadHardware()
        {
            if (!IsX86) return null;
            try
            {
                int maxLeaf = X86Base.CpuId(0, 0).Eax;
                int maxExt = X86Base.CpuId(unchecked((int)0x80000000), 0).Eax;
                int leaf1Ecx = maxLeaf >= 1 ? X86Base.CpuId(1, 0).Ecx : 0;
                int leaf7Ebx = maxLeaf >= 7 ? X86Base.CpuId(7, 0).Ebx : 0;
                int ext1Ecx = (uint)maxExt >= 0x80000001 ? X86Base.CpuId(unchecked((int)0x80000001), 0).Ecx : 0;
                return CpuFeatures.FromCpuid(leaf1Ecx, leaf7Ebx, ext1Ecx);
            }
            catch (PlatformNotSupportedException)
            {
                return null;
            }
        }

        /// <summary>The processor's marketing name (CPUID 0x80000002-4), or "" when the CPU has none.</summary>
        public static string ReadBrand()
        {
            if (!IsX86) return "";
            try
            {
                if ((uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax < 0x80000004) return "";
                var bytes = new byte[48];
                for (int i = 0; i < 3; i++)
                {
                    var (eax, ebx, ecx, edx) = X86Base.CpuId(unchecked((int)(0x80000002 + i)), 0);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16), eax);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 4), ebx);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 8), ecx);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i * 16 + 12), edx);
                }
                return Encoding.ASCII.GetString(bytes).TrimEnd('\0').Trim();
            }
            catch (PlatformNotSupportedException)
            {
                return "";
            }
        }

        /// <summary>The start-up line for the current process.</summary>
        public static string DescribeCurrent() =>
            Describe(ReadBrand(), ReadHardware(), Avx.IsSupported, Avx2.IsSupported, Environment.Version);

        /// <summary>
        /// One line for app.log: the CPU, its level, AVX and AVX2 as the runtime uses them, and the runtime version. On
        /// .NET 11 or newer without AVX2 it says why start-up is slower, so a "slow start" report explains itself.
        /// </summary>
        public static string Describe(string brand, CpuFeatures? hardware, bool runtimeAvx, bool runtimeAvx2, Version runtime)
        {
            var sb = new StringBuilder("CPU: ");
            sb.Append(string.IsNullOrWhiteSpace(brand) ? "(no name)" : brand.Trim());
            sb.Append("; level ").Append(hardware is { } hw ? LevelName(hw.Level) : "unknown");
            sb.Append("; AVX ").Append(YesNo(runtimeAvx, hardware?.Avx));
            sb.Append(", AVX2 ").Append(YesNo(runtimeAvx2, hardware?.Avx2));
            sb.Append("; runtime .NET ").Append(runtime);
            if (!runtimeAvx2 && runtime.Major >= 11)
                sb.Append(". Without AVX2 the runtime compiles its precompiled start-up code again: expect a slower start");
            return sb.ToString();
        }

        public static string LevelName(X86Level level) => level switch
        {
            X86Level.V1 => "x86-64-v1",
            X86Level.V2 => "x86-64-v2",
            X86Level.V3 => "x86-64-v3",
            _ => "unknown",
        };

        // "no (turned off for this process)" when the hardware has it but a DOTNET_Enable* switch or the OS hides it.
        private static string YesNo(bool runtime, bool? hardware) =>
            runtime ? "yes" : hardware == true ? "no (the CPU has it; turned off for this process)" : "no";
    }
}
