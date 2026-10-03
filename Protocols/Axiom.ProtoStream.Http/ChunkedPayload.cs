using System;
using System.Buffers;
using System.Buffers.Text;
using Axiom.ProtoStream.Codecs;

namespace Axiom.ProtoStream.Http;

/// <summary>
/// Decodes the chunked transfer coding (RFC 9112 section 7.1) straight from the connection buffer. Chunk
/// data is handed out as slices, never copied; sizes, extensions and trailers are validated and bounded.
/// </summary>
public sealed class ChunkedPayloadDecoder : IPayloadDecoder
{
    /// <summary>Most hex digits of a chunk size: 15 digits cannot overflow a 64-bit length.</summary>
    private const int MaxSizeDigits = 15;

    /// <summary>Most bytes of chunk extensions on one size line.</summary>
    private const int MaxExtensionBytes = 256;

    /// <summary>Framing bytes (size lines, extensions, CRLFs) allowed per byte of chunk data.</summary>
    /// <remarks>
    /// Without it, one data byte per chunk behind a long extension makes the server read some 270 bytes per body
    /// byte, past every limit that counts data (Go CVE-2023-39326). Go allows the same ratio.
    /// </remarks>
    private const int MaxFramingPerDataByte = 16;

    /// <summary>Framing bytes allowed regardless of the data, so short bodies with extensions still pass.</summary>
    private const int FramingAllowance = 4096;

    /// <summary>Format code for hexadecimal numbers in <see cref="Utf8Parser"/> and <see cref="Utf8Formatter"/>.</summary>
    internal const char HexFormat = 'X';

    private State _state;
    private long _remainingInChunk;
    private long _total;
    private long _framing;
    private long _maxTotal;
    private int _trailerBytes;
    private int _maxTrailerBytes;

    private enum State : byte
    {
        Size,
        Data,
        DataEnd,
        Trailer,
        Done,
    }

    /// <summary>Longest size line: digits, extensions and the line end.</summary>
    private static int MaxSizeLine => MaxSizeDigits + MaxExtensionBytes + HttpGrammar.Crlf.Length;

    /// <summary>Prepares the decoder for a body of at most <paramref name="maxBodySize"/> bytes and <paramref name="maxTrailerBytes"/> of trailers.</summary>
    public void Reset(long maxBodySize, int maxTrailerBytes)
    {
        _state = State.Size;
        _remainingInChunk = 0;
        _total = 0;
        _framing = 0;
        _maxTotal = maxBodySize;
        _trailerBytes = 0;
        _maxTrailerBytes = maxTrailerBytes;
    }

    /// <inheritdoc />
    public PayloadStep Next(in ReadOnlySequence<byte> input, bool isCompleted)
    {
        long skip = 0;
        while (true)
        {
            ReadOnlySequence<byte> rest = input.Slice(skip);
            switch (_state)
            {
                case State.Size:
                {
                    // chunk = chunk-size [ chunk-ext ] CRLF chunk-data CRLF
                    if (!TryReadLine(rest, MaxSizeLine, out ReadOnlySequence<byte> line, out long lineLength, out bool tooLong))
                        return tooLong ? PayloadStep.Invalid(ViolationCode.LimitExceeded, "A chunk size line is too long.") : NeedMore(skip, isCompleted);
                    if (!TryParseSize(line, out long size))
                        return PayloadStep.Invalid(ViolationCode.Malformed, "A chunk size line is malformed.");

                    _total += size;
                    if (size > _maxTotal || _total > _maxTotal)
                        return PayloadStep.Invalid(ViolationCode.LimitExceeded, "The chunked body exceeds the maximum body size.");
                    if (!AddFraming(lineLength))
                        return PayloadStep.Invalid(ViolationCode.LimitExceeded, "The chunked body carries far more framing than data.");

                    skip += lineLength;
                    _remainingInChunk = size;
                    _state = size == 0 ? State.Trailer : State.Data;
                    continue;
                }

                case State.Data:
                    return rest.IsEmpty ? NeedMore(skip, isCompleted) : PayloadStep.Data(skip, Math.Min(_remainingInChunk, rest.Length));

                case State.DataEnd:
                {
                    if (rest.Length < HttpGrammar.Crlf.Length)
                        return NeedMore(skip, isCompleted);
                    var reader = new SequenceReader<byte>(rest);
                    if (!reader.IsNext(HttpGrammar.Crlf))
                        return PayloadStep.Invalid(ViolationCode.Malformed, "Chunk data is not followed by CRLF.");
                    skip += HttpGrammar.Crlf.Length;
                    _state = State.Size;
                    if (!AddFraming(HttpGrammar.Crlf.Length))
                        return PayloadStep.Invalid(ViolationCode.LimitExceeded, "The chunked body carries far more framing than data.");
                    continue;
                }

                case State.Trailer:
                {
                    long allowed = _maxTrailerBytes - _trailerBytes + HttpGrammar.Crlf.Length;
                    if (!TryReadLine(rest, allowed, out ReadOnlySequence<byte> line, out long lineLength, out bool tooLong))
                        return tooLong ? PayloadStep.Invalid(ViolationCode.LimitExceeded, "The trailer section is too large.") : NeedMore(skip, isCompleted);
                    skip += lineLength;
                    if (line.IsEmpty)
                    {
                        _state = State.Done;
                        return PayloadStep.End(skip);
                    }

                    // Trailer fields have field-line syntax (RFC 9112 section 7.1.2); they are checked, then discarded.
                    if (!IsFieldLine(line))
                        return PayloadStep.Invalid(ViolationCode.Malformed, "A trailer field is malformed.");
                    _trailerBytes += (int)lineLength;
                    continue;
                }

                default:
                    return PayloadStep.End(skip);
            }
        }
    }

    /// <inheritdoc />
    public void OnConsumed(long dataBytes)
    {
        _remainingInChunk -= dataBytes;
        if (_state == State.Data && _remainingInChunk == 0)
            _state = State.DataEnd;
    }

    private bool AddFraming(long bytes)
    {
        _framing += bytes;
        return _framing <= FramingAllowance + (MaxFramingPerDataByte * _total);
    }

    private static PayloadStep NeedMore(long skip, bool isCompleted) =>
        isCompleted ? PayloadStep.Invalid(ViolationCode.Truncated, "The connection closed inside a chunked body.") : PayloadStep.NeedMore(skip);

    private static bool TryReadLine(in ReadOnlySequence<byte> input, long maxLength, out ReadOnlySequence<byte> line, out long consumed, out bool tooLong)
    {
        ReadOnlySequence<byte> window = input.Length > maxLength ? input.Slice(0, maxLength) : input;
        var reader = new SequenceReader<byte>(window);
        if (reader.TryReadTo(out line, HttpGrammar.Crlf, advancePastDelimiter: true))
        {
            consumed = reader.Consumed;
            tooLong = false;
            return true;
        }

        consumed = 0;
        tooLong = input.Length > maxLength;
        return false;
    }

    private static bool TryParseSize(in ReadOnlySequence<byte> line, out long size)
    {
        size = 0;
        if (line.Length > MaxSizeLine)
            return false;
        Span<byte> buffer = stackalloc byte[MaxSizeLine];
        line.CopyTo(buffer);
        ReadOnlySpan<byte> span = buffer[..(int)line.Length];

        int digits = 0;
        while (digits < span.Length && char.IsAsciiHexDigit((char)span[digits]))
            digits++;
        if (digits is 0 or > MaxSizeDigits)
            return false;

        return IsChunkExtensions(span[digits..]) && Utf8Parser.TryParse(span[..digits], out size, out int used, HexFormat) && used == digits;
    }

    /// <summary>
    /// chunk-ext = *( BWS ";" BWS chunk-ext-name [ BWS "=" BWS chunk-ext-val ] ), where the name is a token and the
    /// value a token or quoted-string (RFC 9112 section 7.1.1). Extensions are validated, then ignored.
    /// </summary>
    private static bool IsChunkExtensions(ReadOnlySpan<byte> extensions)
    {
        while (true)
        {
            // BWS is allowed only around ";" and "=": whitespace that ends the line is not part of the grammar.
            ReadOnlySpan<byte> trimmed = SkipWhitespace(extensions);
            if (trimmed.IsEmpty)
                return extensions.IsEmpty;
            extensions = trimmed;
            if (extensions[0] != HttpGrammar.Semicolon)
                return false;
            extensions = SkipWhitespace(extensions[1..]);

            int nameLength = TokenLength(extensions);
            if (nameLength == 0)
                return false;
            extensions = extensions[nameLength..];

            ReadOnlySpan<byte> afterName = SkipWhitespace(extensions);
            if (afterName.IsEmpty || afterName[0] != HttpGrammar.EqualsSign)
                continue;

            extensions = SkipWhitespace(afterName[1..]);
            int valueLength = !extensions.IsEmpty && extensions[0] == HttpGrammar.Quote
                ? HttpSyntax.QuotedStringLength(extensions)
                : TokenLength(extensions);
            if (valueLength <= 0)
                return false;
            extensions = extensions[valueLength..];
        }
    }

    private static ReadOnlySpan<byte> SkipWhitespace(ReadOnlySpan<byte> value) => value[HttpSyntax.LeadingWhitespace(value)..];

    private static int TokenLength(ReadOnlySpan<byte> value)
    {
        int end = value.IndexOfAnyExcept(HttpSyntax.Token);
        return end < 0 ? value.Length : end;
    }

    /// <summary>field-line = field-name ":" OWS field-value OWS, without obs-fold.</summary>
    private static bool IsFieldLine(in ReadOnlySequence<byte> line)
    {
        ReadOnlySpan<byte> span = line.IsSingleSegment ? line.FirstSpan : line.ToArray();
        int colon = span.IndexOf(HttpGrammar.Colon);
        return colon > 0 && HttpSyntax.IsToken(span[..colon]) && HttpSyntax.IsFieldValue(HttpSyntax.TrimWhitespace(span[(colon + 1)..]));
    }
}

/// <summary>Encodes a body with the chunked transfer coding.</summary>
public sealed class ChunkedPayloadEncoder : IPayloadEncoder
{
    /// <summary>Hex digits of the largest chunk this encoder writes: an <see cref="int"/> length.</summary>
    private const int MaxSizeDigits = sizeof(int) * 2;

    private ChunkedPayloadEncoder() { }

    /// <summary>The shared instance; the encoder is stateless.</summary>
    public static ChunkedPayloadEncoder Instance { get; } = new();

    /// <summary>last-chunk with an empty trailer section: "0" CRLF CRLF.</summary>
    private static ReadOnlySpan<byte> LastChunk => "0\r\n\r\n"u8;

    /// <inheritdoc />
    public void WriteData(ReadOnlySpan<byte> data, IBufferWriter<byte> output)
    {
        if (data.IsEmpty)
            return; // an empty chunk would end the body

        Span<byte> size = output.GetSpan(MaxSizeDigits);
        Utf8Formatter.TryFormat((uint)data.Length, size, out int written, ChunkedPayloadDecoder.HexFormat);
        output.Advance(written);
        output.Write(HttpGrammar.Crlf);
        output.Write(data);
        output.Write(HttpGrammar.Crlf);
    }

    /// <inheritdoc />
    public void WriteEnd(IBufferWriter<byte> output) => output.Write(LastChunk);
}
