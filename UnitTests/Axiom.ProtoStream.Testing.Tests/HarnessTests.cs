using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Framing;
using Axiom.ProtoStream.Testing;
using Axiom.ProtoStream.Tests.Shared;

namespace Axiom.ProtoStream.Testing.Tests;

public sealed class PublicSurface : PublicSurfaceTests<ScriptedTransport>;

/// <summary>Each harness passes a correct codec and catches the bug it exists for.</summary>
public sealed class HarnessTests
{
    private static bool SameToy(ToyMessage sent, ToyMessage received) => sent switch
    {
        Hello h => received is Hello r && r.Name == h.Name,
        Data d => received is Data r && r.Payload.SequenceEqual(d.Payload),
        Ping p => received is Ping r && r.Id == p.Id,
        _ => received is Bye,
    };

    [Fact]
    public void ACorrectCodecRoundTripsAtEverySplit()
    {
        ToyMessage[] messages = [new Hello { Name = "ada" }, new Data { Payload = [1, 2, 3] }, new Ping { Id = 5 }, new Bye()];

        CodecHarness.VerifyRoundTrip(Toy.Client(), messages, SameToy);
    }

    // A codec that only looks at the first segment works in unit tests and breaks on the network.
    private sealed class FirstSegmentOnlyCodec : ICodec<ToyMessage, ToyMessage>
    {
        public ParseResult TryParse(ref MessageParseContext context, out ToyMessage message)
        {
            ReadOnlySpan<byte> first = context.Input.FirstSpan;
            message = null!;
            if (first.Length < 1)
                return context.NeedMore();
            message = new Data { Payload = first.ToArray() };
            return context.Done(context.Input.GetPosition(first.Length));
        }

        public WriteResult Write(ToyMessage message, IBufferWriter<byte> output)
        {
            output.Write(((Data)message).Payload);
            return WriteResult.Done;
        }
    }

    [Fact]
    public void ACodecThatIgnoresLaterSegmentsIsCaught()
    {
        ProtocolDefinition<ToyMessage, ToyMessage> broken = Protocol.Describe<ToyMessage, ToyMessage>("broken")
            .Codec(() => new FirstSegmentOnlyCodec())
            .States(s => s.Start("Open").In("Open").On<Data>().Delegate().OnSend<Data>())
            .Build();

        Assert.Throws<HarnessAssertionException>(() =>
            CodecHarness.VerifyRoundTrip(broken, [new Data { Payload = [1, 2, 3, 4] }], SameToy));
    }

    [Fact]
    public void FuzzingAcceptsACorrectCodec()
    {
        byte[][] samples = [Toy.HelloFrame("ada"), Toy.Concat(Toy.PingFrame(1), Toy.Frame(2, 1, 2, 3)), Toy.Frame(5)];

        FuzzReport report = CodecHarness.Fuzz(Toy.Server().Build(), samples, seed: 1234, iterations: 5_000);

        Assert.Equal(5_000, report.Iterations);
        Assert.True(report.Invalid > 0 && report.Messages > 0);
    }

    // The bug fuzzing exists for: input the author never imagined makes the decoder throw.
    private sealed class IndexingFrameCodec : IFrameCodec<ToyMessage, ToyMessage>
    {
        public DecodeResult Decode(in Frame frame, out ToyMessage message)
        {
            byte declared = frame.Memory.Span[0];
            message = new Data { Payload = frame.Memory.Span.Slice(1, declared).ToArray() };
            return DecodeResult.Ok;
        }

        public void Encode(ToyMessage message, IBufferWriter<byte> output) => throw new NotSupportedException();
    }

    [Fact]
    public void FuzzingCatchesADecoderThatTrustsItsInput()
    {
        ProtocolDefinition<ToyMessage, ToyMessage> trusting = Protocol.Describe<ToyMessage, ToyMessage>("trusting")
            .Framing(Toy.Framer)
            .FrameCodec(() => new IndexingFrameCodec())
            .States(s => s.Start("Open").In("Open").On<Data>().Delegate())
            .Build();

        var ex = Assert.Throws<HarnessAssertionException>(() => CodecHarness.Fuzz(trusting, [Toy.Frame(2, 1)], seed: 7, iterations: 2_000));
        Assert.Contains("threw on input", ex.Message, StringComparison.Ordinal);
    }

    // A pooled message that forgets to reset a field shows the previous message's data.
    public sealed class Tagged
    {
        public ReadOnlyMemory<byte> Body { get; set; }

        public string? Tag { get; set; }
    }

    private sealed class LeakyCodec : IFrameCodec<Tagged, Tagged>
    {
        private readonly Tagged _pooled = new();

        public DecodeResult Decode(in Frame frame, out Tagged message)
        {
            _pooled.Body = frame.Memory;
            if (frame.Memory.Span[0] == 0xFF)
                _pooled.Tag = "special"; // never cleared for the next message
            message = _pooled;
            return DecodeResult.Ok;
        }

        public void Encode(Tagged message, IBufferWriter<byte> output) => output.Write(message.Body.Span);
    }

    [Fact]
    public void ReusingAMessageThatKeepsStaleFieldsIsCaught()
    {
        ProtocolDefinition<Tagged, Tagged> leaky = Protocol.Describe<Tagged, Tagged>("leaky")
            .Framing(Toy.Framer)
            .FrameCodec(() => new LeakyCodec())
            .States(s => s.Start("Open").In("Open").On<Tagged>().Delegate())
            .Build();

        Assert.Throws<HarnessAssertionException>(() =>
            MessageReuseContract.Verify(leaky, [0, 1, 0xFF], [0, 1, 0x01], tagged => tagged.Tag ?? "-"));
    }

    [Fact]
    public void DefinitionWarningsAreReported()
    {
        DefinitionAssert.NoWarnings(Toy.Server().Build());

        Assert.Throws<HarnessAssertionException>(() =>
            DefinitionAssert.NoWarnings(Toy.Server().Limits(l => l.NoIdleTimeout()).Build()));
    }

    [Fact]
    public void SegmentsSplitWithoutCopying()
    {
        byte[] data = [1, 2, 3, 4, 5];

        ReadOnlySequence<byte> split = Segments.Split(data, 2, 4);
        Assert.False(split.IsSingleSegment);
        Assert.Equal(data, split.ToArray());
        Assert.Equal(data.Length, Segments.OneBytePerSegment(data).Length);
        Assert.Equal(data.Length + 1, Segments.EverySplit(data).Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => Segments.Split(data, 4, 2));
    }

    [Fact]
    public async Task AScriptedTransportDeliversChunkByChunkAndCapturesOutput()
    {
        var transport = new ScriptedTransport([new byte[] { 1, 2 }, new byte[] { 3 }]);

        ReadResult first = await transport.Input.ReadAsync();
        Assert.Equal(new byte[] { 1, 2 }, first.Buffer.ToArray());
        Assert.False(first.IsCompleted);
        transport.Input.AdvanceTo(first.Buffer.Start, first.Buffer.End);

        ReadResult second = await transport.Input.ReadAsync();
        Assert.Equal(new byte[] { 1, 2, 3 }, second.Buffer.ToArray());
        Assert.False(second.Buffer.IsSingleSegment);
        Assert.True(second.IsCompleted);

        await transport.Output.WriteAsync(new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, transport.Written());
    }

    [Fact]
    public async Task AnInMemoryPairIsCrossWired()
    {
        TransportPair pair = InMemoryTransport.CreatePair();

        await pair.Client.Output.WriteAsync(new byte[] { 1 });
        await pair.Server.Output.WriteAsync(new byte[] { 2 });

        Assert.Equal(new byte[] { 1 }, (await pair.Server.Input.ReadAsync()).Buffer.ToArray());
        Assert.Equal(new byte[] { 2 }, (await pair.Client.Input.ReadAsync()).Buffer.ToArray());
    }
}
