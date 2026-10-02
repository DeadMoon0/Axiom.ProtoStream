using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ProtoStream.Codecs;
using ProtoStream.Errors;
using ProtoStream.Testing;

namespace ProtoStream.Tests.Core;

public sealed class SessionTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class Pair : IAsyncDisposable
    {
        public Pair(ConnectionOptions? options = null)
        {
            Transport = InMemoryTransport.CreatePair();
            Server = Connection.FromPipe(Transport.Server, options ?? ConnectionOptions.Default);
            Client = Connection.FromPipe(Transport.Client, options ?? ConnectionOptions.Default);
        }

        public TransportPair Transport { get; }

        public Connection Server { get; }

        public Connection Client { get; }

        public async Task SendRawAsync(params byte[][] frames)
        {
            foreach (byte[] frame in frames)
                await Transport.Client.Output.WriteAsync(frame);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var all = new List<T>();
        await foreach (T item in source)
            all.Add(item);
        return all;
    }

    [Fact]
    public async Task TheUserSeesDelegatedMessagesWhileTheFrameworkAnswersPings()
    {
        await using var pair = new Pair();
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(Toy.Server().Build(), Ct);
        Session<ToyMessage, ToyMessage> client = await pair.Client.OpenAsync(Toy.Client(), Ct);

        await client.WriteAsync(new Hello { Name = "ada" }, Ct);
        await client.WriteAsync(new Ping { Id = 7 }, Ct);
        await client.WriteAsync(new Data { Payload = [1, 2, 3] }, Ct);
        await client.WriteAsync(new Bye(), Ct);

        List<ToyMessage> seen = await CollectAsync(server.Messages);

        Assert.Collection(seen,
            m => Assert.Equal("ada", Assert.IsType<Hello>(m).Name),
            m => Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<Data>(m).Payload));
        Assert.Equal(SessionStatus.Closed, server.Status);
        Assert.Equal("Closed", server.State);

        List<ToyMessage> answers = await CollectAsync(client.Messages);
        Assert.Collection(answers,
            m => Assert.Equal(7U, Assert.IsType<Pong>(m).Id),
            m => Assert.IsType<Bye>(m));
    }

    [Fact]
    public async Task WritingAMessageTheStateDoesNotAllowThrowsAndSendsNothing()
    {
        await using var pair = new Pair();
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(Toy.Server().Build(), Ct);

        await Assert.ThrowsAsync<ProtocolStateException>(() => server.WriteAsync(new Data { Payload = [1] }, Ct).AsTask());

        Assert.False(pair.Transport.Client.Input.TryRead(out _));
    }

    [Fact]
    public async Task TheStartStateGreetsThePeerOnOpen()
    {
        await using var pair = new Pair();
        ProtocolDefinition<ToyMessage, ToyMessage> greeting = Toy.Describe()
            .States(s => s.Start("Open").In("Open").OnEnter(() => new Hello { Name = "server" }).On<Data>().Delegate())
            .Build();

        await pair.Server.OpenAsync(greeting, Ct);
        Session<ToyMessage, ToyMessage> client = await pair.Client.OpenAsync(Toy.Client(), Ct);

        ProtocolReadResult<ToyMessage> first = await client.ReadAsync(Ct);
        Assert.Equal("server", Assert.IsType<Hello>(first.Message).Name);
    }

    [Fact]
    public async Task AConnectionOpensOneProtocol()
    {
        await using var pair = new Pair();
        await pair.Server.OpenAsync(Toy.Server().Build(), Ct);

        await Assert.ThrowsAsync<ProtocolStateException>(() => pair.Server.OpenAsync(Toy.Server().Build(), Ct).AsTask());
    }

    [Fact]
    public async Task APeerThatClosesBetweenMessagesEndsTheLoopGracefully()
    {
        await using var pair = new Pair();
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(Toy.Server().Build(), Ct);
        await pair.SendRawAsync(Toy.HelloFrame("ada"));
        await pair.Transport.Client.Output.CompleteAsync();

        List<ToyMessage> seen = await CollectAsync(server.Messages);

        Assert.Single(seen);
        Assert.Equal(SessionStatus.Closed, server.Status);
    }

    [Fact]
    public async Task APeerThatClosesInsideAMessageIsATruncation()
    {
        await using var pair = new Pair();
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(Toy.Server().Build(), Ct);
        await pair.SendRawAsync(Toy.HelloFrame("ada")[..3]);
        await pair.Transport.Client.Output.CompleteAsync();

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => CollectAsync(server.Messages));

        Assert.Equal(ViolationCode.Truncated, ex.Violation.Code);
        Assert.Equal(SessionStatus.Faulted, server.Status);
    }

    [Fact]
    public async Task AnUnexpectedMessageIsAViolationAndTheReplyIsSentFirst()
    {
        await using var pair = new Pair();
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(
            Toy.Server().ReplyToViolations(v => new Data { Payload = [(byte)v.Code] }).Build(), Ct);
        Session<ToyMessage, ToyMessage> client = await pair.Client.OpenAsync(Toy.Client(), Ct);
        await pair.SendRawAsync(Toy.PingFrame(1));

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => server.ReadAsync(Ct).AsTask());

        Assert.Equal(ViolationCode.UnexpectedMessage, ex.Violation.Code);
        Assert.Equal("AwaitHello", ex.Violation.State);
        ProtocolReadResult<ToyMessage> reply = await client.ReadAsync(Ct);
        Assert.Equal(new[] { (byte)ViolationCode.UnexpectedMessage }, Assert.IsType<Data>(reply.Message).Payload);
    }

    [Fact]
    public async Task WithSkipAnUnknownMessageIsDiscardedAndReadingContinues()
    {
        var observer = new RecordingObserver();
        await using var pair = new Pair(new ConnectionOptions { Observer = observer });
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(Toy.Server().OnViolation(ViolationAction.Skip).Build(), Ct);
        await pair.SendRawAsync(Toy.Frame(42, 1, 2), Toy.HelloFrame("ada"));

        ProtocolReadResult<ToyMessage> read = await server.ReadAsync(Ct);

        Assert.IsType<Hello>(read.Message);
        Assert.Equal(ViolationCode.UnexpectedMessage, Assert.Single(observer.Violations).Code);
    }

    // A message that straddles network reads must parse the same as one that arrives whole.
    [Fact]
    public async Task MessagesArrivingOneByteAtATimeParse()
    {
        byte[] wire = Toy.Concat(Toy.HelloFrame("ada"), Toy.Frame(2, 9, 8, 7));
        var transport = new ScriptedTransport(wire.Select(b => (ReadOnlyMemory<byte>)new[] { b }));
        await using Connection connection = Connection.FromPipe(transport);
        Session<ToyMessage, ToyMessage> server = await connection.OpenAsync(Toy.Server().Build(), Ct);

        List<ToyMessage> seen = await CollectAsync(server.Messages);

        Assert.Equal(2, seen.Count);
        Assert.Equal(new byte[] { 9, 8, 7 }, Assert.IsType<Data>(seen[1]).Payload);
    }

    [Fact]
    public async Task ASilentPeerHitsTheFirstMessageTimeout()
    {
        var time = new FakeTimeProvider();
        await using var pair = new Pair(new ConnectionOptions { TimeProvider = time });
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(Toy.Server().Build(), Ct);

        Task<ProtocolReadResult<ToyMessage>> read = server.ReadAsync(Ct).AsTask();
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(read.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => read);
        Assert.Equal(ViolationCode.Timeout, ex.Violation.Code);
    }

    [Fact]
    public async Task EveryMessageGetsItsOwnIdleWindow()
    {
        var time = new FakeTimeProvider();
        await using var pair = new Pair(new ConnectionOptions { TimeProvider = time });
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(
            Toy.Server().Limits(l => l.IdleTimeout(TimeSpan.FromSeconds(10))).Build(), Ct);
        await pair.SendRawAsync(Toy.HelloFrame("ada"));
        await server.ReadAsync(Ct);

        Task<ProtocolReadResult<ToyMessage>> read = server.ReadAsync(Ct).AsTask();
        time.Advance(TimeSpan.FromSeconds(8));
        await pair.SendRawAsync(Toy.PingFrame(1));
        await WaitUntil(() => pair.Transport.Client.Input.TryRead(out ReadResult r) && Release(pair.Transport.Client.Input, r));
        time.Advance(TimeSpan.FromSeconds(8));
        Assert.False(read.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(3));

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => read);
        Assert.Equal(ViolationCode.Timeout, ex.Violation.Code);
    }

    [Fact]
    public async Task APingFloodHitsTheAutoRespondBudget()
    {
        var time = new FakeTimeProvider();
        await using var pair = new Pair(new ConnectionOptions { TimeProvider = time });
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(Toy.Server().Limits(l => l.AutoRespondBudget(10)).Build(), Ct);
        _ = Task.Run(async () =>
        {
            ReadResult r;
            do
            {
                r = await pair.Transport.Client.Input.ReadAsync();
                pair.Transport.Client.Input.AdvanceTo(r.Buffer.End);
            }
            while (!r.IsCompleted);
        });
        await pair.SendRawAsync(Toy.HelloFrame("ada"));
        await server.ReadAsync(Ct);
        await pair.SendRawAsync(Enumerable.Range(0, 11).Select(i => Toy.PingFrame((uint)i)).ToArray());

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => server.ReadAsync(Ct).AsTask());
        Assert.Equal(ViolationCode.LimitExceeded, ex.Violation.Code);
    }

    [Fact]
    public async Task AHeartbeatIsSentWhenNothingWasWrittenForTheInterval()
    {
        var time = new FakeTimeProvider();
        await using var pair = new Pair(new ConnectionOptions { TimeProvider = time });
        await pair.Server.OpenAsync(Toy.Server().Heartbeat(TimeSpan.FromSeconds(10), () => new Ping { Id = 99 }).Build(), Ct);
        Session<ToyMessage, ToyMessage> client = await pair.Client.OpenAsync(Toy.Client(), Ct);

        Task<ProtocolReadResult<ToyMessage>> read = client.ReadAsync(Ct).AsTask();
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(read.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(99U, Assert.IsType<Ping>((await read).Message).Id);
    }

    [Fact]
    public async Task OnlyOneReadMayBeInProgress()
    {
        await using var pair = new Pair();
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(Toy.Server().Build(), Ct);
        using var cancel = new CancellationTokenSource();

        Task<ProtocolReadResult<ToyMessage>> first = server.ReadAsync(cancel.Token).AsTask();
        await Assert.ThrowsAsync<ProtocolStateException>(() => server.ReadAsync(Ct).AsTask());
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // A cancelled read leaves the session usable: nothing was consumed.
        await pair.SendRawAsync(Toy.HelloFrame("ada"));
        Assert.IsType<Hello>((await server.ReadAsync(Ct)).Message);
    }

    [Fact]
    public async Task ConcurrentWritesArriveWholeAndInSomeOrder()
    {
        await using var pair = new Pair();
        Session<ToyMessage, ToyMessage> client = await pair.Client.OpenAsync(Toy.Client(), Ct);
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(
            Toy.Describe().States(s => s.Start("Open").In("Open").On<Data>().Delegate()).Build(), Ct);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => client.WriteAsync(new Data { Payload = [(byte)i, (byte)i] }, Ct).AsTask())));
        var received = new List<byte>();
        for (int i = 0; i < 200; i++)
        {
            byte[] payload = Assert.IsType<Data>((await server.ReadAsync(Ct)).Message).Payload;
            Assert.Equal(payload[0], payload[1]);
            received.Add(payload[0]);
        }

        Assert.Equal(Enumerable.Range(0, 200).Select(i => (byte)i).OrderBy(b => b), received.OrderBy(b => b));
    }

    [Fact]
    public async Task CloseRunsTheCloseHandshakeAndWaitsForThePeer()
    {
        await using var pair = new Pair();
        ProtocolDefinition<ToyMessage, ToyMessage> definition = Toy.Describe()
            .States(s => s
                .Start("Open")
                .In("Open").On<Data>().Delegate().OnSend<Bye>().GoTo("Closing")
                .In("Closing").On<Bye>().Wait().GoTo("Closed").On<Data>().Wait()
                .Final("Closed"))
            .OnClose(() => new Bye())
            .Build();
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(definition, Ct);

        Task close = server.CloseAsync(Ct).AsTask();
        ReadResult sent = await pair.Transport.Client.Input.ReadAsync();
        Assert.Equal(Toy.Frame(5), sent.Buffer.ToArray());
        pair.Transport.Client.Input.AdvanceTo(sent.Buffer.End);
        Assert.False(close.IsCompleted);

        await pair.SendRawAsync(Toy.Frame(2, 1), Toy.Frame(5));
        await close;
        Assert.Equal(SessionStatus.Closed, server.Status);
    }

    [Fact]
    public async Task AThrowingResponderFaultsOnlyItsSession()
    {
        var observer = new RecordingObserver();
        await using var pair = new Pair(new ConnectionOptions { Observer = observer });
        ProtocolDefinition<ToyMessage, ToyMessage> definition = Toy.Describe()
            .States(s => s.Start("Open").In("Open").On<Ping>().Respond(_ => throw new InvalidOperationException("responder bug")))
            .Build();
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(definition, Ct);
        await pair.SendRawAsync(Toy.PingFrame(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.ReadAsync(Ct).AsTask());
        Assert.Equal(SessionStatus.Faulted, server.Status);
        Assert.IsType<InvalidOperationException>(Assert.Single(observer.Faults));
    }

    private sealed class ZeroConsumingCodec : ICodec<ToyMessage, ToyMessage>
    {
        public ParseResult TryParse(ref MessageParseContext context, out ToyMessage message)
        {
            message = new Bye();
            return context.Done(context.Input.Start);
        }

        public WriteResult Write(ToyMessage message, IBufferWriter<byte> output) => WriteResult.Done;
    }

    [Fact]
    public async Task ACodecBugIsReportedAsSuchAndFaultsTheSession()
    {
        await using var pair = new Pair();
        ProtocolDefinition<ToyMessage, ToyMessage> definition = Protocol.Describe<ToyMessage, ToyMessage>("buggy")
            .Codec(() => new ZeroConsumingCodec())
            .States(s => s.Start("Open").In("Open").On<Bye>().Delegate())
            .Build();
        Session<ToyMessage, ToyMessage> server = await pair.Server.OpenAsync(definition, Ct);
        await pair.SendRawAsync([1]);

        await Assert.ThrowsAsync<CodecContractException>(() => server.ReadAsync(Ct).AsTask());
        Assert.Equal(SessionStatus.Faulted, server.Status);
    }

    // ---- pooled messages and allocations -------------------------------------------------------

    public sealed class Chunk
    {
        private MessageStamp _stamp;
        private ReadOnlyMemory<byte> _memory;

        public ReadOnlyMemory<byte> Memory
        {
            get
            {
                _stamp.ThrowIfStale();
                return _memory;
            }
        }

        public void Set(ReadOnlyMemory<byte> memory, MessageStamp stamp)
        {
            _memory = memory;
            _stamp = stamp;
        }
    }

    // Two instances in rotation: a message kept past the next read is not refilled yet and reports itself stale.
    private sealed class ChunkCodec : ProtoStream.Framing.IFrameCodec<Chunk, Chunk>
    {
        private readonly Chunk[] _pooled = [new(), new()];
        private int _next;

        public ProtoStream.Framing.DecodeResult Decode(in ProtoStream.Framing.Frame frame, out Chunk message)
        {
            message = _pooled[_next ^= 1];
            message.Set(frame.Memory, frame.Stamp);
            return ProtoStream.Framing.DecodeResult.Ok;
        }

        public void Encode(Chunk message, IBufferWriter<byte> output) => output.Write(message.Memory.Span);
    }

    private static ProtocolDefinition<Chunk, Chunk> Chunks() => Protocol.Describe<Chunk, Chunk>("chunks")
        .Framing(Toy.Framer)
        .FrameCodec(() => new ChunkCodec())
        .States(s => s.Start("Open").In("Open").On<Chunk>().Delegate().OnSend<Chunk>())
        .Build();

    private static (IDuplexPipe Pipe, PipeWriter Peer) PrefilledPipe()
    {
        var unbounded = new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false);
        var input = new Pipe(unbounded);
        var output = new Pipe(unbounded);
        return (new Duplex(input.Reader, output.Writer), input.Writer);
    }

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }

    // The steady state of the read path allocates nothing per message: no message copies, no state-machine
    // boxes, no timer objects. Reads complete synchronously here because every frame is already buffered.
    // The in-memory pipe allocates a little on its own, so the session is measured against a raw pipe loop
    // over the same bytes; any per-message object (at least 24 bytes) would show up as kilobytes.
    [ReleaseFact]
    public async Task ReadingPooledMessagesAllocatesNothingBeyondThePipeItself()
    {
        const int Messages = 10_000;
        byte[] frame = [0, 4, 1, 2, 3, 4];

        (IDuplexPipe raw, PipeWriter rawPeer) = PrefilledPipe();
        (IDuplexPipe pipe, PipeWriter peer) = PrefilledPipe();
        for (int i = 0; i < Messages; i++)
        {
            rawPeer.Write(frame);
            peer.Write(frame);
        }

        await rawPeer.FlushAsync();
        await peer.FlushAsync();

        await using Connection connection = Connection.FromPipe(pipe);
        Session<Chunk, Chunk> session = await connection.OpenAsync(Chunks(), Ct);
        for (int i = 0; i < 100; i++)
        {
            await session.ReadAsync(Ct);
            ReadResult warm = await raw.Input.ReadAsync();
            raw.Input.AdvanceTo(warm.Buffer.GetPosition(frame.Length));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 100; i < Messages; i++)
        {
            ReadResult read = await raw.Input.ReadAsync();
            raw.Input.AdvanceTo(read.Buffer.GetPosition(frame.Length));
        }

        long pipeOnly = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 100; i < Messages; i++)
        {
            ProtocolReadResult<Chunk> read = await session.ReadAsync(Ct);
            if (read.Message!.Memory.Length != 4)
                Assert.Fail("Wrong frame.");
        }

        long withSession = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(withSession - pipeOnly < 1024, $"The session allocated {withSession} bytes where the bare pipe allocated {pipeOnly}.");
    }

    [Fact]
    public async Task APooledMessageUsedAfterTheNextReadThrowsAndRetainedDataSurvives()
    {
        (IDuplexPipe pipe, PipeWriter peer) = PrefilledPipe();
        peer.Write(new byte[] { 0, 1, 10, 0, 1, 20 });
        await peer.FlushAsync();
        await using Connection connection = Connection.FromPipe(pipe);
        Session<Chunk, Chunk> session = await connection.OpenAsync(Chunks(), Ct);

        Chunk first = (await session.ReadAsync(Ct)).Message!;
        byte[] kept = first.Memory.ToArray();
        await session.ReadAsync(Ct);

        Assert.Throws<StaleMessageException>(() => first.Memory);
        Assert.Equal(new byte[] { 10 }, kept);
    }

    // ---- harness -------------------------------------------------------------------------------

    [Fact]
    public void ToyMessagesRoundTripAtEverySplit()
    {
        ToyMessage[] messages = [new Hello { Name = "ada" }, new Data { Payload = [1, 2, 3] }, new Ping { Id = 5 }, new Bye()];

        CodecHarness.VerifyRoundTrip(Toy.Client(), messages, (sent, received) => sent switch
        {
            Hello h => received is Hello r && r.Name == h.Name,
            Data d => received is Data r && r.Payload.SequenceEqual(d.Payload),
            Ping p => received is Ping r && r.Id == p.Id,
            _ => received is Bye,
        });
    }

    [Fact]
    public void TheToyCodecSurvivesMutationFuzzing()
    {
        byte[][] samples = [Toy.HelloFrame("ada"), Toy.Concat(Toy.PingFrame(1), Toy.Frame(2, 1, 2, 3)), Toy.Frame(5)];

        FuzzReport report = CodecHarness.Fuzz(Toy.Server().Build(), samples, seed: 1234, iterations: 5_000);

        Assert.Equal(5_000, report.Iterations);
        Assert.True(report.Invalid > 0 && report.Messages > 0);
    }

    [Fact]
    public void APooledCodecLeaksNothingBetweenMessages()
    {
        MessageReuseContract.Verify(Chunks(), [0, 3, 1, 2, 3], [0, 1, 9], chunk => Convert.ToHexString(chunk.Memory.Span));
    }

    [Fact]
    public void TheToyServerHasNoWarnings() => DefinitionAssert.NoWarnings(Toy.Server().Build());

    // ---- helpers -------------------------------------------------------------------------------

    private static bool Release(PipeReader reader, ReadResult result)
    {
        reader.AdvanceTo(result.Buffer.End);
        return !result.Buffer.IsEmpty;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
    }

    private sealed class RecordingObserver : IProtocolObserver
    {
        public List<Violation> Violations { get; } = [];

        public List<Exception> Faults { get; } = [];

        public void OnViolation(ISession session, Violation violation) => Violations.Add(violation);

        public void OnFault(ISession session, Exception exception) => Faults.Add(exception);
    }
}
