using System;
using System.Collections.Concurrent;
using System.Threading;
using OpenCvSharp;

namespace GI_Subtitles.Core.Pooling
{
    /// <summary>
    /// Thread-safe pool of <see cref="Mat"/> instances keyed by (rows, cols, <see cref="MatType"/>).
    /// Each tick in the OCR loop allocates several transient Mats (clones, diff frames, binaries);
    /// reusing them keeps native allocations off the GC's radar and trims working set.
    /// </summary>
    /// <remarks>
    /// <para>Mats wrap unmanaged memory. When a bucket is full on <see cref="Return"/> or the pool
    /// is disposed, the surplus instance is <c>Dispose</c>d to release that native buffer.</para>
    /// <para>Pool entries are NOT kept with pixel data — they are treated as "blank canvases" of the
    /// right shape. Callers must not assume residual content.</para>
    /// </remarks>
    public sealed class MatPool : IDisposable
    {
        /// <summary>Process-wide default pool.</summary>
        public static readonly MatPool Default = new MatPool();

        /// <summary>Bounded per-key capacity. Surplus instances on return are disposed.</summary>
        public const int MaxPerKey = 4;

        private readonly ConcurrentDictionary<MatKey, ConcurrentBag<Mat>> _buckets
            = new ConcurrentDictionary<MatKey, ConcurrentBag<Mat>>();

        private int _disposed;

        /// <summary>
        /// Rent an uninitialised Mat with the requested shape. Never null.
        /// Overload for 0x0 blank Mats is <see cref="RentBlank"/>.
        /// </summary>
        public Mat Rent(int rows, int cols, MatType type)
        {
            if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
            if (cols <= 0) throw new ArgumentOutOfRangeException(nameof(cols));
            ThrowIfDisposed();

            var key = new MatKey(rows, cols, type);
            if (_buckets.TryGetValue(key, out var bag) && bag.TryTake(out var mat))
            {
                if (mat != null && !mat.IsDisposed)
                {
                    return mat;
                }
            }

            return new Mat(rows, cols, type);
        }

        /// <summary>
        /// Rent a blank (0x0) Mat — suitable as an output buffer for OpenCV operations that
        /// allocate the result themselves (e.g. <c>Cv2.Absdiff</c>, <c>Cv2.CvtColor</c>).
        /// After the op completes, the Mat has a real shape; return it with <see cref="Return"/>
        /// and the pool will key by the post-op shape.
        /// </summary>
        public Mat RentBlank()
        {
            ThrowIfDisposed();

            var key = MatKey.Blank;
            if (_buckets.TryGetValue(key, out var bag) && bag.TryTake(out var mat))
            {
                if (mat != null && !mat.IsDisposed)
                {
                    return mat;
                }
            }

            return new Mat();
        }

        /// <summary>
        /// Return a previously rented Mat. Safe to pass null or an already-disposed Mat
        /// (both are dropped silently).
        /// </summary>
        public void Return(Mat mat)
        {
            if (mat == null)
            {
                return;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                SafeDispose(mat);
                return;
            }

            if (mat.IsDisposed)
            {
                return;
            }

            MatKey key;
            try
            {
                // An empty Mat (0 rows or 0 cols) keys as Blank.
                if (mat.Rows == 0 || mat.Cols == 0)
                {
                    key = MatKey.Blank;
                }
                else
                {
                    key = new MatKey(mat.Rows, mat.Cols, mat.Type());
                }
            }
            catch
            {
                SafeDispose(mat);
                return;
            }

            var bag = _buckets.GetOrAdd(key, _ => new ConcurrentBag<Mat>());

            if (bag.Count >= MaxPerKey)
            {
                SafeDispose(mat);
                return;
            }

            bag.Add(mat);
        }

        /// <summary>Diagnostic — total pooled Mats across all keys.</summary>
        public int Count
        {
            get
            {
                int total = 0;
                foreach (var bag in _buckets.Values)
                {
                    total += bag.Count;
                }
                return total;
            }
        }

        /// <summary>Diagnostic — number of distinct buckets currently tracked.</summary>
        public int BucketCount => _buckets.Count;

        /// <summary>Drain and dispose every pooled Mat. Further <see cref="Return"/> calls dispose their argument.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var kvp in _buckets)
            {
                while (kvp.Value.TryTake(out var mat))
                {
                    SafeDispose(mat);
                }
            }
            _buckets.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(MatPool));
            }
        }

        private static void SafeDispose(Mat mat)
        {
            try
            {
                if (mat != null && !mat.IsDisposed)
                {
                    mat.Dispose();
                }
            }
            catch { /* already disposed — ignore */ }
        }

        /// <summary>Composite key — separates buckets by shape and pixel type.</summary>
        private readonly struct MatKey : IEquatable<MatKey>
        {
            /// <summary>Shape-less key used for 0x0 "blank" Mats.</summary>
            public static readonly MatKey Blank = default;

            public readonly int Rows;
            public readonly int Cols;
            public readonly int TypeValue;

            public MatKey(int rows, int cols, MatType type)
            {
                Rows = rows;
                Cols = cols;
                TypeValue = (int)type;
            }

            public bool Equals(MatKey other) =>
                Rows == other.Rows && Cols == other.Cols && TypeValue == other.TypeValue;

            public override bool Equals(object obj) => obj is MatKey k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + Rows;
                    h = h * 31 + Cols;
                    h = h * 31 + TypeValue;
                    return h;
                }
            }
        }
    }
}
