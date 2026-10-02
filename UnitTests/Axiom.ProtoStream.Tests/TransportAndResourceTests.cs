using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Testing;

using Axiom.ProtoStream.Tests.Shared;

namespace Axiom.ProtoStream.Tests;

/// <summary>What the connection does with the transport, memory and threads the host hands it.</summary>
public sealed class TransportAndResourceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // A pipe-backed stream pair stands in for stdio: input and output are two unrelated streams.
    private sealed class StreamPair
    {
        public Pipe Inbound { get; } = new();

        public Pipe Outbound { get; } = new();

        public Connection Open(ConnectionOptions options) =>
            Connection.FromStreams(Inbound.Reader.AsStream(), Outbound.Writer.AsStream(), options);

        public async Task<byte[]> ReceiveAsync(int length)
        {
            while (true)
            {
                ReadResult read = await Outbound.Reader.ReadAsync(Ct);
                if (read.Buffer.Length >= length || read.IsCompleted)
                {
                    byte[] bytes = read.Buffer.Slice(0, Math.Min(length, read.Buffer.Length)).ToArray();
                    Outbound.Reader.AdvanceTo(read.Buffer.GetPosition(bytes.Length));
                    return bytes;
                }

                Outbound.Reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);
            }
        }
    }

    private sealed class RecordingPool : MemoryPool<byte>
    {
        private int _rented;

        public int Rented => Volatile.Read(ref _rented);

        public override int MaxBufferSize => Shared.MaxBufferSize;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            Interlocked.Increment(ref _rented);
            return Shared.Rent(minBufferSize);
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    [Fact]
    public async Task FromStreamsReadsFromOneStreamAndWritesToTheOther()
    {
        var streams = new StreamPair();
        await using (Connection connection = streams.Open(ConnectionOptions.Default))
        {
            Session<ToyMessage, ToyMessage> server = await connection.OpenAsync(Toy.Server().Build(), Ct);
            await streams.Inbound.Writer.WriteAsync(Toy.HelloFrame("ada"));

            Assert.Equal("ada", Assert.IsType<Hello>((await server.ReadAsync(Ct)).Message).Name);

            await server.WriteAsync(new Data { Payload = [1, 2] }, Ct);
            Assert.Equal(Toy.Frame(2, 1, 2), await streams.ReceiveAsync(5));

            // End of input lets the dispose finish its close without waiting for the close timeout.
            await streams.Inbound.Writer.CompleteAsync();
        }
    }

    // A host that shares one pool across connections must be able to rely on it actually being used.
    [Fact]
    public async Task TheInjectedMemoryPoolIsTheOneTheBuffersComeFrom()
    {
        var pool = new RecordingPool();
        var streams = new StreamPair();
        await using (Connection connection = streams.Open(new ConnectionOptions { Pool = pool }))
        {
            Session<ToyMessage, ToyMessage> server = await connection.OpenAsync(Toy.Server().Build(), Ct);
            await streams.Inbound.Writer.WriteAsync(Toy.HelloFrame("ada"));
            await server.ReadAsync(Ct);
            int afterRead = pool.Rented;

            await server.WriteAsync(new Data { Payload = [1] }, Ct);

            Assert.True(afterRead > 0, "the read side rented nothing from the injected pool");
            Assert.True(pool.Rented > afterRead, "the write side rented nothing from the injected pool");
            await streams.Inbound.Writer.CompleteAsync();
        }
    }

    // The framework never schedules work: a message that is already buffered comes back synchronously, on the
    // caller's thread, so a host with one task per connection keeps control over where the work runs.
    [Fact]
    public async Task ABufferedMessageIsReturnedSynchronously()
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<ToyMessage, ToyMessage> server = await connection.OpenAsync(Toy.Server().Build(), Ct);
        await transport.Client.Output.WriteAsync(Toy.Concat(Toy.HelloFrame("ada"), Toy.Frame(2, 7), Toy.Frame(2, 8)));
        await server.ReadAsync(Ct);

        ValueTask<ProtocolReadResult<ToyMessage>> second = server.ReadAsync(Ct);
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(new byte[] { 7 }, Assert.IsType<Data>((await second).Message).Payload);

        ValueTask<ProtocolReadResult<ToyMessage>> third = server.ReadAsync(Ct);
        Assert.True(third.IsCompletedSuccessfully);
        Assert.Equal(new byte[] { 8 }, Assert.IsType<Data>((await third).Message).Payload);

        await transport.Client.Output.CompleteAsync();
    }

    // A client that pipelines requests and never reads the answers must stall the server, not grow its memory:
    // the server's writes wait on the full outbound pipe, so it stops reading, so the inbound pipe fills too.
    [Fact]
    public async Task APeerThatPipelinesWithoutReadingStallsTheServerInsteadOfGrowingItsBuffers()
    {
        const int PauseThreshold = 64 * 1024;
        const int Frames = 1000;
        TransportPair transport = InMemoryTransport.CreatePair(new PipeOptions(
            pauseWriterThreshold: PauseThreshold, resumeWriterThreshold: PauseThreshold / 2, useSynchronizationContext: false));
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<ToyMessage, ToyMessage> server = await connection.OpenAsync(Toy.Server().Build(), Ct);
        Task echo = Task.Run(async () =>
        {
            await foreach (ToyMessage message in server.Messages)
                if (message is Data data)
                    await server.WriteAsync(data, Ct);
        });

        byte[] frame = Toy.Frame(2, new byte[1024]);
        await transport.Client.Output.WriteAsync(Toy.HelloFrame("ada"));
        long sentBeforeStall = -1;
        Task<FlushResult>? stalled = null;
        int next = 0;
        for (; next < Frames && sentBeforeStall < 0; next++)
        {
            Task<FlushResult> flush = transport.Client.Output.WriteAsync(frame).AsTask();
            if (await Task.WhenAny(flush, Task.Delay(TimeSpan.FromMilliseconds(500))) != flush)
            {
                sentBeforeStall = (next + 1L) * frame.Length;
                stalled = flush;
            }
        }

        // Both pipes full plus the frames in flight; far below the megabyte the client tried to send.
        Assert.InRange(sentBeforeStall, 1, 4L * PauseThreshold);
        Assert.False(echo.IsCompleted);

        long expected = (long)Frames * frame.Length + Toy.Frame(5).Length;
        Task<long> receive = Task.Run(async () =>
        {
            long received = 0;
            while (received < expected)
            {
                ReadResult read = await transport.Client.Input.ReadAsync(Ct);
                received += read.Buffer.Length;
                transport.Client.Input.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted)
                    break;
            }

            return received;
        });

        await stalled!;
        for (; next < Frames; next++)
            await transport.Client.Output.WriteAsync(frame);
        await transport.Client.Output.WriteAsync(Toy.Frame(5));

        Assert.Equal(expected, await receive.WaitAsync(TimeSpan.FromSeconds(30)));
        await echo.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // Coalescing holds writes back only for the reading loop to flush; a write from another task while the
    // reader waits for input would otherwise sit in the buffer until the peer sent something.
    [Fact]
    public async Task WithCoalescingAWriteWhileTheReaderWaitsIsStillFlushedAtOnce()
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<ToyMessage, ToyMessage> server = await connection.OpenAsync(Toy.Server().Flushing(FlushPolicy.WhileInputIsBuffered).Build(), Ct);
        await transport.Client.Output.WriteAsync(Toy.HelloFrame("ada"));
        await server.ReadAsync(Ct);

        Task<ProtocolReadResult<ToyMessage>> waiting = server.ReadAsync(Ct).AsTask();
        await server.WriteAsync(new Data { Payload = [5] }, Ct);

        ReadResult read = await transport.Client.Input.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Toy.Frame(2, 5), read.Buffer.ToArray());
        transport.Client.Input.AdvanceTo(read.Buffer.End);
        Assert.False(waiting.IsCompleted);
        await transport.Client.Output.CompleteAsync();
        await waiting;
    }

    // Disposing while another task reads must not hand the session's buffers to the shared pools under the
    // running read: the read ends first, then the buffers go back.
    [Fact]
    public async Task DisposingWhileAReadWaitsEndsTheReadAndThenReleases()
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        Connection connection = Connection.FromPipe(transport.Server);
        Session<ToyMessage, ToyMessage> server = await connection.OpenAsync(Toy.Server().Build(), Ct);
        await transport.Client.Output.WriteAsync(Toy.HelloFrame("ada"));
        Hello hello = Assert.IsType<Hello>((await server.ReadAsync(Ct)).Message);
        Task<ProtocolReadResult<ToyMessage>> waiting = server.ReadAsync(Ct).AsTask();

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(10))).IsCompleted);
        Assert.Equal(SessionStatus.Closed, server.Status);
        Assert.True((await server.ReadAsync(Ct)).IsCompleted);
        Assert.Equal("ada", hello.Name); // owned message, not pooled: unaffected
    }

    // A write stuck on a peer that stopped reading must fail when the connection is disposed, never report
    // success for bytes that did not leave, and never see its buffers released underneath it.
    [Fact]
    public async Task DisposingWhileAWriteIsStuckFailsTheWrite()
    {
        TransportPair transport = InMemoryTransport.CreatePair(new PipeOptions(pauseWriterThreshold: 1024, resumeWriterThreshold: 512, useSynchronizationContext: false));
        Connection connection = Connection.FromPipe(transport.Server);
        Session<ToyMessage, ToyMessage> server = await connection.OpenAsync(Toy.Server().Build(), Ct);
        await transport.Client.Output.WriteAsync(Toy.HelloFrame("ada"));
        await server.ReadAsync(Ct);
        Task stuck = server.WriteAsync(new Data { Payload = new byte[4000] }, Ct).AsTask();
        Assert.False(stuck.IsCompleted);

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<Errors.TransportException>(() => stuck.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<Errors.ProtocolStateException>(() => server.WriteAsync(new Data { Payload = [1] }, Ct).AsTask());
    }

    private sealed class Note : ToyMessage
    {
        public required string Text { get; init; }
    }

    // Stands in for MessagePack, protobuf or source-generated JSON: the body is the note's UTF-8 text.
    private sealed class NoteSerializer : IMessageSerializer
    {
        public bool TryDeserialize(Type type, ReadOnlyMemory<byte> body, out object? message)
        {
            message = type == typeof(Note) && body.Length > 0 ? new Note { Text = Encoding.UTF8.GetString(body.Span) } : null;
            return message is not null;
        }

        public void Serialize(Type type, object message, IBufferWriter<byte> output) =>
            Encoding.UTF8.GetBytes(((Note)message).Text, output);
    }

    [Fact]
    public void AMessageSetHandsMappedBodiesToTheSerializerAndKeepsTheIdItself()
    {
        ProtocolDefinition<ToyMessage, ToyMessage> definition = Protocol.Describe<ToyMessage, ToyMessage>("notes")
            .Framing(Toy.Framer)
            .Messages(m => m.Map<Note>(9).Serializer(new NoteSerializer()))
            .States(s => s.Start("Open").In("Open").On<Note>().Delegate().OnSend<Note>())
            .Build();
        using var runner = new CodecRunner<ToyMessage, ToyMessage>(definition);
        var output = new ArrayBufferWriter<byte>();

        runner.Encode(new Note { Text = "hi" }, output);

        Assert.Equal(Toy.Frame(9, (byte)'h', (byte)'i'), output.WrittenSpan.ToArray());
        Assert.Equal("hi", Assert.IsType<Note>(runner.Decode(new ReadOnlySequence<byte>(output.WrittenMemory), true).Message).Text);
        Assert.Equal(DecodeStatus.Invalid, runner.Decode(new ReadOnlySequence<byte>(Toy.Frame(9)), true).Status);
    }

    [Fact]
    public void FramedCodecsCanResumeAfterAnInvalidMessageSoSkipIsNotFlagged()
    {
        ProtocolDefinition<ToyMessage, ToyMessage> definition = Toy.Server().OnViolation(ViolationAction.Skip).Build();

        Assert.DoesNotContain(definition.Warnings, w => w.Contains("OnViolation(Skip)"));
    }
}
