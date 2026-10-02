using System;
using System.Buffers;
using ProtoStream.Codecs;
using ProtoStream.Errors;
using ProtoStream.Framing;

namespace ProtoStream.Tests.Core;

public sealed class WireTests
{
    [Fact]
    public void EveryPrimitiveRoundTrips()
    {
        var output = new ArrayBufferWriter<byte>();
        var w = new WireWriter(output);
        w.UInt8(0xAB);
        w.Int8(-5);
        w.Boolean(true);
        w.UInt16BigEndian(0x1234);
        w.UInt16LittleEndian(0x1234);
        w.Int16BigEndian(-2);
        w.Int16LittleEndian(-3);
        w.UInt32BigEndian(0xDEADBEEF);
        w.UInt32LittleEndian(0xDEADBEEF);
        w.Int32BigEndian(-7);
        w.Int32LittleEndian(-8);
        w.UInt64BigEndian(ulong.MaxValue - 1);
        w.UInt64LittleEndian(42);
        w.Int64BigEndian(long.MinValue);
        w.Int64LittleEndian(-1);
        w.SingleBigEndian(1.5f);
        w.DoubleBigEndian(-2.25);
        Guid guid = Guid.NewGuid();
        w.GuidBigEndian(guid);
        w.VarUInt32(300);
        w.VarUInt64(ulong.MaxValue);
        w.String(LengthPrefix.UInt8, "grüß", maxBytes: 16);
        w.NullTerminatedString("abc");
        w.Bytes(LengthPrefix.VarInt, new byte[] { 1, 2, 3 }, maxBytes: 8);

        var r = new WireReader(output.WrittenMemory);
        Assert.Equal(0xAB, r.UInt8());
        Assert.Equal(-5, r.Int8());
        Assert.True(r.Boolean());
        Assert.Equal(0x1234, r.UInt16BigEndian());
        Assert.Equal(0x1234, r.UInt16LittleEndian());
        Assert.Equal(-2, r.Int16BigEndian());
        Assert.Equal(-3, r.Int16LittleEndian());
        Assert.Equal(0xDEADBEEF, r.UInt32BigEndian());
        Assert.Equal(0xDEADBEEF, r.UInt32LittleEndian());
        Assert.Equal(-7, r.Int32BigEndian());
        Assert.Equal(-8, r.Int32LittleEndian());
        Assert.Equal(ulong.MaxValue - 1, r.UInt64BigEndian());
        Assert.Equal(42UL, r.UInt64LittleEndian());
        Assert.Equal(long.MinValue, r.Int64BigEndian());
        Assert.Equal(-1L, r.Int64LittleEndian());
        Assert.Equal(1.5f, r.SingleBigEndian());
        Assert.Equal(-2.25, r.DoubleBigEndian());
        Assert.Equal(guid, r.GuidBigEndian());
        Assert.Equal(300U, r.VarUInt32());
        Assert.Equal(ulong.MaxValue, r.VarUInt64());
        Assert.Equal("grüß", r.String(LengthPrefix.UInt8, maxBytes: 16));
        Assert.Equal("abc", r.NullTerminatedString(maxBytes: 8));
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Bytes(LengthPrefix.VarInt, maxBytes: 8));
        Assert.True(r.IsAtEnd);
    }

    // A reader never reads past its frame; after the first miss every value is default and Ok is false.
    [Fact]
    public void ReadingPastTheEndFailsAndStaysFailed()
    {
        var r = new WireReader(new byte[] { 1, 2, 3 });

        Assert.Equal(0U, r.UInt32BigEndian());
        Assert.False(r.Ok);
        Assert.Equal(0, r.UInt8());
        Assert.Equal(0, r.Remaining);
    }

    // The declared length is checked before the string is decoded, so a forged length cannot size an allocation.
    [Fact]
    public void AStringLongerThanItsMaximumOrThanTheFrameFails()
    {
        var tooLong = new WireReader(new byte[] { 5, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e' });
        Assert.Equal(string.Empty, tooLong.String(LengthPrefix.UInt8, maxBytes: 4));
        Assert.False(tooLong.Ok);

        var beyondFrame = new WireReader(new byte[] { 0x00, 0x00, 0xFF, 0xFF, (byte)'a' });
        Assert.Equal(string.Empty, beyondFrame.String(LengthPrefix.UInt32BigEndian, maxBytes: int.MaxValue));
        Assert.False(beyondFrame.Ok);
    }

    [Fact]
    public void InvalidUtf8Fails()
    {
        var r = new WireReader(new byte[] { 2, 0xC3, 0x28 });

        r.String(LengthPrefix.UInt8, maxBytes: 8);
        Assert.False(r.Ok);
    }

    [Fact]
    public void ACountLargerThanTheRemainingBytesFails()
    {
        var r = new WireReader(new byte[] { 0, 100, 1, 2 });

        Assert.Equal(0, r.Count(LengthPrefix.UInt16BigEndian, maxCount: 1000));
        Assert.False(r.Ok);
    }

    [Fact]
    public void ABooleanOtherThanZeroOrOneFails()
    {
        var r = new WireReader(new byte[] { 2 });

        r.Boolean();
        Assert.False(r.Ok);
    }

    [Fact]
    public void ANonMinimalVarIntFails()
    {
        var r = new WireReader(new byte[] { 0x80, 0x00 });

        r.VarUInt32();
        Assert.False(r.Ok);
    }

    [Fact]
    public void AViewReferencesTheFrameInsteadOfCopying()
    {
        byte[] frame = [1, 2, 3, 4];
        var r = new WireReader(frame);
        r.UInt8();

        ReadOnlyMemory<byte> view = r.BytesView(2);
        frame[1] = 9;

        Assert.Equal(9, view.Span[0]);
    }

    [Fact]
    public void WritingAStringOverItsMaximumThrows()
    {
        var output = new ArrayBufferWriter<byte>();

        Assert.Throws<ProtocolStateException>(() => new WireWriter(output).String(LengthPrefix.UInt8, "abcdef", maxBytes: 3));
        Assert.Throws<ProtocolStateException>(() => new WireWriter(output).String(LengthPrefix.UInt8, new string('x', 300), maxBytes: 1000));
        Assert.Throws<ProtocolStateException>(() => new WireWriter(output).NullTerminatedString("a\0b"));
    }
}
