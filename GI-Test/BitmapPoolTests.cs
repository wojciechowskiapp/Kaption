using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using GI_Subtitles.Core.Pooling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test
{
    /// <summary>
    /// Unit tests for <see cref="BitmapPool"/>. Covers rent/return/evict/dispose contract
    /// used by the OCR capture hot path.
    /// </summary>
    [TestClass]
    public class BitmapPoolTests
    {
        [TestMethod]
        public void Rent_Returns_Bitmap_With_Correct_Shape()
        {
            using (var pool = new BitmapPool())
            {
                var bmp = pool.Rent(64, 32, PixelFormat.Format32bppArgb);
                try
                {
                    Assert.IsNotNull(bmp);
                    Assert.AreEqual(64, bmp.Width);
                    Assert.AreEqual(32, bmp.Height);
                    Assert.AreEqual(PixelFormat.Format32bppArgb, bmp.PixelFormat);
                }
                finally
                {
                    pool.Return(bmp);
                }
            }
        }

        [TestMethod]
        public void Return_Then_Rent_Same_Shape_Reuses_Instance()
        {
            using (var pool = new BitmapPool())
            {
                var first = pool.Rent(100, 50);
                pool.Return(first);

                var second = pool.Rent(100, 50);
                try
                {
                    Assert.AreSame(first, second, "Pool should hand the same bitmap back for the same shape.");
                }
                finally
                {
                    pool.Return(second);
                }
            }
        }

        [TestMethod]
        public void Different_Shapes_Use_Different_Buckets()
        {
            using (var pool = new BitmapPool())
            {
                var a = pool.Rent(100, 50);
                var b = pool.Rent(200, 100);
                pool.Return(a);
                pool.Return(b);

                Assert.AreEqual(2, pool.BucketCount);
                Assert.AreEqual(2, pool.Count);
            }
        }

        [TestMethod]
        public void Return_Respects_MaxPerKey_Cap()
        {
            using (var pool = new BitmapPool())
            {
                // Create more than MaxPerKey bitmaps of the same shape and return them all.
                int overflow = BitmapPool.MaxPerKey + 3;
                var bitmaps = new Bitmap[overflow];
                for (int i = 0; i < overflow; i++)
                {
                    bitmaps[i] = pool.Rent(16, 16);
                }
                for (int i = 0; i < overflow; i++)
                {
                    pool.Return(bitmaps[i]);
                }

                Assert.IsTrue(pool.Count <= BitmapPool.MaxPerKey,
                    $"Pool must not retain more than MaxPerKey ({BitmapPool.MaxPerKey}); actual={pool.Count}");
            }
        }

        [TestMethod]
        public void Return_Null_Is_Safe()
        {
            using (var pool = new BitmapPool())
            {
                pool.Return(null); // must not throw
                Assert.AreEqual(0, pool.Count);
            }
        }

        [TestMethod]
        public void Dispose_Drains_Pool()
        {
            var pool = new BitmapPool();
            var a = pool.Rent(32, 32);
            var b = pool.Rent(32, 32);
            pool.Return(a);
            pool.Return(b);

            Assert.AreEqual(2, pool.Count);

            pool.Dispose();

            Assert.AreEqual(0, pool.Count);
        }

        [TestMethod]
        public void Rent_After_Dispose_Throws()
        {
            var pool = new BitmapPool();
            pool.Dispose();

            Assert.ThrowsException<ObjectDisposedException>(() => pool.Rent(8, 8));
        }

        [TestMethod]
        public void Return_After_Dispose_Disposes_Argument()
        {
            var pool = new BitmapPool();
            var bmp = pool.Rent(8, 8);
            pool.Dispose();

            // Should not throw, and the bitmap should end up disposed.
            pool.Return(bmp);

            Assert.ThrowsException<ArgumentException>(() =>
            {
                // Accessing a disposed Bitmap's properties throws ArgumentException.
                var _ = bmp.Width;
            });
        }

        [TestMethod]
        public void Concurrent_Rent_Return_Does_Not_Deadlock_Or_Leak()
        {
            using (var pool = new BitmapPool())
            {
                const int iterationsPerTask = 200;
                const int taskCount = 8;

                Parallel.For(0, taskCount, _ =>
                {
                    for (int i = 0; i < iterationsPerTask; i++)
                    {
                        var bmp = pool.Rent(24, 24);
                        pool.Return(bmp);
                    }
                });

                // After bursty concurrent use, bucket count shouldn't exceed the cap.
                Assert.IsTrue(pool.Count <= BitmapPool.MaxPerKey * 2,
                    $"Bucket overflow: {pool.Count}");
            }
        }
    }
}
