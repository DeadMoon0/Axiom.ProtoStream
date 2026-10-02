using System;
using System.Buffers;
using System.Linq;
using System.Text;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Framing;
using Axiom.ProtoStream.Testing;

namespace Axiom.ProtoStream.Tests;

public sealed class FramingTests
{
    public static TheoryData<string> Prefixes => new()
    {
        "UInt8", "UInt16BigEndian", "UInt16LittleEndian", "UInt24BigEndian", "UInt32BigEndian", "UInt32LittleEndian", "VarInt", "QuicVarInt", "HexAscii4",
    };

    private static LengthPrefix PrefixNamed(string name) => name switch
    {
        "UInt8" => LengthPrefix.UInt8,
        "UInt16BigEndian" => LengthPrefix.UInt16BigEndian,
        "UInt16LittleEndian" => LengthPrefix.UInt16LittleEndian,
        "UInt24BigEndian" => LengthPrefix.UInt24BigEndian,
        "UInt32BigEndian" => LengthPrefix.UInt32BigEndian,
        "UInt32LittleEndian" => LengthPrefix.UInt32LittleEndian,
        "VarInt" => LengthPrefix.VarInt,
        "QuicVarInt" => LengthPrefix.QuicVarInt,
        _ => LengthPrefix.HexAscii(4),
    };

    // A frame that straddles network reads must come out the same however the bytes were split.
    [Theory]
    [MemberData(nameof(Prefixes))]
    public void ALengthPrefixedFrameParsesAtEverySplit(string prefixName)
    {
        IFramer framer = Framers.LengthPrefixed(PrefixNamed(prefixName), maxFrameSize: 200);
        byte[] content = Enumerable.Range(0, 130).Select(i => (byte)i).ToArray();
        byte[] wire = Write(framer, content).Concat(new byte[] { 0xEE }).ToArray();

        foreach (ReadOnlySequence<byte> input in Segments.EverySplit(wire))
        {
            Assert.Equal(FrameStatus.Complete, framer.TryReadFrame(input, out ReadOnlySequence<byte> frame, out SequencePosition consumed));
            Assert.Equal(content, frame.ToArray());
            Assert.Equal(new byte[] { 0xEE }, input.Slice(consumed).ToArray());
        }
    }

    // A peer must not make the session wait for, or buffer, bytes it merely announced.
    [Fact]
    public void ADeclaredLengthAboveTheMaximumIsRefusedBeforeTheBytesArrive()
    {
        IFramer framer = Framers.LengthPrefixed(LengthPrefix.UInt32BigEndian, maxFrameSize: 1024);
        var input = new ReadOnlySequence<byte>(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF });

        Assert.Equal(FrameStatus.TooLarge, framer.TryReadFrame(input, out _, out _));
    }

    [Fact]
    public void AnIncompleteFrameNeedsMore()
    {
        IFramer framer = Framers.LengthPrefixed(LengthPrefix.UInt16BigEndian, maxFrameSize: 1024);

        Assert.Equal(FrameStatus.NeedMore, framer.TryReadFrame(new ReadOnlySequence<byte>(new byte[] { 0 }), out _, out _));
        Assert.Equal(FrameStatus.NeedMore, framer.TryReadFrame(new ReadOnlySequence<byte>(new byte[] { 0, 3, 1, 2 }), out _, out _));
    }

    // Non-minimal varints are a smuggling vector: two encodings of one length.
    [Theory]
    [InlineData(new byte[] { 0x80, 0x00 })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]
    public void AMalformedVarIntIsRefused(byte[] prefix)
    {
        IFramer framer = Framers.LengthPrefixed(LengthPrefix.VarInt, maxFrameSize: int.MaxValue);

        Assert.Equal(FrameStatus.Malformed, framer.TryReadFrame(new ReadOnlySequence<byte>(prefix), out _, out _));
    }

    // Examples from RFC 9000 appendix A.1.
    [Theory]
    [InlineData(new byte[] { 0x25 }, 37)]
    [InlineData(new byte[] { 0x7B, 0xBD }, 15293)]
    [InlineData(new byte[] { 0x9D, 0x7F, 0x3E, 0x7D }, 494878333)]
    [InlineData(new byte[] { 0xC2, 0x19, 0x7C, 0x5E, 0xFF, 0x14, 0xE8, 0x8C }, 151288809941952652)]
    public void QuicVarIntsDecodeAsTheRfcSays(byte[] encoded, long expected)
    {
        Assert.Equal(PrefixStatus.Ok, LengthPrefix.QuicVarInt.TryRead(encoded, out long value, out int size));
        Assert.Equal(expected, value);
        Assert.Equal(encoded.Length, size);
    }

    [Fact]
    public void AHexPrefixWithANonHexDigitIsMalformed()
    {
        IFramer framer = Framers.LengthPrefixed(LengthPrefix.HexAscii(4), maxFrameSize: 1000);

        Assert.Equal(FrameStatus.Malformed, framer.TryReadFrame(new ReadOnlySequence<byte>("00g4"u8.ToArray()), out _, out _));
    }

    [Fact]
    public void ADelimitedFrameParsesAtEverySplitAndExcludesTheDelimiter()
    {
        IFramer framer = Framers.Delimited("\r\n"u8, maxFrameSize: 64);
        byte[] wire = "PING :abc\r\nNEXT"u8.ToArray();

        foreach (ReadOnlySequence<byte> input in Segments.EverySplit(wire))
        {
            Assert.Equal(FrameStatus.Complete, framer.TryReadFrame(input, out ReadOnlySequence<byte> frame, out SequencePosition consumed));
            Assert.Equal("PING :abc", Encoding.ASCII.GetString(frame.ToArray()));
            Assert.Equal("NEXT", Encoding.ASCII.GetString(input.Slice(consumed).ToArray()));
        }
    }

    [Fact]
    public void ADelimitedFrameWithoutDelimiterWithinTheMaximumIsTooLarge()
    {
        IFramer framer = Framers.Delimited("\n"u8, maxFrameSize: 4);

        Assert.Equal(FrameStatus.NeedMore, framer.TryReadFrame(new ReadOnlySequence<byte>("abcd"u8.ToArray()), out _, out _));
        Assert.Equal(FrameStatus.TooLarge, framer.TryReadFrame(new ReadOnlySequence<byte>("abcdef"u8.ToArray()), out _, out _));
    }

    // Writing a delimiter inside content would let one message smuggle a second one.
    [Fact]
    public void WritingContentThatContainsTheDelimiterThrowsBeforeAnythingIsWritten()
    {
        IFramer framer = Framers.Delimited("\r\n"u8, maxFrameSize: 64);
        var output = new ArrayBufferWriter<byte>();

        Assert.Throws<ProtocolStateException>(() => framer.WriteFrame("a\r\nQUIT"u8, output));
        Assert.Equal(0, output.WrittenCount);
    }

    // Modbus TCP: transaction(2) protocol(2) length(2) unit(1) pdu; the length counts unit + pdu.
    [Fact]
    public void AHeaderDeclaredLengthFrameIncludesItsHeaderAndTheWriterFillsInTheLength()
    {
        IFramer framer = Framers.HeaderDeclaredLength(lengthOffset: 4, LengthPrefix.UInt16BigEndian, adjust: 0, maxFrameSize: 260);
        byte[] encoded = [0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x11, 0x03, 0x00, 0x6B, 0x00, 0x03];

        var output = new ArrayBufferWriter<byte>();
        framer.WriteFrame(encoded, output);
        byte[] wire = output.WrittenSpan.ToArray();
        Assert.Equal(0x06, wire[5]);

        foreach (ReadOnlySequence<byte> input in Segments.EverySplit(wire))
        {
            Assert.Equal(FrameStatus.Complete, framer.TryReadFrame(input, out ReadOnlySequence<byte> frame, out _));
            Assert.Equal(wire, frame.ToArray());
        }
    }

    // HTTP/2: length(3) type(1) flags(1) stream(4) payload; the length counts the payload only.
    [Fact]
    public void AnHttp2ShapedHeaderUsesTheAdjustment()
    {
        IFramer framer = Framers.HeaderDeclaredLength(lengthOffset: 0, LengthPrefix.UInt24BigEndian, adjust: 6, maxFrameSize: 16384 + 9);
        byte[] wire = [0x00, 0x00, 0x02, 0x08, 0x00, 0x00, 0x00, 0x00, 0x01, 0xAA, 0xBB, 0xCC];

        Assert.Equal(FrameStatus.Complete, framer.TryReadFrame(new ReadOnlySequence<byte>(wire), out ReadOnlySequence<byte> frame, out _));
        Assert.Equal(11, frame.Length);
    }

    [Fact]
    public void AContentLengthHeaderFrameIgnoresOtherHeadersAndHeaderCase()
    {
        IFramer framer = Framers.ContentLengthHeader(maxFrameSize: 100);
        byte[] wire = "content-length: 2\r\nContent-Type: application/vscode-jsonrpc\r\n\r\n{}"u8.ToArray();

        foreach (ReadOnlySequence<byte> input in Segments.EverySplit(wire))
        {
            Assert.Equal(FrameStatus.Complete, framer.TryReadFrame(input, out ReadOnlySequence<byte> frame, out _));
            Assert.Equal("{}", Encoding.ASCII.GetString(frame.ToArray()));
        }
    }

    [Theory]
    [InlineData("Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}")]
    [InlineData("Content-Length: +2\r\n\r\n{}")]
    [InlineData("Content-Length: 0x2\r\n\r\n{}")]
    [InlineData("Content-Type: x\r\n\r\n{}")]
    public void AnAmbiguousOrMissingContentLengthIsMalformed(string text)
    {
        IFramer framer = Framers.ContentLengthHeader(maxFrameSize: 100);

        Assert.Equal(FrameStatus.Malformed, framer.TryReadFrame(new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(text)), out _, out _));
    }

    [Fact]
    public void AContentLengthHeaderFrameRoundTrips()
    {
        IFramer framer = Framers.ContentLengthHeader(maxFrameSize: 100);
        byte[] wire = Write(framer, "{\"id\":1}"u8.ToArray());

        Assert.Equal("Content-Length: 8\r\n\r\n{\"id\":1}", Encoding.ASCII.GetString(wire));
    }

    private static byte[] Write(IFramer framer, byte[] content)
    {
        var output = new ArrayBufferWriter<byte>();
        framer.WriteFrame(content, output);
        return output.WrittenSpan.ToArray();
    }
}
