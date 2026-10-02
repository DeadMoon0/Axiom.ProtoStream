using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using ProtoStream.Errors;
using ProtoStream.Framing;

namespace ProtoStream.Codecs;

/// <summary>
/// Writes binary fields; the mirror of <see cref="WireReader"/>. Values that break their declared bounds
/// (a string longer than its maximum, a NUL inside a NUL-terminated string) throw
/// <see cref="ProtocolStateException"/> before anything reaches the connection.
/// </summary>
public readonly ref struct WireWriter
{
    private readonly IBufferWriter<byte> _output;

    /// <summary>Creates a writer that appends to <paramref name="output"/>.</summary>
    public WireWriter(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>One byte.</summary>
    public void UInt8(byte value)
    {
        _output.GetSpan(1)[0] = value;
        _output.Advance(1);
    }

    /// <summary>One signed byte.</summary>
    public void Int8(sbyte value) => UInt8((byte)value);

    /// <summary>One byte, 0 or 1.</summary>
    public void Boolean(bool value) => UInt8(value ? (byte)1 : (byte)0);

    /// <summary>Two bytes, most significant first.</summary>
    public void UInt16BigEndian(ushort value) { BinaryPrimitives.WriteUInt16BigEndian(_output.GetSpan(2), value); _output.Advance(2); }

    /// <summary>Two bytes, least significant first.</summary>
    public void UInt16LittleEndian(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(_output.GetSpan(2), value); _output.Advance(2); }

    /// <summary>Two bytes, most significant first.</summary>
    public void Int16BigEndian(short value) { BinaryPrimitives.WriteInt16BigEndian(_output.GetSpan(2), value); _output.Advance(2); }

    /// <summary>Two bytes, least significant first.</summary>
    public void Int16LittleEndian(short value) { BinaryPrimitives.WriteInt16LittleEndian(_output.GetSpan(2), value); _output.Advance(2); }

    /// <summary>Four bytes, most significant first.</summary>
    public void UInt32BigEndian(uint value) { BinaryPrimitives.WriteUInt32BigEndian(_output.GetSpan(4), value); _output.Advance(4); }

    /// <summary>Four bytes, least significant first.</summary>
    public void UInt32LittleEndian(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(_output.GetSpan(4), value); _output.Advance(4); }

    /// <summary>Four bytes, most significant first.</summary>
    public void Int32BigEndian(int value) { BinaryPrimitives.WriteInt32BigEndian(_output.GetSpan(4), value); _output.Advance(4); }

    /// <summary>Four bytes, least significant first.</summary>
    public void Int32LittleEndian(int value) { BinaryPrimitives.WriteInt32LittleEndian(_output.GetSpan(4), value); _output.Advance(4); }

    /// <summary>Eight bytes, most significant first.</summary>
    public void UInt64BigEndian(ulong value) { BinaryPrimitives.WriteUInt64BigEndian(_output.GetSpan(8), value); _output.Advance(8); }

    /// <summary>Eight bytes, least significant first.</summary>
    public void UInt64LittleEndian(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(_output.GetSpan(8), value); _output.Advance(8); }

    /// <summary>Eight bytes, most significant first.</summary>
    public void Int64BigEndian(long value) { BinaryPrimitives.WriteInt64BigEndian(_output.GetSpan(8), value); _output.Advance(8); }

    /// <summary>Eight bytes, least significant first.</summary>
    public void Int64LittleEndian(long value) { BinaryPrimitives.WriteInt64LittleEndian(_output.GetSpan(8), value); _output.Advance(8); }

    /// <summary>IEEE 754 single precision, most significant byte first.</summary>
    public void SingleBigEndian(float value) { BinaryPrimitives.WriteSingleBigEndian(_output.GetSpan(4), value); _output.Advance(4); }

    /// <summary>IEEE 754 double precision, most significant byte first.</summary>
    public void DoubleBigEndian(double value) { BinaryPrimitives.WriteDoubleBigEndian(_output.GetSpan(8), value); _output.Advance(8); }

    /// <summary>A 16-byte GUID in RFC 9562 (big-endian) byte order.</summary>
    public void GuidBigEndian(Guid value)
    {
        value.TryWriteBytes(_output.GetSpan(16), bigEndian: true, out _);
        _output.Advance(16);
    }

    /// <summary>Unsigned LEB128.</summary>
    public void VarUInt32(uint value) => VarUInt64(value);

    /// <summary>Unsigned LEB128.</summary>
    public void VarUInt64(ulong value)
    {
        Span<byte> span = _output.GetSpan(10);
        int i = 0;
        while (value >= 0x80)
        {
            span[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[i++] = (byte)value;
        _output.Advance(i);
    }

    /// <summary>A length in <paramref name="prefix"/> encoding.</summary>
    public void Length(LengthPrefix prefix, long value)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (value < 0 || value > prefix.MaxValue)
            throw new ProtocolStateException($"The length {value} cannot be encoded as {prefix}.");
        prefix.WriteTo(value, _output);
    }

    /// <summary>UTF-8 text with its length in front; at most <paramref name="maxBytes"/> bytes.</summary>
    public void String(LengthPrefix prefix, string value, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > maxBytes)
            throw new ProtocolStateException($"A string of {byteCount} UTF-8 bytes exceeds its maximum of {maxBytes}.");
        Length(prefix, byteCount);
        WriteUtf8(value, byteCount);
    }

    /// <summary>UTF-8 text without a length; the reader must know where it ends.</summary>
    public void Utf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteUtf8(value, Encoding.UTF8.GetByteCount(value));
    }

    /// <summary>UTF-8 text followed by a zero byte; the text itself must not contain one.</summary>
    public void NullTerminatedString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal))
            throw new ProtocolStateException("A NUL-terminated string contains a NUL character.");
        Utf8(value);
        UInt8(0);
    }

    /// <summary>Raw bytes without a length.</summary>
    public void Bytes(ReadOnlySpan<byte> value) => _output.Write(value);

    /// <summary>Bytes with their length in front; at most <paramref name="maxBytes"/>.</summary>
    public void Bytes(LengthPrefix prefix, ReadOnlySpan<byte> value, int maxBytes)
    {
        if (value.Length > maxBytes)
            throw new ProtocolStateException($"A block of {value.Length} bytes exceeds its maximum of {maxBytes}.");
        Length(prefix, value.Length);
        _output.Write(value);
    }

    private void WriteUtf8(string value, int byteCount)
    {
        Span<byte> span = _output.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(value, span);
        _output.Advance(byteCount);
    }
}

/// <summary>
/// A message that reads and writes itself with <see cref="WireReader"/> and <see cref="WireWriter"/>.
/// Register it in a message set with <c>.Messages(m => m.Add&lt;T&gt;(id))</c>.
/// </summary>
/// <typeparam name="TSelf">The message type itself.</typeparam>
public interface IWireMessage<TSelf> where TSelf : IWireMessage<TSelf>
{
    /// <summary>Reads the message fields. Return <c>reader.Ok</c> at the end.</summary>
    static abstract bool TryRead(ref WireReader reader, out TSelf message);

    /// <summary>Writes the message fields.</summary>
    void Write(ref WireWriter writer);
}
