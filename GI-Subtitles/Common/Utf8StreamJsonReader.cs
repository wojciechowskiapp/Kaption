using System;
using System.Buffers;
using System.IO;
using System.Text.Json;

namespace GI_Subtitles.Common
{
    /// <summary>
    /// Pull-style streaming JSON reader over a <see cref="Stream"/>, backed by
    /// <see cref="Utf8JsonReader"/>.
    ///
    /// <para>Exists because <c>Utf8JsonReader</c> is a <c>ref struct</c> and
    /// therefore cannot be held in a field across calls, which is what a
    /// pull-style <c>Read()</c> loop needs. The reader is reconstructed per token
    /// from a persisted <see cref="JsonReaderState"/> and the current buffer
    /// window; construction is a struct over a span, so it allocates nothing.</para>
    ///
    /// <para>The alternative — reading the whole file into a byte array and using
    /// one long-lived reader — would buffer 18 MB for the dialogue graph alone and
    /// undo the streaming behaviour the graph loaders were written for. Peak
    /// managed memory here stays at the buffer size plus one token.</para>
    /// </summary>
    public sealed class Utf8StreamJsonReader : IDisposable
    {
        private const int InitialBufferSize = 32 * 1024;

        private readonly Stream _stream;
        private readonly bool _leaveOpen;

        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        private byte[] _buffer;
        private bool _bomChecked;
        private int _dataLength;
        private int _consumed;
        private bool _isFinalBlock;
        private JsonReaderState _state;

        private string _stringValue;
        private bool _valueIsNull;
        private long _int64Value;
        private ulong _uint64Value;
        private bool _numberIsUnsigned;
        private bool _hasNumber;

        public Utf8StreamJsonReader(Stream stream, bool leaveOpen = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _leaveOpen = leaveOpen;
            _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
            _state = new JsonReaderState(new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }

        public JsonTokenType TokenType { get; private set; } = JsonTokenType.None;

        /// <summary>True when the current token is a JSON null literal.</summary>
        public bool ValueIsNull => _valueIsNull;

        /// <summary>
        /// True when the current token carries a scalar value. Mirrors the
        /// <c>jsonReader.Value != null</c> checks the Newtonsoft loaders used.
        /// </summary>
        public bool HasValue =>
            TokenType == JsonTokenType.String ||
            TokenType == JsonTokenType.Number ||
            TokenType == JsonTokenType.True ||
            TokenType == JsonTokenType.False;

        public bool Read()
        {
            while (true)
            {
                if (!_bomChecked)
                {
                    if (_dataLength - _consumed < Utf8Bom.Length && !_isFinalBlock)
                    {
                        if (!Refill()) return false;
                        continue;
                    }
                    SkipBomIfPresent();
                }

                bool ok = ReadFromBuffer(out bool needMore);
                if (ok) return true;
                if (!needMore) return false;
                if (!Refill()) return false;
            }
        }

        /// <summary>
        /// StreamReader swallowed a UTF-8 BOM; Utf8JsonReader treats one as an
        /// invalid start of value. The graph files written by
        /// DialogGraphDownloader.SaveCompactJson carry a BOM, so without this
        /// the loaders throw on files the app itself produced.
        /// </summary>
        private void SkipBomIfPresent()
        {
            _bomChecked = true;
            int available = _dataLength - _consumed;
            if (available < Utf8Bom.Length) return;
            for (int i = 0; i < Utf8Bom.Length; i++)
            {
                if (_buffer[_consumed + i] != Utf8Bom[i]) return;
            }
            _consumed += Utf8Bom.Length;
        }

        private bool ReadFromBuffer(out bool needMore)
        {
            needMore = false;
            var reader = new Utf8JsonReader(
                new ReadOnlySpan<byte>(_buffer, _consumed, _dataLength - _consumed),
                _isFinalBlock,
                _state);

            if (!reader.Read())
            {
                needMore = !_isFinalBlock;
                return false;
            }

            TokenType = reader.TokenType;
            _stringValue = null;
            _valueIsNull = false;
            _hasNumber = false;

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                case JsonTokenType.String:
                    _stringValue = reader.GetString();
                    break;
                case JsonTokenType.Number:
                    if (reader.TryGetUInt64(out ulong u))
                    {
                        _uint64Value = u;
                        _int64Value = unchecked((long)u);
                        _numberIsUnsigned = true;
                        _hasNumber = true;
                    }
                    else if (reader.TryGetInt64(out long l))
                    {
                        _int64Value = l;
                        _uint64Value = unchecked((ulong)l);
                        _numberIsUnsigned = false;
                        _hasNumber = true;
                    }
                    else
                    {
                        _stringValue = reader.GetDouble().ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                    }
                    break;
                case JsonTokenType.Null:
                    _valueIsNull = true;
                    break;
            }

            _state = reader.CurrentState;
            _consumed += (int)reader.BytesConsumed;
            return true;
        }

        /// <summary>
        /// Skip the subtree rooted at the current token. Refills and retries when
        /// the subtree does not fit in the current window, which the equivalent
        /// Newtonsoft call handled implicitly.
        /// </summary>
        public void Skip()
        {
            while (true)
            {
                var reader = new Utf8JsonReader(
                    new ReadOnlySpan<byte>(_buffer, _consumed, _dataLength - _consumed),
                    _isFinalBlock,
                    _state);

                if (reader.TrySkip())
                {
                    TokenType = reader.TokenType;
                    _state = reader.CurrentState;
                    _consumed += (int)reader.BytesConsumed;
                    return;
                }

                if (_isFinalBlock || !Refill()) return;
            }
        }

        public string GetString()
        {
            if (_stringValue != null) return _stringValue;
            if (_hasNumber)
            {
                return _numberIsUnsigned
                    ? _uint64Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : _int64Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return null;
        }

        public long GetInt64()
        {
            if (_hasNumber) return _int64Value;
            if (_stringValue != null &&
                long.TryParse(_stringValue, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long parsed))
                return parsed;
            return 0;
        }

        public ulong GetUInt64()
        {
            if (_hasNumber) return _uint64Value;
            if (_stringValue != null &&
                ulong.TryParse(_stringValue, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out ulong parsed))
                return parsed;
            return 0;
        }

        /// <summary>
        /// Pull more bytes in. Compacts the unconsumed tail to the front and grows
        /// the buffer when a single token spans the whole window.
        /// </summary>
        private bool Refill()
        {
            int remaining = _dataLength - _consumed;

            if (remaining > 0 && _consumed > 0)
                Buffer.BlockCopy(_buffer, _consumed, _buffer, 0, remaining);

            _dataLength = remaining;
            _consumed = 0;

            if (_dataLength == _buffer.Length)
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
                Buffer.BlockCopy(_buffer, 0, bigger, 0, _dataLength);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = bigger;
            }

            int read = _stream.Read(_buffer, _dataLength, _buffer.Length - _dataLength);
            if (read <= 0)
            {
                if (_isFinalBlock) return false;
                _isFinalBlock = true;
                return true;
            }

            _dataLength += read;
            return true;
        }

        public void Dispose()
        {
            if (_buffer != null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
            }
            if (!_leaveOpen) _stream?.Dispose();
        }
    }
}
