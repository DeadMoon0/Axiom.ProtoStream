using System;
using System.Buffers;
using System.Linq;
using ProtoStream.Codecs;
using ProtoStream.Errors;
using ProtoStream.Framing;

namespace ProtoStream.Tests.Core;

public sealed class DefinitionTests
{
    [Fact]
    public void TheToyServerBuildsWithoutWarnings()
    {
        ProtocolDefinition<ToyMessage, ToyMessage> definition = Toy.Server().Build();

        Assert.Empty(definition.Warnings);
        Assert.Equal(["AwaitHello", "Open", "Closed"], definition.States);
    }

    // Fixing a definition should take one round trip, not one per mistake.
    [Fact]
    public void EveryProblemIsReportedAtOnce()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() => Toy.Describe()
            .States(s => s
                .In("A").On<Hello>().Delegate().GoTo("Nowhere")
                .In("B").On<Data>().Delegate())
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("No start state"));
        Assert.Contains(ex.Errors, e => e.Contains("'Nowhere'"));
    }

    [Fact]
    public void AnUnreachableStateIsAnError()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() => Toy.Describe()
            .States(s => s
                .Start("A")
                .In("A").On<Data>().Delegate()
                .In("Island").On<Data>().Delegate())
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("'Island' cannot be reached"));
    }

    // Two descriptions of the same message must not silently overwrite each other.
    [Fact]
    public void DescribingAMessageTwiceInAStateIsAnError()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() => Toy.Describe()
            .States(s => s
                .Start("A")
                .In("A").On<Data>().Delegate().On<Data>().Wait())
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("describes Data twice"));
    }

    [Fact]
    public void DescribingAStateTwiceIsAnError()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() => Toy.Describe()
            .States(s => s
                .Start("A")
                .In("A").On<Data>().Delegate()
                .In("A").On<Ping>().Delegate())
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("'A' is described twice"));
    }

    [Fact]
    public void AFinalStateWithTransitionsIsAnError()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() => Toy.Describe()
            .States(s => s
                .Start("A")
                .In("A").On<Bye>().Wait().GoTo("End")
                .In("End").On<Data>().Delegate()
                .Final("End"))
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("Final state 'End'"));
    }

    private sealed class Unregistered : ToyMessage
    {
    }

    [Fact]
    public void AMessageTypeTheSetCannotDecodeIsAnError()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() => Toy.Describe()
            .States(s => s.Start("A").In("A").On<Unregistered>().Delegate())
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("Unregistered arrives"));
    }

    [Fact]
    public void ADuplicateMessageIdIsAnError()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() => Protocol.Describe<ToyMessage, ToyMessage>("dup")
            .Framing(Toy.Framer)
            .Messages(m => m.Add<Hello>(1).Add<Data>(1))
            .States(s => s.Start("A").In("A").On<Hello>().Delegate())
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("Message id 1 is registered twice"));
    }

    [Fact]
    public void AMessageIdThatDoesNotFitTheDiscriminatorIsAnError()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() => Protocol.Describe<ToyMessage, ToyMessage>("big id")
            .Framing(Toy.Framer)
            .Messages(m => m.Add<Hello>(300))
            .States(s => s.Start("A").In("A").On<Hello>().Delegate())
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("cannot be encoded"));
    }

    // Turning a protection off is allowed, but never silent.
    [Fact]
    public void OptingOutOfALimitIsAWarningAndAnErrorInStrictBuilds()
    {
        ProtocolDefinition<ToyMessage, ToyMessage> definition = Toy.Server().Limits(l => l.NoIdleTimeout()).Build();
        Assert.Contains(definition.Warnings, w => w.Contains("idle timeout is off"));

        Assert.Throws<ProtocolDefinitionException>(() =>
            Toy.Server().Limits(l => l.NoIdleTimeout()).Build(new ProtocolBuildOptions { WarningsAsErrors = true }));
    }

    [Fact]
    public void AnInfiniteTimeoutWithoutTheOptOutIsAnError()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() =>
            Toy.Server().Limits(l => l.IdleTimeout(TimeSpan.Zero)).Build());

        Assert.Contains(ex.Errors, e => e.Contains("NoIdleTimeout()"));
    }

    [Fact]
    public void AFrameLargerThanTheBufferLimitIsAnError()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() =>
            Toy.Server().Limits(l => l.MaxBufferedBytes(1000)).Build());

        Assert.Contains(ex.Errors, e => e.Contains("cannot hold a frame"));
    }

    [Fact]
    public void AStateThatAcceptsAndSendsNothingIsAWarning()
    {
        ProtocolDefinition<ToyMessage, ToyMessage> definition = Toy.Describe()
            .States(s => s
                .Start("A")
                .In("A").On<Data>().Delegate().GoTo("Stuck")
                .In("Stuck"))
            .Build();

        Assert.Contains(definition.Warnings, w => w.Contains("'Stuck' accepts and sends nothing"));
    }

    private sealed class StrictCodec : ICodec<ToyMessage, ToyMessage>
    {
        public ParseResult TryParse(ref MessageParseContext context, out ToyMessage message) => throw new NotSupportedException();

        public WriteResult Write(ToyMessage message, IBufferWriter<byte> output) => throw new NotSupportedException();
    }

    [Fact]
    public void SkippingViolationsWithACodecThatCannotResumeIsAWarning()
    {
        ProtocolDefinition<ToyMessage, ToyMessage> definition = Protocol.Describe<ToyMessage, ToyMessage>("strict")
            .Codec(() => new StrictCodec())
            .States(s => s.Start("A").In("A").On<Data>().Delegate())
            .OnViolation(ViolationAction.Skip)
            .Build();

        Assert.Contains(definition.Warnings, w => w.Contains("OnViolation(Skip) has no effect"));
    }

    [Fact]
    public void ACodecFactoryThatFailsIsAnError()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() => Protocol.Describe<ToyMessage, ToyMessage>("broken")
            .Codec(() => throw new InvalidOperationException("no codec today"))
            .States(s => s.Start("A").In("A").On<Data>().Delegate())
            .Build());

        Assert.Contains(ex.Errors, e => e.Contains("no codec today"));
    }

    [Fact]
    public void AMessageSetRoundTripsEveryRegisteredMessageAndRefusesUnknownIds()
    {
        ProtocolDefinition<ToyMessage, ToyMessage> definition = Toy.Server().Build();
        ICodec<ToyMessage, ToyMessage> codec = definition.CodecFactory();
        var output = new ArrayBufferWriter<byte>();
        codec.Write(new Hello { Name = "ada" }, output);

        Assert.Equal(Toy.HelloFrame("ada"), output.WrittenSpan.ToArray());

        using var parser = new ProtoStream.Internal.MessageParser(4096, payload: null);
        ProtoStream.Internal.ParseOutcome outcome = parser.Parse(codec, new ReadOnlySequence<byte>(output.WrittenMemory), false, out ToyMessage message);
        Assert.Equal(ProtoStream.Internal.ParseOutcomeKind.Message, outcome.Kind);
        Assert.Equal("ada", Assert.IsType<Hello>(message).Name);

        // The wire never names a type: an id outside the set is refused, and framing stays intact to resume after it.
        outcome = parser.Parse(codec, new ReadOnlySequence<byte>(Toy.Frame(9)), false, out _);
        Assert.Equal(ProtoStream.Internal.ParseOutcomeKind.Invalid, outcome.Kind);
        Assert.Equal(ViolationCode.UnexpectedMessage, outcome.Code);
        Assert.True(outcome.HasResumeAt);

        // Trailing bytes after a complete message are malformed, not ignored.
        outcome = parser.Parse(codec, new ReadOnlySequence<byte>(Toy.Frame(5, 0xFF)), false, out _);
        Assert.Equal(ViolationCode.Malformed, outcome.Code);
    }

    [Fact]
    public void TheHeartbeatIntervalMustBePositive()
    {
        var ex = Assert.Throws<ProtocolDefinitionException>(() =>
            Toy.Server().Heartbeat(TimeSpan.Zero, () => new Ping { Id = 0 }).Build());

        Assert.Contains(ex.Errors, e => e.Contains("heartbeat interval"));
    }

    [Fact]
    public void TypeLookupFindsTheNearestDescribedBaseType()
    {
        var map = new Definition.TypeMap([typeof(ToyMessage), typeof(Data)]);

        Assert.Equal(1, map.Find(typeof(Data)));
        Assert.Equal(0, map.Find(typeof(Ping)));
        Assert.Equal(-1, map.Find(typeof(string)));
        Assert.Equal(2, map.Types.Length);
        Assert.True(map.Types.SequenceEqual([typeof(ToyMessage), typeof(Data)]));
    }
}
