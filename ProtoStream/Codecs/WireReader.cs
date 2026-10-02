using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;
using ProtoStream.Framing;

namespace ProtoStream.Codecs;

/// <summary>
/// Reads binary fields from one frame. Never throws and never reads past the frame: the first read that
/// does not fit, or a value outside its declared bounds, makes the reader fail, and every later read
/// returns a default value. Check <see cref="Ok"/> once at the end.
/// </summary>
/// <remarks>
/// <code>
/// var id = reader.UInt32BigEndian();
/// var name = reader.String(LengthPrefix.UInt8, maxBytes: 64);
/// message = new Hello { Id = id, Name = name };
/// return reader.Ok;
/// </code>
/// Every variable-size read takes a maximum, and the declared size is checked against it and against the
/// bytes left in the frame before anything is allocated.
/// </remarks>
public ref struct WireReader
{
    private readonly ReadOnlyMemory<byte> _memory;
    private readonly ReadOnlySpan<byte> _span;
    private int _position;
    private bool _failed;

    /// <summary>Creates a reader over one frame.</summary>
    public WireReader(ReadOnlyMemory<byte> memory)
    {
        _memory = memory;
        _span = memory.Span;
        _position = 0;
        _failed = false;
    }

    /// <summary>True while every read so far fit and was valid.</summary>
    public readonly bool Ok => !_failed;

    /// <summary>Bytes read so far.</summary>
    public readonly int Position => _position;

    /// <summary>Bytes left to read; zero after a failure.</summary>
    public readonly int Remaining => _failed ? 0 : _span.Length - _position;

    /// <summary>True when the reader has not failed and every byte was read.</summary>
    public readonly bool IsAtEnd => !_failed && _position == _span.Length;

    /// <summary>Marks the reader as failed, for validation the reader cannot know about. Returns false.</summary>
    public bool Fail()
    {
        _failed = true;
        return false;
    }

    /// <summary>One byte.</summary>
    public byte UInt8() => Take(1, out ReadOnlySpan<byte> b) ? b[0] : default;

    /// <summary>One signed byte.</summary>
    public sbyte Int8() => (sbyte)UInt8();

    /// <summary>One byte that must be 0 or 1.</summary>
    public bool Boolean()
    {
        byte value = UInt8();
        if (value > 1)
            Fail();
        return value == 1;
    }

    /// <summary>Two bytes, most significant first.</summary>
    public ushort UInt16BigEndian() => Take(sizeof(ushort), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadUInt16BigEndian(b) : default;

    /// <summary>Two bytes, least significant first.</summary>
    public ushort UInt16LittleEndian() => Take(sizeof(ushort), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadUInt16LittleEndian(b) : default;

    /// <summary>Two bytes, most significant first.</summary>
    public short Int16BigEndian() => Take(sizeof(ushort), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadInt16BigEndian(b) : default;

    /// <summary>Two bytes, least significant first.</summary>
    public short Int16LittleEndian() => Take(sizeof(ushort), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadInt16LittleEndian(b) : default;

    /// <summary>Four bytes, most significant first.</summary>
    public uint UInt32BigEndian() => Take(sizeof(uint), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadUInt32BigEndian(b) : default;

    /// <summary>Four bytes, least significant first.</summary>
    public uint UInt32LittleEndian() => Take(sizeof(uint), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadUInt32LittleEndian(b) : default;

    /// <summary>Four bytes, most significant first.</summary>
    public int Int32BigEndian() => Take(sizeof(uint), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadInt32BigEndian(b) : default;

    /// <summary>Four bytes, least significant first.</summary>
    public int Int32LittleEndian() => Take(sizeof(uint), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadInt32LittleEndian(b) : default;

    /// <summary>Eight bytes, most significant first.</summary>
    public ulong UInt64BigEndian() => Take(sizeof(ulong), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadUInt64BigEndian(b) : default;

    /// <summary>Eight bytes, least significant first.</summary>
    public ulong UInt64LittleEndian() => Take(sizeof(ulong), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadUInt64LittleEndian(b) : default;

    /// <summary>Eight bytes, most significant first.</summary>
    public long Int64BigEndian() => Take(sizeof(ulong), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadInt64BigEndian(b) : default;

    /// <summary>Eight bytes, least significant first.</summary>
    public long Int64LittleEndian() => Take(sizeof(ulong), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadInt64LittleEndian(b) : default;

    /// <summary>IEEE 754 single precision, most significant byte first.</summary>
    public float SingleBigEndian() => Take(sizeof(float), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadSingleBigEndian(b) : default;

    /// <summary>IEEE 754 double precision, most significant byte first.</summary>
    public double DoubleBigEndian() => Take(sizeof(double), out ReadOnlySpan<byte> b) ? BinaryPrimitives.ReadDoubleBigEndian(b) : default;

    /// <summary>A 16-byte GUID in RFC 9562 (big-endian) byte order.</summary>
    public Guid GuidBigEndian() => Take(Leb128.GuidSize, out ReadOnlySpan<byte> b) ? new Guid(b, bigEndian: true) : default;

    /// <summary>Unsigned LEB128 of at most five bytes; non-minimal encodings fail.</summary>
    public uint VarUInt32()
    {
        ulong value = ReadVarInt(Leb128.MaxBytes32);
        if (value > uint.MaxValue)
            Fail();
        return _failed ? 0 : (uint)value;
    }

    /// <summary>Unsigned LEB128 of at most ten bytes; non-minimal encodings fail.</summary>
    public ulong VarUInt64() => ReadVarInt(Leb128.MaxBytes64);

    /// <summary>A length in <paramref name="prefix"/> encoding that must not exceed <paramref name="max"/>.</summary>
    public long Length(LengthPrefix prefix, long max)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (_failed)
            return 0;

        if (prefix.TryRead(_span[_position..], out long value, out int size) != PrefixStatus.Ok || value > max)
        {
            Fail();
            return 0;
        }

        _position += size;
        return value;
    }

    /// <summary>
    /// An element count of at most <paramref name="maxCount"/>, also limited to the bytes left (every
    /// element is assumed to take at least one byte), so a forged count cannot size an allocation.
    /// </summary>
    public int Count(LengthPrefix prefix, int maxCount)
    {
        long count = Length(prefix, maxCount);
        if (count > Remaining)
        {
            Fail();
            return 0;
        }

        return (int)count;
    }

    /// <summary>UTF-8 text of at most <paramref name="maxBytes"/> bytes with its length in front. Invalid UTF-8 fails.</summary>
    public string String(LengthPrefix prefix, int maxBytes) => Utf8((int)Length(prefix, maxBytes));

    /// <summary>Exactly <paramref name="byteCount"/> bytes of UTF-8 text. Invalid UTF-8 fails.</summary>
    public string Utf8(int byteCount)
    {
        if (!Take(byteCount, out ReadOnlySpan<byte> bytes))
            return string.Empty;
        if (!System.Text.Unicode.Utf8.IsValid(bytes))
        {
            Fail();
            return string.Empty;
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>UTF-8 text ended by a zero byte, at most <paramref name="maxBytes"/> bytes before it.</summary>
    public string NullTerminatedString(int maxBytes)
    {
        if (_failed)
            return string.Empty;

        ReadOnlySpan<byte> rest = _span[_position..];
        int end = rest[..Math.Min(rest.Length, maxBytes + 1)].IndexOf((byte)0);
        if (end < 0)
        {
            Fail();
            return string.Empty;
        }

        string value = Utf8(end);
        Skip(1);
        return value;
    }

    /// <summary>Exactly <paramref name="count"/> bytes as a view of the frame, valid until the next read on the session.</summary>
    public ReadOnlyMemory<byte> BytesView(int count)
    {
        int start = _position;
        return Take(count, out _) ? _memory.Slice(start, count) : default;
    }

    /// <summary>At most <paramref name="maxBytes"/> bytes with their length in front, as a view of the frame.</summary>
    public ReadOnlyMemory<byte> BytesView(LengthPrefix prefix, int maxBytes) => BytesView((int)Length(prefix, maxBytes));

    /// <summary>Exactly <paramref name="count"/> bytes as an owned copy.</summary>
    public byte[] Bytes(int count) => BytesView(count).ToArray();

    /// <summary>At most <paramref name="maxBytes"/> bytes with their length in front, as an owned copy.</summary>
    public byte[] Bytes(LengthPrefix prefix, int maxBytes) => BytesView(prefix, maxBytes).ToArray();

    /// <summary>Everything left in the frame, as a view.</summary>
    public ReadOnlyMemory<byte> RestView() => BytesView(Remaining);

    /// <summary>Skips <paramref name="count"/> bytes.</summary>
    public void Skip(int count) => Take(count, out _);

    private bool Take(int count, out ReadOnlySpan<byte> bytes)
    {
        if (_failed || count < 0 || count > _span.Length - _position)
        {
            _failed = true;
            bytes = default;
            return false;
        }

        bytes = _span.Slice(_position, count);
        _position += count;
        return true;
    }

    private ulong ReadVarInt(int maxBytes)
    {
        ulong value = 0;
        for (int i = 0; i < maxBytes; i++)
        {
            byte b = UInt8();
            if (_failed)
                return 0;

            value |= (ulong)(b & Leb128.PayloadMask) << (Leb128.PayloadBits * i);
            if ((b & Leb128.ContinuationBit) == 0)
            {
                if (i > 0 && b == 0)
                    break; // non-minimal
                if (i == Leb128.MaxBytes64 - 1 && b > Leb128.LastByteMax64)
                    break; // more than 64 bits
                return value;
            }
        }

        Fail();
        return 0;
    }
}
