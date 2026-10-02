using System;
using System.Buffers;
using ProtoStream.Codecs;
using ProtoStream.Errors;
using ProtoStream.Internal;
using ProtoStream.Testing;

namespace ProtoStream.Tests.Core;

/// <summary>The session's side of the reader contract: what a buggy reader cannot get away with.</summary>
public sealed class MessageParserTests
{
    private delegate ParseResult Parse(ref MessageParseContext context, out string message);

    private sealed class Reader(Parse parse) : IMessageReader<string>
    {
        public ParseResult TryParse(ref MessageParseContext context, out string message) => parse(ref context, out message);
    }

    private static ParseOutcome Run(Parse parse, ReadOnlySequence<byte> input, out string message)
    {
        using var parser = new MessageParser(maxBufferedBytes: 1024, payload: null);
        return parser.Parse(new Reader(parse), input, isCompleted: false, out message);
    }

    private static readonly ReadOnlySequence<byte> Abc = new("abc"u8.ToArray());

    [Fact]
    public void AResultNoOutcomeMethodProducedIsACodecBug()
    {
        Assert.Throws<CodecContractException>(() => Run((ref MessageParseContext c, out string m) => { m = ""; return default; }, Abc, out _));
    }

    // A message that consumes nothing would be parsed again forever.
    [Fact]
    public void AMessageThatConsumesNoBytesIsACodecBug()
    {
        Assert.Throws<CodecContractException>(() => Run((ref MessageParseContext c, out string m) => { m = ""; return c.Done(c.Input.Start); }, Abc, out _));
    }

    [Fact]
    public void ReportingTwoOutcomesIsACodecBug()
    {
        Assert.Throws<CodecContractException>(() => Run((ref MessageParseContext c, out string m) =>
        {
            m = "";
            c.NeedMore();
            return c.Done(c.Input.End);
        }, Abc, out _));
    }

    [Fact]
    public void AThrowingReaderIsReportedAsACodecBugWithTheCause()
    {
        var ex = Assert.Throws<CodecContractException>(() => Run((ref MessageParseContext c, out string m) => throw new FormatException("boom"), Abc, out _));
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void APositionOutsideTheInputIsACodecBug()
    {
        var other = new ReadOnlySequence<byte>(new byte[10]);
        Assert.Throws<CodecContractException>(() => Run((ref MessageParseContext c, out string m) => { m = ""; return c.Done(other.GetPosition(8)); }, Abc, out _));
    }

    [Fact]
    public void ADoneMessageReportsWhereItEnded()
    {
        ParseOutcome outcome = Run((ref MessageParseContext c, out string m) => { m = "ab"; return c.Done(c.Input.GetPosition(2)); }, Abc, out string message);

        Assert.Equal(ParseOutcomeKind.Message, outcome.Kind);
        Assert.False(outcome.Detached);
        Assert.Equal("ab", message);
        Assert.Equal("c"u8.ToArray(), Abc.Slice(outcome.Consumed).ToArray());
    }

    // A detached head lives in session memory, so the connection can be advanced at once.
    [Fact]
    public void ADetachedHeadIsACopyAndMarksTheMessageDetached()
    {
        ReadOnlySequence<byte> input = Segments.Split("abcdef"u8.ToArray(), 2, 4);
        ReadOnlyMemory<byte> head = default;

        ParseOutcome outcome = Run((ref MessageParseContext c, out string m) =>
        {
            DetachedHead detached = c.Detach(c.Input.GetPosition(5));
            head = detached.Memory;
            m = "";
            return c.Done(detached);
        }, input, out _);

        Assert.True(outcome.Detached);
        Assert.Equal("abcde"u8.ToArray(), head.ToArray());
    }

    [Fact]
    public void ADetachedHeadFromAnotherParseIsRefused()
    {
        using var parser = new MessageParser(1024, payload: null);
        DetachedHead first = default;
        parser.Parse(new Reader((ref MessageParseContext c, out string m) =>
        {
            first = c.Detach(c.Input.GetPosition(1));
            m = "";
            return c.NeedMore();
        }), Abc, false, out _);

        Assert.Throws<CodecContractException>(() => parser.Parse(new Reader((ref MessageParseContext c, out string m) =>
        {
            m = "";
            return c.Done(first);
        }), Abc, false, out _));
    }

    [Fact]
    public void ContiguousMemoryIsTheInputItselfWhenItIsOneSegmentAndACopyOtherwise()
    {
        byte[] single = "abc"u8.ToArray();
        ReadOnlyMemory<byte> view = default;
        Run((ref MessageParseContext c, out string m) => { view = c.AsContiguous(c.Input); m = ""; return c.Done(c.Input.End); }, new ReadOnlySequence<byte>(single), out _);
        single[0] = (byte)'z';
        Assert.Equal((byte)'z', view.Span[0]);

        Run((ref MessageParseContext c, out string m) => { view = c.AsContiguous(c.Input); m = ""; return c.Done(c.Input.End); }, Segments.Split("abc"u8.ToArray(), 1), out _);
        Assert.Equal("abc"u8.ToArray(), view.ToArray());
    }

    // One scratch buffer per parse: a second copy would silently overwrite the first.
    [Fact]
    public void CopyingTwiceInOneParseIsACodecBug()
    {
        Assert.Throws<CodecContractException>(() => Run((ref MessageParseContext c, out string m) =>
        {
            c.Detach(c.Input.GetPosition(1));
            c.Detach(c.Input.GetPosition(2));
            m = "";
            return c.NeedMore();
        }, Abc, out _));
    }

    [Fact]
    public void AStampGoesStaleWhenTheSessionReadsOn()
    {
        using var parser = new MessageParser(1024, payload: null);
        MessageStamp stamp = parser.Stamp;

        Assert.True(stamp.IsCurrent);
        parser.Generation.Advance();
        Assert.False(stamp.IsCurrent);
        Assert.Throws<StaleMessageException>(stamp.ThrowIfStale);
        Assert.True(default(MessageStamp).IsCurrent);
    }

    [Fact]
    public void AnInvalidResultCarriesItsResumePosition()
    {
        ParseOutcome outcome = Run((ref MessageParseContext c, out string m) =>
        {
            m = "";
            return c.Invalid(ViolationCode.InvalidData, "bad", c.Input.GetPosition(1));
        }, Abc, out _);

        Assert.Equal(ParseOutcomeKind.Invalid, outcome.Kind);
        Assert.True(outcome.HasResumeAt);
        Assert.Equal(ViolationCode.InvalidData, outcome.Code);
    }

    [Fact]
    public void AFixedLengthPayloadReportsItsDataAndEnds()
    {
        var decoder = new FixedLengthPayloadDecoder();
        decoder.Reset(3);

        PayloadStep step = decoder.Next(new ReadOnlySequence<byte>(new byte[5]), isCompleted: false);
        Assert.Equal(PayloadStepKind.Data, step.Kind);
        Assert.Equal(3, step.Length);

        decoder.OnConsumed(3);
        Assert.Equal(PayloadStepKind.End, decoder.Next(ReadOnlySequence<byte>.Empty, false).Kind);
    }

    [Fact]
    public void AFixedLengthPayloadCutShortByTheConnectionIsTruncated()
    {
        var decoder = new FixedLengthPayloadDecoder();
        decoder.Reset(3);

        PayloadStep step = decoder.Next(ReadOnlySequence<byte>.Empty, isCompleted: true);
        Assert.Equal(PayloadStepKind.Invalid, step.Kind);
        Assert.Equal(ViolationCode.Truncated, step.Code);
    }
}
