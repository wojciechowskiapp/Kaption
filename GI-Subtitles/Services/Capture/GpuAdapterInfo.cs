namespace GI_Subtitles.Services.Capture
{
    /// <summary>
    /// One DXGI adapter, at its position in <c>IDXGIFactory1::EnumAdapters1</c>. That position is what ONNX Runtime's
    /// DirectML device id means (dml_provider_factory.cc, v1.24.4).
    /// </summary>
    internal sealed record GpuAdapterInfo(
        int Index, string Description, uint VendorId, uint DeviceId, ulong DedicatedMb, long Luid, bool IsSoftware)
    {
        /// <summary>ONNX Runtime refuses these for DirectML: the software flag, or the Microsoft Basic Render Driver.</summary>
        internal static bool LooksSoftware(uint flags, uint vendorId, uint deviceId) =>
            (flags & 0x2u) != 0 || (vendorId == 0x1414 && deviceId == 0x8C);

        public override string ToString() =>
            $"[{Index}] {Description} (vendor 0x{VendorId:X4}, device 0x{DeviceId:X4}, {DedicatedMb} MB dedicated" +
            (IsSoftware ? ", software)" : ")");
    }

    /// <summary><c>DXGI_GPU_PREFERENCE</c>, the ranking <c>IDXGIFactory6::EnumAdapterByGpuPreference</c> returns.</summary>
    internal enum DxgiGpuPreference
    {
        Unspecified = 0,
        MinimumPower = 1,
        HighPerformance = 2,
    }
}
