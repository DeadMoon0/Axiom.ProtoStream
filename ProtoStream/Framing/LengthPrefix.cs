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
    public static LengthPrefix UInt8 { get; } = new FixedSizePrefix("UInt8", sizeof(byte), bigEndian: true);

    /// <summary>Two bytes, most significant first.</summary>
    public static LengthPrefix UInt16BigEndian { get; } = new FixedSizePrefix("UInt16BigEndian", sizeof(ushort), bigEndian: true);

    /// <summary>Two bytes, least significant first.</summary>
    public static LengthPrefix UInt16LittleEndian { get; } = new FixedSizePrefix("UInt16LittleEndian", sizeof(ushort), bigEndian: false);

    /// <summary>Three bytes, most significant first (HTTP/2 frame length).</summary>
    public static LengthPrefix UInt24BigEndian { get; } = new FixedSizePrefix("UInt24BigEndian", UInt24Size, bigEndian: true);

    /// <summary>Four bytes, most significant first, limited to <see cref="int.MaxValue"/>.</summary>
    public static LengthPrefix UInt32BigEndian { get; } = new FixedSizePrefix("UInt32BigEndian", sizeof(uint), bigEndian: true);

    /// <summary>Four bytes, least significant first, limited to <see cref="int.MaxValue"/>.</summary>
    public static LengthPrefix UInt32LittleEndian { get; } = new FixedSizePrefix("UInt32LittleEndian", sizeof(uint), bigEndian: false);

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
        if (digits is < 1 or > MaxHexDigits)
            throw new ArgumentOutOfRangeException(nameof(digits), digits, $"A hex length prefix has 1 to {MaxHexDigits} digits.");
        return new HexAsciiPrefix(digits);
    }

    /// <summary>Size of a three-byte length.</summary>
    private const int UInt24Size = 3;

    /// <summary>Most digits of a hex length: 15 hex digits cannot overflow a 64-bit length.</summary>
    private const int MaxHexDigits = 15;

    /// <summary>Bits in one byte.</summary>
    private protected const int BitsPerByte = 8;

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

        Span<byte> copy = stackalloc byte[MaxHexDigits + 1];
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

/// <summary>Constants of unsigned LEB128 varints (protobuf, MQTT, WebAssembly).</summary>
internal static class Leb128
{
    /// <summary>Set on every byte but the last.</summary>
    public const byte ContinuationBit = 0x80;

    /// <summary>The seven value bits of a byte.</summary>
    public const byte PayloadMask = 0x7F;

    /// <summary>Value bits carried per byte.</summary>
    public const int PayloadBits = 7;

    /// <summary>Most bytes of a 32-bit value: ceil(32 / 7).</summary>
    public const int MaxBytes32 = 5;

    /// <summary>Most bytes of a 64-bit value: ceil(64 / 7).</summary>
    public const int MaxBytes64 = 10;

    /// <summary>The tenth byte of a 64-bit value can only carry bit 63.</summary>
    public const byte LastByteMax64 = 1;

    /// <summary>Size of a GUID.</summary>
    public const int GuidSize = 16;
}

internal enum PrefixStatus : byte
{
    Ok,
    NeedMore,
    Malformed,
}

internal sealed class FixedSizePrefix(string name, int size, bool bigEndian)
    : LengthPrefix(name, size, size >= sizeof(int) ? int.MaxValue : (1L << (BitsPerByte * size)) - 1, isFixedSize: true)
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
            value = (value << BitsPerByte) | source[index];
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
            destination[index] = (byte)(value >> (BitsPerByte * i));
        }
    }
}

internal sealed class VarIntPrefix() : LengthPrefix("VarInt", Leb128.MaxBytes32, int.MaxValue, isFixedSize: false)
{
    internal override PrefixStatus TryRead(ReadOnlySpan<byte> source, out long value, out int size)
    {
        value = 0;
        size = 0;
        for (int i = 0; i < Leb128.MaxBytes32; i++)
        {
            if (i >= source.Length)
                return PrefixStatus.NeedMore;

            byte b = source[i];
            value |= (long)(b & Leb128.PayloadMask) << (Leb128.PayloadBits * i);
            if ((b & Leb128.ContinuationBit) == 0)
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
        while ((value >>= Leb128.PayloadBits) != 0)
            size++;
        return size;
    }

    internal override void Write(long value, Span<byte> destination)
    {
        int i = 0;
        while (value >= Leb128.ContinuationBit)
        {
            destination[i++] = (byte)(value | Leb128.ContinuationBit);
            value >>= Leb128.PayloadBits;
        }

        destination[i] = (byte)value;
    }
}

internal sealed class QuicVarIntPrefix() : LengthPrefix("QuicVarInt", sizeof(ulong), (1L << ValueBits(sizeof(ulong))) - 1, isFixedSize: false)
{
    /// <summary>The top two bits of the first byte hold log2 of the encoded size.</summary>
    private const int SizeBits = 2;

    private const int SizeShift = BitsPerByte - SizeBits;

    private const byte FirstByteValueMask = (1 << SizeShift) - 1;

    private static int ValueBits(int size) => BitsPerByte * size - SizeBits;

    private static long MaxValueFor(int size) => (1L << ValueBits(size)) - 1;

    internal override PrefixStatus TryRead(ReadOnlySpan<byte> source, out long value, out int size)
    {
        value = 0;
        size = 0;
        if (source.IsEmpty)
            return PrefixStatus.NeedMore;

        int length = 1 << (source[0] >> SizeShift);
        if (source.Length < length)
            return PrefixStatus.NeedMore;

        value = source[0] & FirstByteValueMask;
        for (int i = 1; i < length; i++)
            value = (value << BitsPerByte) | source[i];

        size = length;
        return PrefixStatus.Ok;
    }

    internal override int GetSize(long value)
    {
        foreach (int size in (ReadOnlySpan<int>)[sizeof(byte), sizeof(ushort), sizeof(uint)])
        {
            if (value <= MaxValueFor(size))
                return size;
        }

        return sizeof(ulong);
    }

    internal override void Write(long value, Span<byte> destination)
    {
        int size = GetSize(value);
        for (int i = size - 1; i >= 0; i--)
        {
            destination[i] = (byte)value;
            value >>= BitsPerByte;
        }

        destination[0] |= (byte)(System.Numerics.BitOperations.Log2((uint)size) << SizeShift);
    }
}

internal sealed class HexAsciiPrefix(int digits)
    : LengthPrefix($"HexAscii({digits})", digits, (1L << (BitsPerHexDigit * digits)) - 1, isFixedSize: true)
{
    private const int BitsPerHexDigit = 4;
    private const string LowerHexDigits = "0123456789abcdef";

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
            value = (value << BitsPerHexDigit) | (uint)nibble;
        }

        size = MaxSize;
        return PrefixStatus.Ok;
    }

    internal override int GetSize(long value) => MaxSize;

    internal override void Write(long value, Span<byte> destination)
    {
        for (int i = MaxSize - 1; i >= 0; i--)
        {
            destination[i] = (byte)LowerHexDigits[(int)(value & (LowerHexDigits.Length - 1))];
            value >>= BitsPerHexDigit;
        }
    }

    /// <returns>The value of a hex digit, or -1 for any other byte.</returns>
    internal static int HexValue(byte b) => LowerHexDigits.IndexOf(char.ToLowerInvariant((char)b), StringComparison.Ordinal);
}
