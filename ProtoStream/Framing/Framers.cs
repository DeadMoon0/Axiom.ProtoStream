using System;
using System.Buffers;
using System.Buffers.Text;
using System.Text;
using ProtoStream.Errors;

namespace ProtoStream.Framing;

/// <summary>The built-in framers. Every one is bounded by a required maximum frame size.</summary>
public static class Framers
{
    /// <summary>A length in front of every frame; the length counts the frame content only.</summary>
    public static IFramer LengthPrefixed(LengthPrefix prefix, int maxFrameSize)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ValidateMax(maxFrameSize);
        return new LengthPrefixedFramer(prefix, maxFrameSize);
    }

    /// <summary>Frames end with <paramref name="delimiter"/>, which is not part of the frame (lines, NUL-terminated records).</summary>
    public static IFramer Delimited(ReadOnlySpan<byte> delimiter, int maxFrameSize)
    {
        if (delimiter.IsEmpty)
            throw new ArgumentException("A delimiter needs at least one byte.", nameof(delimiter));
        ValidateMax(maxFrameSize);
        return new DelimitedFramer(delimiter.ToArray(), maxFrameSize);
    }

    /// <summary>Every frame has exactly <paramref name="size"/> bytes.</summary>
    public static IFramer FixedSize(int size)
    {
        ValidateMax(size);
        return new FixedSizeFramer(size);
    }

    /// <summary>
    /// A header that contains the length at <paramref name="lengthOffset"/>; the frame handed to the codec
    /// includes the header. The whole frame is <c>lengthOffset + prefix size + length + adjust</c> bytes:
    /// Modbus TCP is (4, UInt16BigEndian, 0), gRPC (1, UInt32BigEndian, 0), HTTP/2 (0, UInt24BigEndian, 6).
    /// On write the codec encodes the whole frame and the framer fills in the length.
    /// </summary>
    public static IFramer HeaderDeclaredLength(int lengthOffset, LengthPrefix prefix, int adjust, int maxFrameSize)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfNegative(lengthOffset);
        if (!prefix.IsFixedSize)
            throw new ArgumentException("A length inside a header must have a fixed size, so it can be filled in after encoding.", nameof(prefix));
        ValidateMax(maxFrameSize);
        return new HeaderDeclaredLengthFramer(lengthOffset, prefix, adjust, maxFrameSize);
    }

    /// <summary>
    /// Text headers with a <c>Content-Length</c>, a blank line, then the frame content: the base protocol
    /// of LSP and JSON-RPC over stdio. Other headers are accepted and ignored.
    /// </summary>
    public static IFramer ContentLengthHeader(int maxFrameSize)
    {
        ValidateMax(maxFrameSize);
        return new ContentLengthHeaderFramer(maxFrameSize);
    }

    private static void ValidateMax(int maxFrameSize)
    {
        if (maxFrameSize < 1)
            throw new ArgumentOutOfRangeException(nameof(maxFrameSize), maxFrameSize, "A frame size must be at least one byte.");
    }
}

internal sealed class LengthPrefixedFramer(LengthPrefix prefix, int maxFrameSize) : IFramer
{
    public int MaxFrameSize => maxFrameSize;

    public FrameStatus TryReadFrame(in ReadOnlySequence<byte> input, out ReadOnlySequence<byte> frame, out SequencePosition consumed)
    {
        frame = default;
        consumed = input.Start;

        switch (prefix.TryRead(input, 0, out long length, out int size))
        {
            case PrefixStatus.NeedMore:
                return FrameStatus.NeedMore;
            case PrefixStatus.Malformed:
                return FrameStatus.Malformed;
        }

        if (length > maxFrameSize)
            return FrameStatus.TooLarge;
        if (input.Length < size + length)
            return FrameStatus.NeedMore;

        frame = input.Slice(size, length);
        consumed = frame.End;
        return FrameStatus.Complete;
    }

    public void WriteFrame(ReadOnlySpan<byte> encoded, IBufferWriter<byte> output)
    {
        if (encoded.Length > maxFrameSize || encoded.Length > prefix.MaxValue)
            throw new ProtocolStateException($"A frame of {encoded.Length} bytes exceeds the maximum of {Math.Min(maxFrameSize, prefix.MaxValue)}.");

        prefix.WriteTo(encoded.Length, output);
        output.Write(encoded);
    }
}

internal sealed class DelimitedFramer(byte[] delimiter, int maxFrameSize) : IFramer
{
    public int MaxFrameSize => maxFrameSize;

    public FrameStatus TryReadFrame(in ReadOnlySequence<byte> input, out ReadOnlySequence<byte> frame, out SequencePosition consumed)
    {
        // Only the bytes that could belong to an acceptable frame are searched, so the work per call is bounded.
        long window = (long)maxFrameSize + delimiter.Length;
        ReadOnlySequence<byte> searched = input.Length > window ? input.Slice(0, window) : input;

        var reader = new SequenceReader<byte>(searched);
        if (reader.TryReadTo(out frame, delimiter, advancePastDelimiter: true))
        {
            consumed = reader.Position;
            return FrameStatus.Complete;
        }

        frame = default;
        consumed = input.Start;
        return input.Length > window ? FrameStatus.TooLarge : FrameStatus.NeedMore;
    }

    public void WriteFrame(ReadOnlySpan<byte> encoded, IBufferWriter<byte> output)
    {
        if (encoded.Length > maxFrameSize)
            throw new ProtocolStateException($"A frame of {encoded.Length} bytes exceeds the maximum of {maxFrameSize}.");
        // A delimiter inside the content would end the frame early on the other side: an injection.
        if (encoded.IndexOf(delimiter) >= 0)
            throw new ProtocolStateException("The frame content contains the frame delimiter.");

        output.Write(encoded);
        output.Write(delimiter);
    }
}

internal sealed class FixedSizeFramer(int size) : IFramer
{
    public int MaxFrameSize => size;

    public FrameStatus TryReadFrame(in ReadOnlySequence<byte> input, out ReadOnlySequence<byte> frame, out SequencePosition consumed)
    {
        if (input.Length < size)
        {
            frame = default;
            consumed = input.Start;
            return FrameStatus.NeedMore;
        }

        frame = input.Slice(0, size);
        consumed = frame.End;
        return FrameStatus.Complete;
    }

    public void WriteFrame(ReadOnlySpan<byte> encoded, IBufferWriter<byte> output)
    {
        if (encoded.Length != size)
            throw new ProtocolStateException($"A frame of {encoded.Length} bytes does not have the fixed size of {size}.");
        output.Write(encoded);
    }
}

internal sealed class HeaderDeclaredLengthFramer(int lengthOffset, LengthPrefix prefix, int adjust, int maxFrameSize) : IFramer
{
    private readonly int _headerSize = lengthOffset + prefix.MaxSize;

    public int MaxFrameSize => maxFrameSize;

    public FrameStatus TryReadFrame(in ReadOnlySequence<byte> input, out ReadOnlySequence<byte> frame, out SequencePosition consumed)
    {
        frame = default;
        consumed = input.Start;

        switch (prefix.TryRead(input, lengthOffset, out long length, out _))
        {
            case PrefixStatus.NeedMore:
                return FrameStatus.NeedMore;
            case PrefixStatus.Malformed:
                return FrameStatus.Malformed;
        }

        long total = _headerSize + length + adjust;
        if (total < _headerSize)
            return FrameStatus.Malformed;
        if (total > maxFrameSize)
            return FrameStatus.TooLarge;
        if (input.Length < total)
            return FrameStatus.NeedMore;

        frame = input.Slice(0, total);
        consumed = frame.End;
        return FrameStatus.Complete;
    }

    public void WriteFrame(ReadOnlySpan<byte> encoded, IBufferWriter<byte> output)
    {
        long length = encoded.Length - _headerSize - adjust;
        if (encoded.Length < _headerSize || length < 0 || length > prefix.MaxValue)
            throw new ProtocolStateException($"An encoded frame of {encoded.Length} bytes cannot carry its length in a {prefix} at offset {lengthOffset}.");
        if (encoded.Length > maxFrameSize)
            throw new ProtocolStateException($"A frame of {encoded.Length} bytes exceeds the maximum of {maxFrameSize}.");

        Span<byte> destination = output.GetSpan(encoded.Length)[..encoded.Length];
        encoded.CopyTo(destination);
        prefix.Write(length, destination.Slice(lengthOffset, prefix.MaxSize));
        output.Advance(encoded.Length);
    }
}

internal sealed class ContentLengthHeaderFramer(int maxFrameSize) : IFramer
{
    private const int MaxHeaderSize = 8 * 1024;

    /// <summary>Digits of the largest frame length, an <see cref="int"/>.</summary>
    private const int MaxInt32Digits = 10;

    private static ReadOnlySpan<byte> LineEnd => "\r\n"u8;
    private static ReadOnlySpan<byte> HeaderEnd => "\r\n\r\n"u8;

    public int MaxFrameSize => maxFrameSize;

    public FrameStatus TryReadFrame(in ReadOnlySequence<byte> input, out ReadOnlySequence<byte> frame, out SequencePosition consumed)
    {
        frame = default;
        consumed = input.Start;

        ReadOnlySequence<byte> searched = input.Length > MaxHeaderSize ? input.Slice(0, MaxHeaderSize) : input;
        var reader = new SequenceReader<byte>(searched);
        if (!reader.TryReadTo(out ReadOnlySequence<byte> headers, HeaderEnd, advancePastDelimiter: true))
            return input.Length > MaxHeaderSize ? FrameStatus.TooLarge : FrameStatus.NeedMore;

        long headerLength = reader.Consumed;
        long contentLength = ParseContentLength(headers);
        if (contentLength < 0)
            return FrameStatus.Malformed;
        if (contentLength > maxFrameSize)
            return FrameStatus.TooLarge;
        if (input.Length < headerLength + contentLength)
            return FrameStatus.NeedMore;

        frame = input.Slice(headerLength, contentLength);
        consumed = frame.End;
        return FrameStatus.Complete;
    }

    public void WriteFrame(ReadOnlySpan<byte> encoded, IBufferWriter<byte> output)
    {
        if (encoded.Length > maxFrameSize)
            throw new ProtocolStateException($"A frame of {encoded.Length} bytes exceeds the maximum of {maxFrameSize}.");

        output.Write("Content-Length: "u8);
        Span<byte> digits = output.GetSpan(MaxInt32Digits);
        Utf8Formatter.TryFormat(encoded.Length, digits, out int written);
        output.Advance(written);
        output.Write(HeaderEnd);
        output.Write(encoded);
    }

    /// <returns>The declared length, or -1 when the headers are malformed or the length is missing or duplicated.</returns>
    private static long ParseContentLength(in ReadOnlySequence<byte> headers)
    {
        byte[]? rented = null;
        ReadOnlySpan<byte> span = headers.IsSingleSegment
            ? headers.FirstSpan
            : (rented = ArrayPool<byte>.Shared.Rent((int)headers.Length)).AsSpan(0, (int)headers.Length);
        if (rented is not null)
            headers.CopyTo(rented);

        try
        {
            long length = -1;
            while (!span.IsEmpty)
            {
                int end = span.IndexOf(LineEnd);
                ReadOnlySpan<byte> line = end < 0 ? span : span[..end];
                span = end < 0 ? default : span[(end + LineEnd.Length)..];

                int colon = line.IndexOf((byte)':');
                if (colon <= 0)
                    return -1;
                if (!Ascii.EqualsIgnoreCase(line[..colon], "Content-Length"u8))
                    continue;

                ReadOnlySpan<byte> value = line[(colon + 1)..].Trim((byte)' ');
                // Digits only: no sign, no inner whitespace, no hex, nothing a lenient parser would accept.
                if (length >= 0 || value.IsEmpty || value.Length > MaxInt32Digits || value.IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0
                    || !Utf8Parser.TryParse(value, out long parsed, out int used) || used != value.Length)
                    return -1;
                length = parsed;
            }

            return length;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
