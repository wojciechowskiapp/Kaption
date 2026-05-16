using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using ZstdSharp;

namespace GI_Subtitles.Services.Translation
{
    /// <summary>
    /// Thread-safe zstd value decoder with an optional trained dictionary.
    /// Maintains a bounded pool of native <see cref="Decompressor"/> instances;
    /// each call to <see cref="Decode"/> rents one, decompresses, and returns
    /// it. The dictionary bytes live on the shared read-only side; each
    /// pooled Decompressor digests them lazily on first rent.
    ///
    /// Previously this used <see cref="ThreadLocal{T}"/> with
    /// <c>trackAllValues: true</c>. On a many-core CPU the .NET ThreadPool
    /// rotates work across dozens of worker threads over a session, and
    /// every worker that ever ran a Decode() paid a permanent native
    /// Decompressor allocation — on a 16-core box we observed up to 16 live
    /// instances each holding several MB of native state, for ~100 MB of
    /// avoidable native residency under a single-digit concurrency workload
    /// (OCR tick + occasional background tasks). Bounding the pool cap at
    /// <see cref="MaxPooledDecompressors"/> lets a burst spin up extras while
    /// the steady state collapses back to ~2 instances.
    ///
    /// Hot-path allocations: zero on the happy path when the pool is warm.
    /// A cold rent on an empty pool creates a fresh Decompressor (rare after
    /// the first few calls). When the pool is already at cap, overflow
    /// rentals still get a transient Decompressor — we just don't keep it
    /// around after return.
    /// </summary>
    public sealed class ZstdValueDecoder : IDisposable
    {
        // Cap is chosen to cover realistic burst concurrency (OCR tick +
        // one or two background Task.Run workers that might touch values).
        // Setting higher wastes native memory; lower would serialise under
        // burst and add latency to the matcher hot path. 4 is comfortably
        // above the observed steady-state of 2 and well below the many-
        // worker-thread residency the ThreadLocal variant leaked.
        private const int MaxPooledDecompressors = 4;

        private readonly byte[] _dict; // empty byte[0] when no dict was trained
        private readonly ConcurrentBag<Decompressor> _pool;
        private int _pooledCount;
        private int _disposed;

        public ZstdValueDecoder(byte[] dictionary)
        {
            _dict = dictionary ?? Array.Empty<byte>();
            _pool = new ConcurrentBag<Decompressor>();
        }

        private Decompressor CreateDecompressor()
        {
            var decompressor = new Decompressor();
            if (_dict.Length > 0)
            {
                decompressor.LoadDictionary(_dict);
            }
            return decompressor;
        }

        /// <summary>
        /// Decompress <paramref name="compressed"/> into <paramref name="destination"/>.
        /// Returns the number of bytes written. Throws if the destination is
        /// smaller than the decompressed payload. No partial writes on failure.
        /// </summary>
        public int Decode(ReadOnlySpan<byte> compressed, Span<byte> destination)
        {
            if (_disposed != 0) throw new ObjectDisposedException(nameof(ZstdValueDecoder));

            Decompressor decompressor;
            bool rentedFromPool = _pool.TryTake(out decompressor);
            if (rentedFromPool)
            {
                // Whenever we pull an instance out of the bag, the pool
                // shrinks by one — keep _pooledCount in sync so overflow
                // accounting stays honest.
                Interlocked.Decrement(ref _pooledCount);
            }
            else
            {
                decompressor = CreateDecompressor();
            }

            bool unwrapThrew = false;
            try
            {
                return decompressor.Unwrap(compressed, destination);
            }
            catch
            {
                unwrapThrew = true;
                throw;
            }
            finally
            {
                // Return the instance to the pool only if we're under cap
                // AND the unwrap completed cleanly. A Decompressor whose
                // Unwrap threw (malformed frame, destination too small,
                // native error) may be in a partially-consumed state; re-
                // using it would risk decoding the next caller's bytes
                // against stale dict/context state. Disposing here is
                // cheap — the hot path tolerates the occasional fresh
                // CreateDecompressor on the next rent.
                if (_disposed != 0 || unwrapThrew)
                {
                    try { decompressor.Dispose(); } catch { /* best-effort */ }
                }
                else if (Interlocked.Increment(ref _pooledCount) <= MaxPooledDecompressors)
                {
                    _pool.Add(decompressor);
                    // Dispose may have drained the bag between our _disposed
                    // check above and the Add. Take one more try: if we
                    // succeed, the pool owns it; if not (steady-state hot
                    // path), somebody else already rented it or we beat a
                    // concurrent Dispose — either way the bag is the right
                    // owner.
                    if (_disposed != 0 && _pool.TryTake(out var stranded))
                    {
                        Interlocked.Decrement(ref _pooledCount);
                        try { stranded.Dispose(); } catch { /* best-effort */ }
                    }
                }
                else
                {
                    // We optimistically bumped the counter above — undo.
                    Interlocked.Decrement(ref _pooledCount);
                    try { decompressor.Dispose(); } catch { /* best-effort */ }
                }
            }
        }

        /// <summary>
        /// Decompress and return the plaintext as a UTF-8 string. Convenience
        /// path; allocates one <see cref="string"/> per call.
        /// </summary>
        public string DecodeAsString(ReadOnlySpan<byte> compressed)
        {
            if (_disposed != 0) throw new ObjectDisposedException(nameof(ZstdValueDecoder));
            if (compressed.IsEmpty) return string.Empty;

            ulong decompressedSize = Decompressor.GetDecompressedSize(compressed);
            int needed = decompressedSize > int.MaxValue
                ? int.MaxValue
                : (int)decompressedSize;

            if (needed <= 0)
            {
                // Fallback: zstd sometimes returns 0 for small frames with
                // no pledged size. Pool a reasonable default.
                byte[] fallback = ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    int n = Decode(compressed, fallback);
                    return Encoding.UTF8.GetString(fallback, 0, n);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(fallback);
                }
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(needed);
            try
            {
                int n = Decode(compressed, buffer.AsSpan(0, needed));
                return Encoding.UTF8.GetString(buffer, 0, n);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Read the pledged decompressed length embedded in the zstd frame
        /// header. Returns 0 when the header carries no pledge (rare in our
        /// pipeline since the writer always sets it).
        /// </summary>
        public static int GetDecompressedLength(ReadOnlySpan<byte> compressed)
        {
            ulong pledged = Decompressor.GetDecompressedSize(compressed);
            if (pledged > int.MaxValue) return int.MaxValue;
            return (int)pledged;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Drain the pool and dispose every resident native decompressor.
            // ConcurrentBag.TryTake returns false when empty, so this
            // exits cleanly regardless of size. The Decode finally block
            // self-heals against the drain/Add race: a worker whose Add
            // landed after our drain will itself take its instance back
            // out and dispose it inline (see the post-Add re-check).
            while (_pool.TryTake(out var decompressor))
            {
                try { decompressor.Dispose(); } catch { /* best-effort */ }
            }
        }
    }
}
