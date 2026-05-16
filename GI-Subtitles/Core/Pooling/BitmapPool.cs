using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;

namespace GI_Subtitles.Core.Pooling
{
    /// <summary>
    /// Thread-safe pool of <see cref="Bitmap"/> instances keyed by (width, height, pixelFormat).
    /// Designed for the OCR capture loop, which allocates a new region-sized Bitmap every 100 ms.
    /// </summary>
    /// <remarks>
    /// <para>Per-key bounded capacity (<see cref="MaxPerKey"/>) — extras are disposed on return to
    /// avoid retaining LOH memory for scenarios the app has stopped using.</para>
    /// <para>Callers MUST NOT hold a reference to a returned Bitmap after calling
    /// <see cref="Return"/> — the pool may hand the same instance to another caller.</para>
    /// </remarks>
    public sealed class BitmapPool : IDisposable
    {
        /// <summary>Process-wide default pool. Used by the OCR capture path.</summary>
        public static readonly BitmapPool Default = new BitmapPool();

        /// <summary>Bounded per-key capacity. Extras on return are disposed.</summary>
        public const int MaxPerKey = 4;

        private readonly ConcurrentDictionary<BitmapKey, ConcurrentBag<Bitmap>> _buckets
            = new ConcurrentDictionary<BitmapKey, ConcurrentBag<Bitmap>>();

        private int _disposed;

        /// <summary>
        /// Rent a Bitmap of the requested dimensions and pixel format. Returns a ready-to-use
        /// instance — do NOT dispose; call <see cref="Return"/> when finished.
        /// </summary>
        public Bitmap Rent(int width, int height, PixelFormat pixelFormat = PixelFormat.Format32bppArgb)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            ThrowIfDisposed();

            var key = new BitmapKey(width, height, pixelFormat);
            if (_buckets.TryGetValue(key, out var bag) && bag.TryTake(out var bmp))
            {
                if (bmp != null)
                {
                    return bmp;
                }
            }

            return new Bitmap(width, height, pixelFormat);
        }

        /// <summary>
        /// Return a previously rented Bitmap to the pool. Safe to pass <c>null</c>.
        /// Disposes the instance when the bucket is full or when the pool is disposed.
        /// </summary>
        public void Return(Bitmap bitmap)
        {
            if (bitmap == null)
            {
                return;
            }

            // If pool is being disposed, dispose instance instead of racing.
            if (Volatile.Read(ref _disposed) != 0)
            {
                SafeDispose(bitmap);
                return;
            }

            BitmapKey key;
            try
            {
                key = new BitmapKey(bitmap.Width, bitmap.Height, bitmap.PixelFormat);
            }
            catch
            {
                // Instance already disposed by caller — drop it silently.
                return;
            }

            var bag = _buckets.GetOrAdd(key, _ => new ConcurrentBag<Bitmap>());

            // ConcurrentBag has no Count-cap; we approximate with a best-effort check.
            // Racing readers may over-fill slightly, which is acceptable for 4-slot buckets.
            if (bag.Count >= MaxPerKey)
            {
                SafeDispose(bitmap);
                return;
            }

            bag.Add(bitmap);
        }

        /// <summary>Diagnostic — total pooled Bitmaps across all keys.</summary>
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

        /// <summary>Diagnostic — number of distinct (w, h, pf) buckets currently tracked.</summary>
        public int BucketCount => _buckets.Count;

        /// <summary>Drain and dispose every pooled instance. Further calls to <see cref="Return"/> dispose their argument.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var kvp in _buckets)
            {
                while (kvp.Value.TryTake(out var bmp))
                {
                    SafeDispose(bmp);
                }
            }
            _buckets.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(BitmapPool));
            }
        }

        private static void SafeDispose(Bitmap bmp)
        {
            try { bmp?.Dispose(); }
            catch { /* already disposed — ignore */ }
        }

        /// <summary>Composite key so differently-sized captures don't share a bucket.</summary>
        private readonly struct BitmapKey : IEquatable<BitmapKey>
        {
            public readonly int Width;
            public readonly int Height;
            public readonly PixelFormat PixelFormat;

            public BitmapKey(int w, int h, PixelFormat pf)
            {
                Width = w;
                Height = h;
                PixelFormat = pf;
            }

            public bool Equals(BitmapKey other) =>
                Width == other.Width && Height == other.Height && PixelFormat == other.PixelFormat;

            public override bool Equals(object obj) => obj is BitmapKey k && Equals(k);

            public override int GetHashCode()
            {
                // Classic hand-rolled hash — no HashCode.Combine on net48 without System.HashCode.
                unchecked
                {
                    int h = 17;
                    h = h * 31 + Width;
                    h = h * 31 + Height;
                    h = h * 31 + (int)PixelFormat;
                    return h;
                }
            }
        }
    }
}
