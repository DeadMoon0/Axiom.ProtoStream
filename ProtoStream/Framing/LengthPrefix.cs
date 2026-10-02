using System;
using System.Buffers;

namespace ProtoStream.Framing;

/// <summary>
/// How a length is encoded on the wire: in front of a frame, a string or a byte block.
/// </summary>
/// <remarks>
/// The set is closed: every encoding is implemented, validated and tested here, so a protocol picks one
/// instead of writing integer parsing by hand. Reads reject encodings that do not fit the declared size
/// (for example a 7-bit varint longer than five bytes) instead of guessing.
/// </remarks>
public abstract class LengthPrefix
{
    private protected LengthPrefix(string name, int maxSize, long maxValue, bool isFixedSize)
    {
        Name = name;
        MaxSize = maxSize;
        MaxValue = maxValue;
        IsFixedSize = isFixedSize;
    }

    /// <summary>One byte, 0 to 255.</summary>
    public static LengthPrefix UInt8 { get; } = new FixedSizePrefix("UInt8", 1, bigEndian: true);

    /// <summary>Two bytes, most significant first.</summary>
    public static LengthPrefix UInt16BigEndian { get; } = new FixedSizePrefix("UInt16BigEndian", 2, bigEndian: true);

    /// <summary>Two bytes, least significant first.</summary>
    public static LengthPrefix UInt16LittleEndian { get; } = new FixedSizePrefix("UInt16LittleEndian", 2, bigEndian: false);

    /// <summary>Three bytes, most significant first (HTTP/2 frame length).</summary>
    public static LengthPrefix UInt24BigEndian { get; } = new FixedSizePrefix("UInt24BigEndian", 3, bigEndian: true);

    /// <summary>Four bytes, most significant first, limited to <see cref="int.MaxValue"/>.</summary>
    public static LengthPrefix UInt32BigEndian { get; } = new FixedSizePrefix("UInt32BigEndian", 4, bigEndian: true);

    /// <summary>Four bytes, least significant first, limited to <see cref="int.MaxValue"/>.</summary>
    public static LengthPrefix UInt32LittleEndian { get; } = new FixedSizePrefix("UInt32LittleEndian", 4, bigEndian: false);

    /// <summary>
    /// Unsigned LEB128 (protobuf, MQTT): seven bits per byte, least significant group first, at most five
    /// bytes, limited to <see cref="int.MaxValue"/>. Non-minimal encodings are rejected.
    /// </summary>
    public static LengthPrefix VarInt { get; } = new VarIntPrefix();

    /// <summary>QUIC variable-length integer (RFC 9000 section 16): the top two bits give a size of 1, 2, 4 or 8 bytes.</summary>
    public static LengthPrefix QuicVarInt { get; } = new QuicVarIntPrefix();

    /// <summary>A fixed number of hexadecimal ASCII digits, such as the four of a git pkt-line.</summary>
    public static LengthPrefix HexAscii(int digits)
    {
        if (digits is < 1 or > 15)
            throw new ArgumentOutOfRangeException(nameof(digits), digits, "A hex length prefix has 1 to 15 digits.");
        return new HexAsciiPrefix(digits);
    }

    /// <summary>Name of the encoding.</summary>
    public string Name { get; }

    /// <summary>Largest number of bytes an encoded value takes.</summary>
    public int MaxSize { get; }

    /// <summary>Largest value the encoding can carry.</summary>
    public long MaxValue { get; }

    /// <summary>True when every value takes <see cref="MaxSize"/> bytes.</summary>
    public bool IsFixedSize { get; }

    /// <inheritdoc />
    public override string ToString() => Name;

    internal abstract PrefixStatus TryRead(ReadOnlySpan<byte> source, out long value, out int size);

    internal abstract int GetSize(long value);

    internal abstract void Write(long value, Span<byte> destination);

    internal PrefixStatus TryRead(in ReadOnlySequence<byte> input, long offset, out long value, out int size)
    {
        value = 0;
        size = 0;
        if (input.Length <= offset)
            return PrefixStatus.NeedMore;

        ReadOnlySequence<byte> rest = input.Slice(offset);
        int available = (int)Math.Min(rest.Length, MaxSize);
        ReadOnlySpan<byte> first = rest.FirstSpan;
        if (first.Length >= available)
            return TryRead(first[..available], out value, out size);

        Span<byte> copy = stackalloc byte[16];
        rest.Slice(0, available).CopyTo(copy);
        return TryRead(copy[..available], out value, out size);
    }

    internal int WriteTo(long value, IBufferWriter<byte> output)
    {
        int size = GetSize(value);
        Write(value, output.GetSpan(size)[..size]);
        output.Advance(size);
        return size;
    }
}

internal enum PrefixStatus : byte
{
    Ok,
    NeedMore,
    Malformed,
}

internal sealed class FixedSizePrefix(string name, int size, bool bigEndian)
    : LengthPrefix(name, size, size >= 4 ? int.MaxValue : (1L << (8 * size)) - 1, isFixedSize: true)
{
    internal override PrefixStatus TryRead(ReadOnlySpan<byte> source, out long value, out int read)
    {
        value = 0;
        read = 0;
        if (source.Length < MaxSize)
            return PrefixStatus.NeedMore;

        for (int i = 0; i < MaxSize; i++)
        {
            int index = bigEndian ? i : MaxSize - 1 - i;
            value = (value << 8) | source[index];
        }

        read = MaxSize;
        return value > MaxValue ? PrefixStatus.Malformed : PrefixStatus.Ok;
    }

    internal override int GetSize(long value) => MaxSize;

    internal override void Write(long value, Span<byte> destination)
    {
        for (int i = 0; i < MaxSize; i++)
        {
            int index = bigEndian ? MaxSize - 1 - i : i;
            destination[index] = (byte)(value >> (8 * i));
        }
    }
}

internal sealed class VarIntPrefix() : LengthPrefix("VarInt", 5, int.MaxValue, isFixedSize: false)
{
    internal override PrefixStatus TryRead(ReadOnlySpan<byte> source, out long value, out int size)
    {
        value = 0;
        size = 0;
        for (int i = 0; i < 5; i++)
        {
            if (i >= source.Length)
                return PrefixStatus.NeedMore;

            byte b = source[i];
            value |= (long)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                // A final group of zero after the first byte means a longer encoding than needed.
                if (i > 0 && b == 0)
                    return PrefixStatus.Malformed;
                size = i + 1;
                return value > int.MaxValue ? PrefixStatus.Malformed : PrefixStatus.Ok;
            }
        }

        return PrefixStatus.Malformed;
    }

    internal override int GetSize(long value)
    {
        int size = 1;
        while ((value >>= 7) != 0)
            size++;
        return size;
    }

    internal override void Write(long value, Span<byte> destination)
    {
        int i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[i] = (byte)value;
    }
}

internal sealed class QuicVarIntPrefix() : LengthPrefix("QuicVarInt", 8, (1L << 62) - 1, isFixedSize: false)
{
    internal override PrefixStatus TryRead(ReadOnlySpan<byte> source, out long value, out int size)
    {
        value = 0;
        size = 0;
        if (source.IsEmpty)
            return PrefixStatus.NeedMore;

        int length = 1 << (source[0] >> 6);
        if (source.Length < length)
            return PrefixStatus.NeedMore;

        value = source[0] & 0x3F;
        for (int i = 1; i < length; i++)
            value = (value << 8) | source[i];

        size = length;
        return PrefixStatus.Ok;
    }

    internal override int GetSize(long value) => value switch
    {
        <= 63 => 1,
        <= 16_383 => 2,
        <= 1_073_741_823 => 4,
        _ => 8,
    };

    internal override void Write(long value, Span<byte> destination)
    {
        int size = GetSize(value);
        for (int i = size - 1; i >= 0; i--)
        {
            destination[i] = (byte)value;
            value >>= 8;
        }

        destination[0] |= (byte)(System.Numerics.BitOperations.Log2((uint)size) << 6);
    }
}

internal sealed class HexAsciiPrefix(int digits)
    : LengthPrefix($"HexAscii({digits})", digits, (1L << (4 * digits)) - 1, isFixedSize: true)
{
    internal override PrefixStatus TryRead(ReadOnlySpan<byte> source, out long value, out int size)
    {
        value = 0;
        size = 0;
        if (source.Length < MaxSize)
            return PrefixStatus.NeedMore;

        for (int i = 0; i < MaxSize; i++)
        {
            int nibble = HexValue(source[i]);
            if (nibble < 0)
                return PrefixStatus.Malformed;
            value = (value << 4) | (uint)nibble;
        }

        size = MaxSize;
        return PrefixStatus.Ok;
    }

    internal override int GetSize(long value) => MaxSize;

    internal override void Write(long value, Span<byte> destination)
    {
        for (int i = MaxSize - 1; i >= 0; i--)
        {
            destination[i] = (byte)"0123456789abcdef"[(int)(value & 0xF)];
            value >>= 4;
        }
    }

    internal static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
