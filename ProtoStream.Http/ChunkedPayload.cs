using System;
using System.Buffers;
using System.Buffers.Text;
using ProtoStream.Codecs;

namespace ProtoStream.Http;

/// <summary>
/// Decodes the chunked transfer coding (RFC 9112 section 7.1) straight from the connection buffer. Chunk
/// data is handed out as slices, never copied; sizes, extensions and trailers are bounded.
/// </summary>
public sealed class ChunkedPayloadDecoder : IPayloadDecoder
{
    private const int MaxSizeLine = 16 + 2 + 256; // hex digits, CRLF, extensions
    private State _state;
    private long _remainingInChunk;
    private long _total;
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

    /// <summary>Prepares the decoder for a body of at most <paramref name="maxBodySize"/> bytes and <paramref name="maxTrailerBytes"/> of trailers.</summary>
    public void Reset(long maxBodySize, int maxTrailerBytes)
    {
        _state = State.Size;
        _remainingInChunk = 0;
        _total = 0;
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
                    if (!TryReadLine(rest, MaxSizeLine, out ReadOnlySequence<byte> line, out long lineLength, out bool tooLong))
                        return tooLong ? PayloadStep.Invalid(ViolationCode.LimitExceeded, "A chunk size line is too long.") : NeedMore(skip, isCompleted);
                    if (!TryParseSize(line, out long size))
                        return PayloadStep.Invalid(ViolationCode.Malformed, "A chunk size is malformed.");

                    _total += size;
                    if (size > _maxTotal || _total > _maxTotal)
                        return PayloadStep.Invalid(ViolationCode.LimitExceeded, "The chunked body exceeds the maximum body size.");

                    skip += lineLength;
                    _remainingInChunk = size;
                    _state = size == 0 ? State.Trailer : State.Data;
                    continue;
                }

                case State.Data:
                    return rest.IsEmpty ? NeedMore(skip, isCompleted) : PayloadStep.Data(skip, Math.Min(_remainingInChunk, rest.Length));

                case State.DataEnd:
                {
                    if (rest.Length < 2)
                        return NeedMore(skip, isCompleted);
                    var crlf = new SequenceReader<byte>(rest);
                    if (!crlf.IsNext("\r\n"u8))
                        return PayloadStep.Invalid(ViolationCode.Malformed, "Chunk data is not followed by CRLF.");
                    skip += 2;
                    _state = State.Size;
                    continue;
                }

                case State.Trailer:
                {
                    if (!TryReadLine(rest, _maxTrailerBytes - _trailerBytes + 2, out ReadOnlySequence<byte> line, out long lineLength, out bool tooLong))
                        return tooLong ? PayloadStep.Invalid(ViolationCode.LimitExceeded, "The trailer section is too large.") : NeedMore(skip, isCompleted);
                    skip += lineLength;
                    if (line.IsEmpty)
                    {
                        _state = State.Done;
                        return PayloadStep.End(skip);
                    }

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

    private static PayloadStep NeedMore(long skip, bool isCompleted) =>
        isCompleted ? PayloadStep.Invalid(ViolationCode.Truncated, "The connection closed inside a chunked body.") : PayloadStep.NeedMore(skip);

    private static bool TryReadLine(in ReadOnlySequence<byte> input, long maxLength, out ReadOnlySequence<byte> line, out long consumed, out bool tooLong)
    {
        ReadOnlySequence<byte> window = input.Length > maxLength ? input.Slice(0, maxLength) : input;
        var reader = new SequenceReader<byte>(window);
        if (reader.TryReadTo(out line, "\r\n"u8, advancePastDelimiter: true))
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
        Span<byte> buffer = stackalloc byte[MaxSizeLine];
        if (line.Length > MaxSizeLine)
            return false;
        line.CopyTo(buffer);
        ReadOnlySpan<byte> span = buffer[..(int)line.Length];

        int digits = 0;
        while (digits < span.Length && IsHex(span[digits]))
            digits++;
        if (digits is 0 or > 15)
            return false;

        // After the size only chunk extensions may follow: optional whitespace, then ';'.
        ReadOnlySpan<byte> rest = HttpSyntax.TrimWhitespace(span[digits..]);
        if (!rest.IsEmpty && rest[0] != ';')
            return false;
        if (rest.IndexOfAny(HttpSyntax.InvalidValue) >= 0)
            return false;

        return Utf8Parser.TryParse(span[..digits], out size, out int used, 'X') && used == digits;
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
}

/// <summary>Encodes a body with the chunked transfer coding.</summary>
public sealed class ChunkedPayloadEncoder : IPayloadEncoder
{
    private ChunkedPayloadEncoder() { }

    /// <summary>The shared instance; the encoder is stateless.</summary>
    public static ChunkedPayloadEncoder Instance { get; } = new();

    /// <inheritdoc />
    public void WriteData(ReadOnlySpan<byte> data, IBufferWriter<byte> output)
    {
        if (data.IsEmpty)
            return; // an empty chunk would end the body

        Span<byte> size = output.GetSpan(18);
        Utf8Formatter.TryFormat((ulong)data.Length, size, out int written, 'x');
        size[written++] = (byte)'\r';
        size[written++] = (byte)'\n';
        output.Advance(written);
        output.Write(data);
        output.Write("\r\n"u8);
    }

    /// <inheritdoc />
    public void WriteEnd(IBufferWriter<byte> output) => output.Write("0\r\n\r\n"u8);
}
