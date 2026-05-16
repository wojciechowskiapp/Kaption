using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Capture
{
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
        private int _outputWidth;
        private int _outputHeight;
        private bool _initialized;
        private bool _disposed;

        /// <summary>
        /// True if the last AcquireNextFrame returned a new frame.
        /// False if timed out (no desktop change). Callers can skip
        /// OCR preprocessing when no frame change is detected.
        /// </summary>
        public bool LastFrameWasNew { get; private set; }

        public bool IsAvailable => _initialized;

        public DxgiScreenCapture(int adapterIndex = 0, int outputIndex = 0)
        {
            try
            {
                Initialize(adapterIndex, outputIndex);
                _initialized = true;
                Logger.Log.Info($"DXGI Desktop Duplication initialized ({_outputWidth}x{_outputHeight})");
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
                    LastFrameWasNew = false;
                    return _stagingTexture != IntPtr.Zero;
                }

                if (hr < 0)
                {
                    if (hr == DXGI_ERROR_ACCESS_LOST)
                    {
                        Logger.Log.Warn("DXGI access lost — reinitializing");
                        Cleanup();
                        try { Initialize(0, 0); _initialized = true; }
                        catch { _initialized = false; }
                    }
                    return false;
                }

                LastFrameWasNew = true;

                IntPtr desktopTexture = IntPtr.Zero;
                hr = IUnknown_QueryInterface(desktopResource, ref IID_ID3D11Texture2D, out desktopTexture);
                if (hr >= 0 && desktopTexture != IntPtr.Zero)
                {
                    ID3D11DeviceContext_CopyResource(_context, _stagingTexture, desktopTexture);
                    Marshal.Release(desktopTexture);
                }

                IDXGIOutputDuplication_ReleaseFrame(_duplication);
                Marshal.Release(desktopResource);
                desktopResource = IntPtr.Zero;
                return _stagingTexture != IntPtr.Zero;
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
                // Clamp region to output bounds
                int sx = Math.Max(0, Math.Min(x, _outputWidth - 1));
                int sy = Math.Max(0, Math.Min(y, _outputHeight - 1));
                int sw = Math.Min(width, _outputWidth - sx);
                int sh = Math.Min(height, _outputHeight - sy);
                if (sw <= 0 || sh <= 0) return null;

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
                            _outputWidth = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
                            _outputHeight = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;
                            Logger.Log.Debug($"DXGI output: {_outputWidth}x{_outputHeight}");
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
            if (_duplication != IntPtr.Zero) { Marshal.Release(_duplication); _duplication = IntPtr.Zero; }
            if (_stagingTexture != IntPtr.Zero) { Marshal.Release(_stagingTexture); _stagingTexture = IntPtr.Zero; }
            if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
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
        private delegate int EnumOutputsDelegate(IntPtr self, int index, out IntPtr ppOutput);

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
        // IDXGIAdapter1: 7=EnumOutputs
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

        private static int IDXGIAdapter1_EnumOutputs(IntPtr adapter, int index, out IntPtr ppOutput)
            => GetVtblDelegate<EnumOutputsDelegate>(adapter, 7)(adapter, index, out ppOutput);

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
