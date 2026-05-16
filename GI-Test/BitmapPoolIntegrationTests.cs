using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using GI_Subtitles.Core.Pooling;
using GI_Subtitles.Services.Capture;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Integration tests exercising <see cref="BitmapPool"/> wired through the
    /// <see cref="IScreenCapture"/> backends (<see cref="GdiScreenCapture"/>) the way the
    /// OCR hot path does. These tests touch <c>Graphics.CopyFromScreen</c> so they require
    /// a real interactive desktop — skip in headless CI.
    /// </summary>
    [TestClass]
    public class BitmapPoolIntegrationTests
    {
        /// <summary>
        /// Capture a tiny top-left region into a pool-rented Bitmap, verify dimensions and
        /// that at least one non-zero byte was written. Fails if the backend silently dropped
        /// the destination or the pool handed back a wrong-sized instance.
        /// </summary>
        [TestMethod]
        [TestCategory("Integration")]
        [TestCategory("RequiresScreen")]
        public void Capture_Into_Rented_Bitmap_Writes_Pixels()
        {
            using (var pool = new BitmapPool())
            using (var gdi = new GdiScreenCapture())
            {
                const int w = 32;
                const int h = 16;
                var rented = pool.Rent(w, h, PixelFormat.Format32bppArgb);
                try
                {
                    var returned = gdi.CaptureRegionInto(0, 0, w, h, rented);
                    Assert.IsNotNull(returned);
                    Assert.AreSame(rented, returned,
                        "Backend must return the same destination instance it was handed.");
                    Assert.AreEqual(w, returned.Width);
                    Assert.AreEqual(h, returned.Height);
                    Assert.AreEqual(PixelFormat.Format32bppArgb, returned.PixelFormat);

                    // At least one pixel should be non-fully-transparent (any real desktop
                    // content has colour). Read via LockBits to avoid per-pixel GetPixel churn.
                    BitmapData data = returned.LockBits(
                        new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int stride = Math.Abs(data.Stride);
                        byte[] buffer = new byte[stride * h];
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                        bool anyNonZero = false;
                        for (int i = 0; i < buffer.Length; i++)
                        {
                            if (buffer[i] != 0) { anyNonZero = true; break; }
                        }
                        Assert.IsTrue(anyNonZero,
                            "Capture produced an all-zero buffer — the backend likely didn't write into the rented Bitmap.");
                    }
                    finally
                    {
                        returned.UnlockBits(data);
                    }
                }
                finally
                {
                    pool.Return(rented);
                }
            }
        }

        /// <summary>
        /// Return-then-rent on the same shape must hand back the same instance —
        /// this is the whole point of pooling. Regression guard for anyone tempted
        /// to replace the ConcurrentBag with a fresh allocation path.
        /// </summary>
        [TestMethod]
        [TestCategory("Integration")]
        [TestCategory("RequiresScreen")]
        public void Return_Then_Rent_After_Real_Capture_Reuses_Instance()
        {
            using (var pool = new BitmapPool())
            using (var gdi = new GdiScreenCapture())
            {
                const int w = 48;
                const int h = 24;

                var first = pool.Rent(w, h, PixelFormat.Format32bppArgb);
                gdi.CaptureRegionInto(0, 0, w, h, first);
                pool.Return(first);

                var second = pool.Rent(w, h, PixelFormat.Format32bppArgb);
                try
                {
                    Assert.IsTrue(ReferenceEquals(first, second),
                        "Pool should hand back the same instance for the same (w,h,pf) shape.");
                }
                finally
                {
                    pool.Return(second);
                }
            }
        }

        /// <summary>
        /// Stress test: N concurrent Rent/Return cycles on T worker threads, each
        /// performing a real capture into its rented Bitmap. Verifies:
        ///   - No deadlock (finishes within the timeout)
        ///   - Pool respects MaxPerKey after the burst settles
        ///   - No thread produced a null bitmap
        /// </summary>
        [TestMethod]
        [TestCategory("Integration")]
        [TestCategory("RequiresScreen")]
        public void Concurrent_Capture_Stress_Respects_MaxPerKey()
        {
            const int iterationsPerThread = 200;
            const int threads = 2;
            const int w = 20;
            const int h = 10;

            using (var pool = new BitmapPool())
            {
                var errors = new ConcurrentQueue<Exception>();

                Parallel.For(0, threads, t =>
                {
                    try
                    {
                        // Each thread builds its own capture backend — GDI is cheap and
                        // this mirrors real behaviour more closely than sharing one instance.
                        using (var gdi = new GdiScreenCapture())
                        {
                            for (int i = 0; i < iterationsPerThread; i++)
                            {
                                Bitmap rented = pool.Rent(w, h, PixelFormat.Format32bppArgb);
                                try
                                {
                                    var returned = gdi.CaptureRegionInto(0, 0, w, h, rented);
                                    if (returned == null)
                                    {
                                        errors.Enqueue(new InvalidOperationException(
                                            $"thread {t} iter {i}: capture returned null"));
                                        break;
                                    }
                                }
                                finally
                                {
                                    pool.Return(rented);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                });

                if (!errors.IsEmpty)
                {
                    var messages = new System.Collections.Generic.List<string>();
                    foreach (var ex in errors) messages.Add(ex.Message);
                    Assert.Fail("Concurrent capture stress failed: " + string.Join("; ", messages));
                }

                // After the burst settles, the pool must not exceed MaxPerKey for the
                // single shape we used. Allow a small slop factor (2x) because ConcurrentBag
                // has no atomic Count-cap — racing returners can briefly over-fill before
                // SafeDispose catches up.
                Assert.IsTrue(pool.Count <= BitmapPool.MaxPerKey * 2,
                    $"Pool over-filled: Count={pool.Count}, MaxPerKey={BitmapPool.MaxPerKey}");
                Assert.AreEqual(1, pool.BucketCount,
                    "Only one (w,h,pf) bucket should exist.");
            }
        }

        /// <summary>
        /// Dispose must invalidate every Bitmap the pool is still holding — subsequent
        /// property access throws <see cref="ArgumentException"/> on a disposed Bitmap.
        /// </summary>
        [TestMethod]
        public void Dispose_Invalidates_Pooled_Instances()
        {
            var pool = new BitmapPool();
            Bitmap a = pool.Rent(16, 16);
            Bitmap b = pool.Rent(16, 16);
            pool.Return(a);
            pool.Return(b);
            Assert.AreEqual(2, pool.Count);

            pool.Dispose();
            Assert.AreEqual(0, pool.Count);

            Assert.ThrowsException<ArgumentException>(() =>
            {
                var _ = a.Width; // disposed Bitmap throws ArgumentException on any property access
            });
            Assert.ThrowsException<ArgumentException>(() =>
            {
                var _ = b.Width;
            });
        }

        /// <summary>
        /// Sanity check: GDI backend rejects null destinations with
        /// <see cref="ArgumentNullException"/> rather than dereferencing.
        /// </summary>
        [TestMethod]
        public void Backend_Rejects_Null_Destination()
        {
            using (var gdi = new GdiScreenCapture())
            {
                Assert.ThrowsException<ArgumentNullException>(() =>
                    gdi.CaptureRegionInto(0, 0, 8, 8, null));
            }
        }

        /// <summary>
        /// Sanity check: GDI backend rejects destinations smaller than the requested region.
        /// </summary>
        [TestMethod]
        public void Backend_Rejects_Undersized_Destination()
        {
            using (var gdi = new GdiScreenCapture())
            using (var tiny = new Bitmap(4, 4, PixelFormat.Format32bppArgb))
            {
                Assert.ThrowsException<ArgumentException>(() =>
                    gdi.CaptureRegionInto(0, 0, 64, 64, tiny));
            }
        }

        /// <summary>
        /// Sanity check: GDI backend rejects unsupported pixel formats so a caller typo
        /// can't silently produce garbled bytes.
        /// </summary>
        [TestMethod]
        public void Backend_Rejects_Unsupported_PixelFormat()
        {
            using (var gdi = new GdiScreenCapture())
            using (var wrong = new Bitmap(16, 16, PixelFormat.Format24bppRgb))
            {
                Assert.ThrowsException<ArgumentException>(() =>
                    gdi.CaptureRegionInto(0, 0, 16, 16, wrong));
            }
        }
    }
}
