using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Capture
{
    /// <summary>
    /// The requested capture rectangle does not live on the DXGI output this
    /// backend duplicates — a game on the secondary monitor, in practice.
    /// Distinct from the generic <see cref="InvalidOperationException"/> the
    /// backend throws for a dead device, because this one is permanent for the
    /// current region: the caller must switch to GDI and stay there instead of
    /// re-probing DXGI on a timer.
    /// </summary>
    public sealed class DxgiRegionOutsideOutputException : InvalidOperationException
    {
        public DxgiRegionOutsideOutputException(string message) : base(message) { }
    }

    /// <summary>
    /// DXGI Desktop Duplication-based screen capture.
    /// Uses GPU-side frame copy — significantly faster than GDI CopyFromScreen.
    ///
    /// Key advantages over GDI:
    ///   - ~0.5-2ms capture vs 5-15ms (GPU texture copy, not CPU BitBlt)
    ///   - Properly respects WDA_EXCLUDEFROMCAPTURE (overlay invisible in captures)
    ///   - Can detect "no frame change" via AcquireNextFrame timeout (zero-cost stability check)
    ///   - Near-zero CPU during stable frames
    ///
    /// Constraints:
    ///   - Only one app can hold Desktop Duplication per output at a time
    ///   - Requires Windows 8+ and WDDM 1.2+ driver
    ///   - Captures from a single monitor (primary by default)
    ///
    /// Falls back gracefully: IsAvailable = false if init fails.
    /// </summary>
    public sealed class DxgiScreenCapture : IScreenCapture
    {
        private IntPtr _device;
        private IntPtr _context;
        private IntPtr _duplication;
        private IntPtr _stagingTexture;
        private readonly int _adapterIndex;
        private readonly int _outputIndex;
        private int _outputLeft;
        private int _outputTop;
        private int _outputWidth;
        private int _outputHeight;
        private bool _initialized;
        private bool _disposed;
        private bool _hasValidFrame;

        /// <summary>
        /// True if the last AcquireNextFrame returned a new frame.
        /// False if timed out (no desktop change). Callers can skip
        /// OCR preprocessing when no frame change is detected.
        /// </summary>
        public bool LastFrameWasNew { get; private set; }

        public bool IsAvailable => _initialized;

        public DxgiScreenCapture(int adapterIndex = 0, int outputIndex = 0)
        {
            _adapterIndex = adapterIndex;
            _outputIndex = outputIndex;
            try
            {
                Initialize(adapterIndex, outputIndex);
                _initialized = true;
                Logger.Log.Info(
                    $"DXGI Desktop Duplication initialized (adapter={adapterIndex}, output={outputIndex}, " +
                    $"bounds={_outputLeft},{_outputTop} {_outputWidth}x{_outputHeight})");
            }
            catch (Exception ex)
            {
                _initialized = false;
                Logger.Log.Warn($"DXGI init failed (will use GDI fallback): {ex.Message}");
                Cleanup();
            }
        }

        /// <summary>
        /// Allocates a fresh Bitmap and captures the region into it. Convenience overload —
        /// the OCR hot path should call <see cref="CaptureRegionInto"/> with a pool-rented
        /// Bitmap to avoid per-tick LOH churn.
        /// </summary>
        public Bitmap CaptureRegion(int x, int y, int width, int height)
        {
            if (!_initialized)
                throw new InvalidOperationException("DXGI not initialized");
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            EnsureRegionBelongsToOutput(x, y, width, height);

            // Acquire the next frame (updates the staging texture in-place).
            if (!AcquireFrameIntoStaging()) return null;

            // Allocate the destination only after we know we have a frame to copy.
            Bitmap bitmap = null;
            try
            {
                bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var result = CopyStagingIntoBitmap(x, y, width, height, bitmap);
                if (result == null)
                {
                    bitmap.Dispose();
                    return null;
                }
                return result;
            }
            catch
            {
                bitmap?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Captures the region into the provided Bitmap. The destination's pixel format MUST
        /// be <see cref="PixelFormat.Format32bppArgb"/> — DXGI delivers BGRA8 bytes which is
        /// the in-memory byte order for that format on little-endian Windows. No byte swap is
        /// performed; a non-ARGB destination will throw.
        /// </summary>
        public Bitmap CaptureRegionInto(int x, int y, int width, int height, Bitmap destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!_initialized)
                throw new InvalidOperationException("DXGI not initialized");
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            if (destination.Width < width || destination.Height < height)
                throw new ArgumentException(
                    $"Destination Bitmap too small: got {destination.Width}x{destination.Height}, need >= {width}x{height}",
                    nameof(destination));

            // DXGI staging is DXGI_FORMAT_B8G8R8A8_UNORM — the in-memory byte order of
            // PixelFormat.Format32bppArgb on little-endian Windows. Any other format would
            // require a byte swap we don't (yet) implement.
            if (destination.PixelFormat != PixelFormat.Format32bppArgb)
            {
                throw new ArgumentException(
                    $"Destination Bitmap pixel format {destination.PixelFormat} is not supported by DXGI capture; use Format32bppArgb.",
                    nameof(destination));
            }

            EnsureRegionBelongsToOutput(x, y, width, height);

            if (!AcquireFrameIntoStaging()) return null;

            return CopyStagingIntoBitmap(x, y, width, height, destination);
        }

        /// <summary>
        /// Acquires the next duplicated frame (or reuses the last on timeout) and copies it
        /// to the staging texture. Returns true when the staging texture holds usable pixels.
        /// On <c>DXGI_ERROR_ACCESS_LOST</c> the backend reinitializes; subsequent ticks retry.
        /// </summary>
        private bool AcquireFrameIntoStaging()
        {
            IntPtr desktopResource = IntPtr.Zero;
            DXGI_OUTDUPL_FRAME_INFO frameInfo;

            try
            {
                int hr = IDXGIOutputDuplication_AcquireNextFrame(
                    _duplication, 16, out frameInfo, out desktopResource);

                if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    // No frame change — staging still holds the previous desktop content.
                    // Before the first successful CopyResource the texture is uninitialised,
                    // so it must never be exposed to OCR as a valid desktop frame.
                    LastFrameWasNew = false;
                    return _hasValidFrame;
                }

                if (hr < 0)
                {
                    if (hr == DXGI_ERROR_ACCESS_LOST)
                    {
                        Logger.Log.Warn("DXGI access lost — reinitializing");
                        Cleanup();
                        try { Initialize(_adapterIndex, _outputIndex); _initialized = true; }
                        catch { _initialized = false; }
                    }
                    else
                    {
                        Logger.Log.Warn($"DXGI AcquireNextFrame failed: 0x{hr:X8}");
                    }
                    return false;
                }

                LastFrameWasNew = true;

                IntPtr desktopTexture = IntPtr.Zero;
                hr = IUnknown_QueryInterface(desktopResource, ref IID_ID3D11Texture2D, out desktopTexture);
                bool copied = false;
                if (hr >= 0 && desktopTexture != IntPtr.Zero)
                {
                    ID3D11DeviceContext_CopyResource(_context, _stagingTexture, desktopTexture);
                    copied = true;
                    _hasValidFrame = true;
                    Marshal.Release(desktopTexture);
                }
                else
                {
                    Logger.Log.Warn($"DXGI frame texture query failed: 0x{hr:X8}");
                }

                IDXGIOutputDuplication_ReleaseFrame(_duplication);
                Marshal.Release(desktopResource);
                desktopResource = IntPtr.Zero;
                return copied;
            }
            catch (Exception ex)
            {
                if (desktopResource != IntPtr.Zero)
                {
                    try { IDXGIOutputDuplication_ReleaseFrame(_duplication); } catch { }
                    Marshal.Release(desktopResource);
                }
                Logger.Log.Error($"DXGI capture error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Copies another region out of the frame the last capture acquired, without asking the desktop for a new one.
        /// <see cref="CaptureRegionInto"/> waits up to a frame (16 ms) for the next one, which is what the dialogue
        /// capture wants once per tick and what a second, unhurried reader on the same tick must not pay again.
        /// Null before the first frame; the same argument rules as <see cref="CaptureRegionInto"/>.
        /// </summary>
        public Bitmap CopyFromHeldFrame(int x, int y, int width, int height, Bitmap destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (destination.Width < width || destination.Height < height || destination.PixelFormat != PixelFormat.Format32bppArgb)
                throw new ArgumentException("Destination Bitmap must be Format32bppArgb and at least the region's size.", nameof(destination));
            if (!_initialized || !_hasValidFrame) return null;
            EnsureRegionBelongsToOutput(x, y, width, height);
            return CopyStagingIntoBitmap(x, y, width, height, destination);
        }

        /// <summary>
        /// Maps the staging texture and memcpys the requested region into <paramref name="destination"/>.
        /// Region is clamped to output bounds — if the clamped region is empty, returns null
        /// without touching the destination.
        /// </summary>
        private Bitmap CopyStagingIntoBitmap(int x, int y, int width, int height, Bitmap destination)
        {
            if (_stagingTexture == IntPtr.Zero) return null;

            D3D11_MAPPED_SUBRESOURCE mapped;
            int hr = ID3D11DeviceContext_Map(_context, _stagingTexture, 0,
                D3D11_MAP_READ, 0, out mapped);
            if (hr < 0) return null;

            try
            {
                // Desktop Duplication surfaces are output-local, while the region picker
                // stores absolute virtual-desktop coordinates. Translate instead of
                // clamping: clamping a second-monitor X to the rightmost primary-monitor
                // pixel used to return a mostly stale bitmap and feed random desktop noise
                // into OCR. Regions outside this output are rejected earlier and make the
                // caller use its correctness-first GDI fallback.
                if (!TryTranslateRegionToOutput(
                    x, y, width, height,
                    _outputLeft, _outputTop, _outputWidth, _outputHeight,
                    out int sx, out int sy))
                {
                    throw new DxgiRegionOutsideOutputException(
                        $"Capture region {x},{y} {width}x{height} is outside DXGI output " +
                        $"{_outputLeft},{_outputTop} {_outputWidth}x{_outputHeight}.");
                }
                int sw = width;
                int sh = height;

                // Destination may be oversized (pool bucket granularity) — lock only the
                // region we're about to overwrite.
                var bmpData = destination.LockBits(
                    new Rectangle(0, 0, sw, sh),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                try
                {
                    unsafe
                    {
                        byte* srcBase = (byte*)mapped.pData + sy * mapped.RowPitch + sx * 4;
                        byte* dstBase = (byte*)bmpData.Scan0;
                        int copyBytes = sw * 4;

                        for (int row = 0; row < sh; row++)
                        {
                            Buffer.MemoryCopy(
                                srcBase + row * mapped.RowPitch,
                                dstBase + row * bmpData.Stride,
                                copyBytes, copyBytes);
                        }
                    }
                }
                finally
                {
                    destination.UnlockBits(bmpData);
                }

                return destination;
            }
            finally
            {
                ID3D11DeviceContext_Unmap(_context, _stagingTexture, 0);
            }
        }

        private void Initialize(int adapterIndex, int outputIndex)
        {
            // Create DXGI Factory
            IntPtr factory;
            int hr = CreateDXGIFactory1(ref IID_IDXGIFactory1, out factory);
            if (hr < 0) { Logger.Log.Warn($"CreateDXGIFactory1 failed: 0x{hr:X8}"); Marshal.ThrowExceptionForHR(hr); }

            try
            {
                // Get adapter
                IntPtr adapter;
                hr = IDXGIFactory1_EnumAdapters1(factory, adapterIndex, out adapter);
                if (hr < 0) { Logger.Log.Warn($"EnumAdapters1 failed: 0x{hr:X8}"); Marshal.ThrowExceptionForHR(hr); }

                try
                {
                    // Create D3D11 device with feature level 11.0
                    int[] featureLevels = { 0xb000 }; // D3D_FEATURE_LEVEL_11_0
                    hr = D3D11CreateDevice(adapter, 0, IntPtr.Zero,
                        0, featureLevels, featureLevels.Length, 7, out _device, out _, out _context);
                    if (hr < 0) { Logger.Log.Warn($"D3D11CreateDevice failed: 0x{hr:X8}"); Marshal.ThrowExceptionForHR(hr); }

                    // Get output
                    IntPtr output;
                    hr = IDXGIAdapter1_EnumOutputs(adapter, outputIndex, out output);
                    if (hr < 0) { Logger.Log.Warn($"EnumOutputs failed: 0x{hr:X8}"); Marshal.ThrowExceptionForHR(hr); }

                    try
                    {
                        // Get output description for dimensions
                        // Use Marshal.PtrToStructure for reliable struct marshaling
                        int descSize = Marshal.SizeOf(typeof(DXGI_OUTPUT_DESC));
                        IntPtr descPtr = Marshal.AllocHGlobal(descSize);
                        try
                        {
                            IDXGIOutput_GetDesc(output, descPtr);
                            var outputDesc = (DXGI_OUTPUT_DESC)Marshal.PtrToStructure(descPtr, typeof(DXGI_OUTPUT_DESC));
                            _outputLeft = outputDesc.DesktopCoordinates.Left;
                            _outputTop = outputDesc.DesktopCoordinates.Top;
                            _outputWidth = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
                            _outputHeight = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;
                            Logger.Log.Debug(
                                $"DXGI output: origin={_outputLeft},{_outputTop}, size={_outputWidth}x{_outputHeight}, " +
                                $"rotation={outputDesc.Rotation}");
                        }
                        finally { Marshal.FreeHGlobal(descPtr); }

                        // QI to IDXGIOutput1
                        IntPtr output1;
                        hr = IUnknown_QueryInterface(output, ref IID_IDXGIOutput1, out output1);
                        if (hr < 0) { Logger.Log.Warn($"QI IDXGIOutput1 failed: 0x{hr:X8}"); Marshal.ThrowExceptionForHR(hr); }

                        try
                        {
                            // Create duplication
                            hr = IDXGIOutput1_DuplicateOutput(output1, _device, out _duplication);
                            if (hr < 0) { Logger.Log.Warn($"DuplicateOutput failed: 0x{hr:X8}"); Marshal.ThrowExceptionForHR(hr); }
                        }
                        finally { Marshal.Release(output1); }
                    }
                    finally { Marshal.Release(output); }

                    // Create staging texture for CPU read
                    CreateStagingTexture();
                }
                finally { Marshal.Release(adapter); }
            }
            finally { Marshal.Release(factory); }
        }

        private void CreateStagingTexture()
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)_outputWidth,
                Height = (uint)_outputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1,
                SampleQuality = 0,
                Usage = D3D11_USAGE_STAGING,
                BindFlags = 0,
                CPUAccessFlags = D3D11_CPU_ACCESS_READ,
                MiscFlags = 0
            };

            int hr = ID3D11Device_CreateTexture2D(_device, ref desc, IntPtr.Zero, out _stagingTexture);
            Marshal.ThrowExceptionForHR(hr);
        }

        private void Cleanup()
        {
            _hasValidFrame = false;
            _initialized = false;
            if (_duplication != IntPtr.Zero) { Marshal.Release(_duplication); _duplication = IntPtr.Zero; }
            if (_stagingTexture != IntPtr.Zero) { Marshal.Release(_stagingTexture); _stagingTexture = IntPtr.Zero; }
            if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
        }

        /// <summary>
        /// Best-effort adapter description for diagnostics — "which GPU is this
        /// machine actually running Kaption on" is the first question every
        /// DirectML performance ticket raises, and app.log is all we get.
        /// Reuses the factory/adapter interop already needed for duplication so
        /// no extra dependency is pulled in for one log line.
        ///
        /// Returns false (and logs nothing beyond Debug) whenever the adapter
        /// cannot be enumerated — this must never be able to break engine init.
        /// </summary>
        public static bool TryGetAdapterName(int adapterIndex, out string name)
        {
            name = null;
            IntPtr factory = IntPtr.Zero;
            IntPtr adapter = IntPtr.Zero;
            try
            {
                int hr = CreateDXGIFactory1(ref IID_IDXGIFactory1, out factory);
                if (hr < 0 || factory == IntPtr.Zero) return false;

                hr = IDXGIFactory1_EnumAdapters1(factory, Math.Max(0, adapterIndex), out adapter);
                if (hr < 0 || adapter == IntPtr.Zero) return false;

                int descSize = Marshal.SizeOf(typeof(DXGI_ADAPTER_DESC1));
                IntPtr descPtr = Marshal.AllocHGlobal(descSize);
                try
                {
                    hr = IDXGIAdapter1_GetDesc1(adapter, descPtr);
                    if (hr < 0) return false;

                    var desc = (DXGI_ADAPTER_DESC1)Marshal.PtrToStructure(descPtr, typeof(DXGI_ADAPTER_DESC1));
                    if (string.IsNullOrWhiteSpace(desc.Description)) return false;

                    // DedicatedVideoMemory is SIZE_T; report MB so the line stays
                    // readable and an integrated-GPU misroute is obvious.
                    ulong dedicatedMb = (ulong)desc.DedicatedVideoMemory / (1024UL * 1024UL);
                    name = $"{desc.Description.Trim()} (vendor 0x{desc.VendorId:X4}, " +
                           $"device 0x{desc.DeviceId:X4}, {dedicatedMb} MB dedicated)";
                    return true;
                }
                finally { Marshal.FreeHGlobal(descPtr); }
            }
            catch (Exception ex)
            {
                if (Logger.IsDebugEnabled)
                {
                    Logger.Log.Debug($"DXGI adapter description unavailable: {ex.Message}");
                }
                return false;
            }
            finally
            {
                if (adapter != IntPtr.Zero) Marshal.Release(adapter);
                if (factory != IntPtr.Zero) Marshal.Release(factory);
            }
        }

        /// <summary>Adapters in <c>EnumAdapters1</c> order (DirectML's device ids). Empty when DXGI cannot be asked.</summary>
        internal static IReadOnlyList<GpuAdapterInfo> EnumerateAdapters()
        {
            var adapters = new List<GpuAdapterInfo>();
            IntPtr factory = IntPtr.Zero;
            try
            {
                if (CreateDXGIFactory1(ref IID_IDXGIFactory1, out factory) < 0 || factory == IntPtr.Zero) return adapters;
                for (int index = 0; index < MaxEnumeratedAdapters; index++)
                {
                    // DXGI_ERROR_NOT_FOUND ends the list.
                    if (IDXGIFactory1_EnumAdapters1(factory, index, out IntPtr adapter) < 0 || adapter == IntPtr.Zero) break;
                    try
                    {
                        if (TryDescribeAdapter(adapter, index, out GpuAdapterInfo info)) adapters.Add(info);
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            catch (Exception ex)
            {
                if (Logger.IsDebugEnabled) Logger.Log.Debug($"DXGI adapter enumeration failed: {ex.Message}");
            }
            finally
            {
                if (factory != IntPtr.Zero) Marshal.Release(factory);
            }
            return adapters;
        }

        /// <summary>
        /// Adapter LUIDs in the order Windows ranks them for <paramref name="preference"/>
        /// (<c>IDXGIFactory6::EnumAdapterByGpuPreference</c>, Windows 10 1803+). Null when that interface is missing.
        /// </summary>
        internal static IReadOnlyList<long> EnumerateAdapterLuidsByPreference(DxgiGpuPreference preference)
        {
            IntPtr factory = IntPtr.Zero;
            IntPtr factory6 = IntPtr.Zero;
            try
            {
                if (CreateDXGIFactory1(ref IID_IDXGIFactory1, out factory) < 0 || factory == IntPtr.Zero) return null;
                if (IUnknown_QueryInterface(factory, ref IID_IDXGIFactory6, out factory6) < 0 || factory6 == IntPtr.Zero)
                    return null;

                var luids = new List<long>();
                for (int index = 0; index < MaxEnumeratedAdapters; index++)
                {
                    if (IDXGIFactory6_EnumAdapterByGpuPreference(
                            factory6, index, (int)preference, ref IID_IDXGIAdapter1, out IntPtr adapter) < 0
                        || adapter == IntPtr.Zero) break;
                    try
                    {
                        // Here index is the ranking position, not a device id; only the LUID is used.
                        if (TryDescribeAdapter(adapter, index, out GpuAdapterInfo info)) luids.Add(info.Luid);
                    }
                    finally { Marshal.Release(adapter); }
                }
                return luids;
            }
            catch (Exception ex)
            {
                if (Logger.IsDebugEnabled) Logger.Log.Debug($"DXGI adapter ranking failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (factory6 != IntPtr.Zero) Marshal.Release(factory6);
                if (factory != IntPtr.Zero) Marshal.Release(factory);
            }
        }

        private const int MaxEnumeratedAdapters = 16;

        private static bool TryDescribeAdapter(IntPtr adapter, int index, out GpuAdapterInfo info)
        {
            info = null;
            IntPtr descPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(DXGI_ADAPTER_DESC1)));
            try
            {
                if (IDXGIAdapter1_GetDesc1(adapter, descPtr) < 0) return false;
                var desc = (DXGI_ADAPTER_DESC1)Marshal.PtrToStructure(descPtr, typeof(DXGI_ADAPTER_DESC1));
                long luid = ((long)desc.AdapterLuid.HighPart << 32) | desc.AdapterLuid.LowPart;
                info = new GpuAdapterInfo(
                    index,
                    (desc.Description ?? "").Trim(),
                    desc.VendorId,
                    desc.DeviceId,
                    (ulong)desc.DedicatedVideoMemory / (1024UL * 1024UL),
                    luid,
                    GpuAdapterInfo.LooksSoftware(desc.Flags, desc.VendorId, desc.DeviceId));
                return true;
            }
            finally { Marshal.FreeHGlobal(descPtr); }
        }

        /// <summary>
        /// Convert an absolute virtual-desktop capture rectangle to coordinates local to
        /// one DXGI output. Desktop Duplication exposes one surface per output; it cannot
        /// legally satisfy a rectangle on another monitor or spanning two monitors.
        /// </summary>
        internal static bool TryTranslateRegionToOutput(
            int x, int y, int width, int height,
            int outputLeft, int outputTop, int outputWidth, int outputHeight,
            out int localX, out int localY)
        {
            localX = 0;
            localY = 0;
            if (width <= 0 || height <= 0 || outputWidth <= 0 || outputHeight <= 0)
                return false;

            long right = (long)x + width;
            long bottom = (long)y + height;
            long outputRight = (long)outputLeft + outputWidth;
            long outputBottom = (long)outputTop + outputHeight;

            if (x < outputLeft || y < outputTop || right > outputRight || bottom > outputBottom)
                return false;

            localX = x - outputLeft;
            localY = y - outputTop;
            return true;
        }

        private void EnsureRegionBelongsToOutput(int x, int y, int width, int height)
        {
            if (TryTranslateRegionToOutput(
                x, y, width, height,
                _outputLeft, _outputTop, _outputWidth, _outputHeight,
                out _, out _))
            {
                return;
            }

            throw new DxgiRegionOutsideOutputException(
                $"Capture region {x},{y} {width}x{height} is outside DXGI output " +
                $"{_outputLeft},{_outputTop} {_outputWidth}x{_outputHeight}; use GDI fallback.");
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Cleanup();
                _disposed = true;
            }
        }

        // --- COM interop via vtable calls ---

        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
        private const int D3D11_USAGE_STAGING = 3;
        private const int D3D11_CPU_ACCESS_READ = 0x20000;
        private const int D3D11_MAP_READ = 1;

        private static Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static Guid IID_IDXGIFactory6 = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
        private static Guid IID_IDXGIAdapter1 = new Guid("29038f61-3839-4626-91fd-086879011a05");
        private static Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        private static Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr pAdapter, int DriverType,
            IntPtr Software, uint Flags, int[] pFeatureLevels, int FeatureLevels,
            int SDKVersion, out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

        // COM vtable helpers — call interface methods via function pointers
        private static int CallVtbl(IntPtr obj, int slot, params object[] args)
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            // Simplified — actual vtable calls need delegate caching for production perf
            return 0;
        }

        // Delegate-based vtable calls for the interfaces we need
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid riid, out IntPtr ppvObject);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Delegate(IntPtr self, int index, out IntPtr ppAdapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapterByGpuPreferenceDelegate(
            IntPtr self, int index, int gpuPreference, ref Guid riid, out IntPtr ppvAdapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumOutputsDelegate(IntPtr self, int index, out IntPtr ppOutput);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDesc1Delegate(IntPtr self, IntPtr pDesc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDescDelegate(IntPtr self, IntPtr pDesc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DuplicateOutputDelegate(IntPtr self, IntPtr pDevice, out IntPtr ppOutputDuplication);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AcquireNextFrameDelegate(IntPtr self, int timeoutMs,
            out DXGI_OUTDUPL_FRAME_INFO pFrameInfo, out IntPtr ppDesktopResource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseFrameDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void CopyResourceDelegate(IntPtr self, IntPtr pDstResource, IntPtr pSrcResource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MapDelegate(IntPtr self, IntPtr pResource, int subresource,
            int mapType, int mapFlags, out D3D11_MAPPED_SUBRESOURCE pMappedResource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void UnmapDelegate(IntPtr self, IntPtr pResource, int subresource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateTexture2DDelegate(IntPtr self, ref D3D11_TEXTURE2D_DESC pDesc,
            IntPtr pInitialData, out IntPtr ppTexture2D);

        // Vtable slot numbers (from COM interface definitions)
        // IUnknown: 0=QI, 1=AddRef, 2=Release
        // IDXGIFactory1: 12=EnumAdapters1
        // IDXGIFactory6: 29=EnumAdapterByGpuPreference (Factory2 adds 14-24, 3 adds 25, 4 adds 26-27, 5 adds 28)
        // IDXGIAdapter1: 7=EnumOutputs, 10=GetDesc1
        // IDXGIOutput: 7=GetDesc
        // IDXGIOutput1: 22=DuplicateOutput
        // IDXGIOutputDuplication: 8=AcquireNextFrame, 14=ReleaseFrame
        // ID3D11DeviceContext: 14=Map, 15=Unmap, 47=CopyResource
        // ID3D11Device: 5=CreateTexture2D

        private static T GetVtblDelegate<T>(IntPtr obj, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return (T)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        private static int IUnknown_QueryInterface(IntPtr obj, ref Guid riid, out IntPtr ppv)
            => GetVtblDelegate<QueryInterfaceDelegate>(obj, 0)(obj, ref riid, out ppv);

        private static int IDXGIFactory1_EnumAdapters1(IntPtr factory, int index, out IntPtr ppAdapter)
            => GetVtblDelegate<EnumAdapters1Delegate>(factory, 12)(factory, index, out ppAdapter);

        private static int IDXGIFactory6_EnumAdapterByGpuPreference(
            IntPtr factory6, int index, int gpuPreference, ref Guid riid, out IntPtr ppAdapter)
            => GetVtblDelegate<EnumAdapterByGpuPreferenceDelegate>(factory6, 29)(
                factory6, index, gpuPreference, ref riid, out ppAdapter);

        private static int IDXGIAdapter1_EnumOutputs(IntPtr adapter, int index, out IntPtr ppOutput)
            => GetVtblDelegate<EnumOutputsDelegate>(adapter, 7)(adapter, index, out ppOutput);

        private static int IDXGIAdapter1_GetDesc1(IntPtr adapter, IntPtr descPtr)
            => GetVtblDelegate<GetDesc1Delegate>(adapter, 10)(adapter, descPtr);

        private static void IDXGIOutput_GetDesc(IntPtr output, IntPtr descPtr)
            => GetVtblDelegate<GetDescDelegate>(output, 7)(output, descPtr);

        private static int IDXGIOutput1_DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr ppDuplication)
            => GetVtblDelegate<DuplicateOutputDelegate>(output1, 22)(output1, device, out ppDuplication);

        private static int IDXGIOutputDuplication_AcquireNextFrame(IntPtr dup, int timeoutMs,
            out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource)
            => GetVtblDelegate<AcquireNextFrameDelegate>(dup, 8)(dup, timeoutMs, out info, out resource);

        private static int IDXGIOutputDuplication_ReleaseFrame(IntPtr dup)
            => GetVtblDelegate<ReleaseFrameDelegate>(dup, 14)(dup);

        private static void ID3D11DeviceContext_CopyResource(IntPtr ctx, IntPtr dst, IntPtr src)
            => GetVtblDelegate<CopyResourceDelegate>(ctx, 47)(ctx, dst, src);

        private static int ID3D11DeviceContext_Map(IntPtr ctx, IntPtr resource, int sub,
            int mapType, int flags, out D3D11_MAPPED_SUBRESOURCE mapped)
            => GetVtblDelegate<MapDelegate>(ctx, 14)(ctx, resource, sub, mapType, flags, out mapped);

        private static void ID3D11DeviceContext_Unmap(IntPtr ctx, IntPtr resource, int sub)
            => GetVtblDelegate<UnmapDelegate>(ctx, 15)(ctx, resource, sub);

        private static int ID3D11Device_CreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc,
            IntPtr init, out IntPtr ppTexture)
            => GetVtblDelegate<CreateTexture2DDelegate>(device, 5)(device, ref desc, init, out ppTexture);

        // --- Native structs ---

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public int AccumulatedFrames;
            public int RectsCoalesced;
            public int ProtectedContentMaskedOut;
            public DXGI_OUTDUPL_POINTER_SHAPE_INFO PointerShapeInfo;
            public int TotalMetadataBufferSize;
            public int PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_POINTER_SHAPE_INFO
        {
            public int Type;
            public int Width;
            public int Height;
            public int Pitch;
            public POINT HotSpot;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            // SIZE_T — must be pointer-sized or the struct layout shifts on x64.
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public LUID AdapterLuid;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public RECT DesktopCoordinates;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr pData;
            public int RowPitch;
            public int DepthPitch;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public uint Width;
            public uint Height;
            public uint MipLevels;
            public uint ArraySize;
            public int Format;
            public uint SampleCount;
            public uint SampleQuality;
            public int Usage;
            public uint BindFlags;
            public uint CPUAccessFlags;
            public uint MiscFlags;
        }
    }
}
