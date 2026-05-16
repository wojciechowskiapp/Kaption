using System;
using System.Threading.Tasks;
using GI_Subtitles.Core.Pooling;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace GI_Test
{
    /// <summary>
    /// Unit tests for <see cref="MatPool"/>. Covers the shape-keyed rent/return contract and
    /// native-resource cleanup used by the OCR diff and preprocessing pipeline.
    /// </summary>
    [TestClass]
    public class MatPoolTests
    {
        [TestMethod]
        public void Rent_Returns_Mat_With_Correct_Shape()
        {
            using (var pool = new MatPool())
            {
                using (var mat = pool.Rent(64, 32, MatType.CV_8UC3))
                {
                    Assert.IsNotNull(mat);
                    Assert.AreEqual(64, mat.Rows);
                    Assert.AreEqual(32, mat.Cols);
                    Assert.AreEqual(MatType.CV_8UC3, mat.Type());
                    Assert.IsFalse(mat.IsDisposed);
                }
            }
        }

        [TestMethod]
        public void Return_Then_Rent_Same_Shape_Reuses_Instance()
        {
            using (var pool = new MatPool())
            {
                var first = pool.Rent(80, 40, MatType.CV_8UC1);
                pool.Return(first);

                var second = pool.Rent(80, 40, MatType.CV_8UC1);
                try
                {
                    Assert.AreSame(first, second);
                    Assert.IsFalse(second.IsDisposed);
                }
                finally
                {
                    pool.Return(second);
                }
            }
        }

        [TestMethod]
        public void RentBlank_Returns_Empty_Mat()
        {
            using (var pool = new MatPool())
            {
                using (var mat = pool.RentBlank())
                {
                    Assert.IsNotNull(mat);
                    // OpenCvSharp treats default Mat as 0x0.
                    Assert.AreEqual(0, mat.Rows);
                    Assert.AreEqual(0, mat.Cols);
                }
            }
        }

        [TestMethod]
        public void RentBlank_Round_Trip_Preserves_Shape_Key_After_Op()
        {
            using (var pool = new MatPool())
            {
                // Simulate the production pattern: rent blank, Cv2 op allocates inside,
                // then return — subsequent RentBlank shouldn't keep reusing a shaped Mat.
                var blank = pool.RentBlank();
                using (var a = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(255)))
                using (var b = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(0)))
                {
                    Cv2.Absdiff(a, b, blank);
                }
                Assert.AreEqual(10, blank.Rows);
                pool.Return(blank);

                // Next RentBlank should NOT give us the shaped Mat — keyed by shape now.
                var blank2 = pool.RentBlank();
                try
                {
                    Assert.AreNotSame(blank, blank2);
                    Assert.AreEqual(0, blank2.Rows);
                }
                finally
                {
                    pool.Return(blank2);
                }
            }
        }

        [TestMethod]
        public void Return_Respects_MaxPerKey_Cap()
        {
            using (var pool = new MatPool())
            {
                int overflow = MatPool.MaxPerKey + 3;
                var mats = new Mat[overflow];
                for (int i = 0; i < overflow; i++)
                {
                    mats[i] = pool.Rent(16, 16, MatType.CV_8UC1);
                }
                for (int i = 0; i < overflow; i++)
                {
                    pool.Return(mats[i]);
                }

                Assert.IsTrue(pool.Count <= MatPool.MaxPerKey,
                    $"Pool exceeded MaxPerKey={MatPool.MaxPerKey}: actual={pool.Count}");

                // Surplus Mats should be disposed — verify the 3 non-cached ones.
                int disposedCount = 0;
                for (int i = 0; i < overflow; i++)
                {
                    if (mats[i].IsDisposed) disposedCount++;
                }
                Assert.IsTrue(disposedCount >= overflow - MatPool.MaxPerKey,
                    $"Expected at least {overflow - MatPool.MaxPerKey} surplus mats disposed, actual={disposedCount}");
            }
        }

        [TestMethod]
        public void Return_Null_Is_Safe()
        {
            using (var pool = new MatPool())
            {
                pool.Return(null);
                Assert.AreEqual(0, pool.Count);
            }
        }

        [TestMethod]
        public void Return_Already_Disposed_Mat_Is_Safe()
        {
            using (var pool = new MatPool())
            {
                var mat = pool.Rent(8, 8, MatType.CV_8UC1);
                mat.Dispose();

                pool.Return(mat); // must not throw, must not re-cache

                Assert.AreEqual(0, pool.Count);
            }
        }

        [TestMethod]
        public void Dispose_Releases_Native_Buffers()
        {
            var pool = new MatPool();
            var a = pool.Rent(32, 32, MatType.CV_8UC1);
            var b = pool.Rent(32, 32, MatType.CV_8UC1);
            pool.Return(a);
            pool.Return(b);

            Assert.AreEqual(2, pool.Count);

            pool.Dispose();

            Assert.AreEqual(0, pool.Count);
            Assert.IsTrue(a.IsDisposed, "Pooled Mat 'a' should be disposed on pool Dispose.");
            Assert.IsTrue(b.IsDisposed, "Pooled Mat 'b' should be disposed on pool Dispose.");
        }

        [TestMethod]
        public void Rent_After_Dispose_Throws()
        {
            var pool = new MatPool();
            pool.Dispose();

            Assert.ThrowsException<ObjectDisposedException>(() => pool.Rent(8, 8, MatType.CV_8UC1));
            Assert.ThrowsException<ObjectDisposedException>(() => pool.RentBlank());
        }

        [TestMethod]
        public void Return_After_Dispose_Disposes_Argument()
        {
            var pool = new MatPool();
            var mat = pool.Rent(8, 8, MatType.CV_8UC1);
            pool.Dispose();

            pool.Return(mat);
            Assert.IsTrue(mat.IsDisposed);
        }

        [TestMethod]
        public void Concurrent_Rent_Return_Stays_Within_Cap()
        {
            using (var pool = new MatPool())
            {
                const int iterationsPerTask = 150;
                const int taskCount = 8;

                Parallel.For(0, taskCount, _ =>
                {
                    for (int i = 0; i < iterationsPerTask; i++)
                    {
                        var m = pool.Rent(20, 20, MatType.CV_8UC1);
                        pool.Return(m);
                    }
                });

                // Bag.Count race permits brief overshoot; cap at 2x for test stability.
                Assert.IsTrue(pool.Count <= MatPool.MaxPerKey * 2,
                    $"Pool overfilled under contention: {pool.Count}");
            }
        }
    }
}
