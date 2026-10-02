using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProtoStream.Codecs;
using ProtoStream.Errors;
using ProtoStream.Framing;
using ProtoStream.Testing;

namespace ProtoStream.Tests.Core;

public sealed class PayloadAndSwitchTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ---- a line protocol whose BLOB message carries a payload -----------------------------------

    public abstract class BlobMessage
    {
    }

    public sealed class Blob : BlobMessage
    {
        public required long Length { get; init; }

        public required PipeReader Body { get; init; }
    }

    public sealed class Note : BlobMessage
    {
        public required string Text { get; init; }
    }

    public sealed class OutgoingBlob : BlobMessage
    {
        public required Stream Source { get; init; }

        public required long Length { get; init; }
    }

    private sealed class BlobCodec : ICodec<BlobMessage, BlobMessage>
    {
        private readonly FixedLengthPayloadDecoder _body = new();

        public ParseResult TryParse(ref MessageParseContext context, out BlobMessage message)
        {
            message = null!;
            var reader = new SequenceReader<byte>(context.Input);
            if (!reader.TryReadTo(out ReadOnlySequence<byte> _, (byte)'\n'))
                return context.Input.Length > 64 ? context.Invalid(ViolationCode.LimitExceeded, "Line too long.") : context.NeedMore();

            DetachedHead head = context.Detach(reader.Position);
            string line = Encoding.ASCII.GetString(head.Memory.Span).TrimEnd('\n');
            if (line.StartsWith("NOTE ", StringComparison.Ordinal))
            {
                message = new Note { Text = line[5..] };
                return context.Done(head);
            }

            if (line.StartsWith("BLOB ", StringComparison.Ordinal) && long.TryParse(line[5..], out long length) && length >= 0)
            {
                _body.Reset(length);
                ParseResult result = context.DoneWithPayload(head, _body, out PipeReader body);
                message = new Blob { Length = length, Body = body };
                return result;
            }

            return context.Invalid(ViolationCode.Malformed, "Unknown line.");
        }

        public WriteResult Write(BlobMessage message, IBufferWriter<byte> output)
        {
            switch (message)
            {
                case Note note:
                    output.Write(Encoding.ASCII.GetBytes($"NOTE {note.Text}\n"));
                    return WriteResult.Done;
                case OutgoingBlob blob:
                    output.Write(Encoding.ASCII.GetBytes($"BLOB {blob.Length}\n"));
                    return WriteResult.WithPayload(blob.Source, blob.Length, IdentityPayloadEncoder.Instance);
                default:
                    throw new NotSupportedException();
            }
        }
    }

    private static ProtocolDefinition<BlobMessage, BlobMessage> Blobs(long drainLimit = 1024) => Protocol.Describe<BlobMessage, BlobMessage>("blobs")
        .Codec(() => new BlobCodec())
        .States(s => s.Start("Open").In("Open").On<Blob>().Delegate().On<Note>().Delegate().OnSend<Note>().OnSend<OutgoingBlob>())
        .Limits(l => l.MaxPayloadDrain(drainLimit))
        .Build();

    private static async Task<byte[]> ReadAllAsync(PipeReader body)
    {
        var all = new List<byte>();
        while (true)
        {
            ReadResult read = await body.ReadAsync();
            all.AddRange(read.Buffer.ToArray());
            body.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                return [.. all];
        }
    }

    private static async Task<(Session<BlobMessage, BlobMessage> Server, TransportPair Transport, Connection Connection)> OpenBlobsAsync(ProtocolDefinition<BlobMessage, BlobMessage> definition, string input)
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        Connection connection = Connection.FromPipe(transport.Server);
        Session<BlobMessage, BlobMessage> server = await connection.OpenAsync(definition, Ct);
        await transport.Client.Output.WriteAsync(Encoding.ASCII.GetBytes(input));
        return (server, transport, connection);
    }

    [Fact]
    public async Task APayloadIsReadStraightFromTheConnectionAndTheNextMessageFollows()
    {
        var (server, _, connection) = await OpenBlobsAsync(Blobs(), "BLOB 5\nhelloNOTE after\n");
        await using Connection _ = connection;

        var blob = Assert.IsType<Blob>((await server.ReadAsync(Ct)).Message);
        Assert.Equal("hello", Encoding.ASCII.GetString(await ReadAllAsync(blob.Body)));
        Assert.Equal("after", Assert.IsType<Note>((await server.ReadAsync(Ct)).Message).Text);
    }

    [Fact]
    public async Task AnUnreadPayloadIsDrainedBeforeTheNextMessage()
    {
        var (server, _, connection) = await OpenBlobsAsync(Blobs(), "BLOB 5\nhelloNOTE after\n");
        await using Connection _ = connection;

        var blob = Assert.IsType<Blob>((await server.ReadAsync(Ct)).Message);
        Assert.Equal("after", Assert.IsType<Note>((await server.ReadAsync(Ct)).Message).Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => blob.Body.ReadAsync().AsTask());
    }

    [Fact]
    public async Task AnUnreadPayloadOverTheDrainLimitIsAViolation()
    {
        var (server, _, connection) = await OpenBlobsAsync(Blobs(drainLimit: 4), "BLOB 5\nhelloNOTE after\n");
        await using Connection _ = connection;

        await server.ReadAsync(Ct);
        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => server.ReadAsync(Ct).AsTask());
        Assert.Equal(ViolationCode.LimitExceeded, ex.Violation.Code);
    }

    [Fact]
    public async Task APayloadCutShortByThePeerIsATruncation()
    {
        var (server, transport, connection) = await OpenBlobsAsync(Blobs(), "BLOB 5\nhel");
        await using Connection _ = connection;
        await transport.Client.Output.CompleteAsync();

        var blob = Assert.IsType<Blob>((await server.ReadAsync(Ct)).Message);
        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ReadAllAsync(blob.Body));
        Assert.Equal(ViolationCode.Truncated, ex.Violation.Code);
        Assert.Equal(SessionStatus.Faulted, server.Status);
    }

    [Fact]
    public async Task PayloadsArrivingOneByteAtATimeAreComplete()
    {
        byte[] wire = Encoding.ASCII.GetBytes("BLOB 5\nhelloNOTE after\n");
        var transport = new ScriptedTransport(wire.Select(b => (ReadOnlyMemory<byte>)new[] { b }));
        await using Connection connection = Connection.FromPipe(transport);
        Session<BlobMessage, BlobMessage> server = await connection.OpenAsync(Blobs(), Ct);

        var blob = Assert.IsType<Blob>((await server.ReadAsync(Ct)).Message);
        Assert.Equal("hello", Encoding.ASCII.GetString(await ReadAllAsync(blob.Body)));
        Assert.Equal("after", Assert.IsType<Note>((await server.ReadAsync(Ct)).Message).Text);
    }

    [Fact]
    public async Task AWrittenPayloadIsStreamedAfterItsHead()
    {
        var (server, transport, connection) = await OpenBlobsAsync(Blobs(), "");
        await using Connection _ = connection;

        await server.WriteAsync(new OutgoingBlob { Source = new MemoryStream(Encoding.ASCII.GetBytes("world")), Length = 5 }, Ct);

        ReadResult sent = await transport.Client.Input.ReadAsync();
        Assert.Equal("BLOB 5\nworld", Encoding.ASCII.GetString(sent.Buffer.ToArray()));
    }

    // Half a payload on the wire means the peer can no longer find the next message.
    [Fact]
    public async Task APayloadShorterThanAnnouncedFaultsTheSession()
    {
        var (server, _, connection) = await OpenBlobsAsync(Blobs(), "");
        await using Connection _ = connection;

        await Assert.ThrowsAsync<ProtocolStateException>(() =>
            server.WriteAsync(new OutgoingBlob { Source = new MemoryStream(new byte[3]), Length = 5 }, Ct).AsTask());
        Assert.Equal(SessionStatus.Faulted, server.Status);
    }

    // ---- switching -----------------------------------------------------------------------------

    [Fact]
    public async Task BytesAlreadyBufferedAfterTheSwitchPointGoToTheNewProtocol()
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<ToyMessage, ToyMessage> toy = await connection.OpenAsync(Toy.Server().Build(), Ct);

        // Everything arrives in one write, so the bytes after Hello sit in the same buffer when the switch happens.
        byte[] afterSwitch = Toy.Concat(Toy.Frame(2, 7), "RAW!"u8.ToArray());
        await transport.Client.Output.WriteAsync(Toy.Concat(Toy.HelloFrame("ada"), afterSwitch));

        Session<RawData, RawData>? raw = null;
        await foreach (ToyMessage message in toy.Messages)
        {
            Assert.IsType<Hello>(message);
            raw = await toy.SwitchAsync(new Data { Payload = [42] }, Raw.Definition, Ct);
        }

        Assert.NotNull(raw);
        Assert.Equal(SessionStatus.Switched, toy.Status);
        await Assert.ThrowsAsync<ProtocolSwitchedException>(() => toy.ReadAsync(Ct).AsTask());
        await Assert.ThrowsAsync<ProtocolSwitchedException>(() => toy.WriteAsync(new Data { Payload = [] }, Ct).AsTask());

        // This read completes from the buffer; a session that examined past the switch point would hang here.
        var received = new List<byte>();
        while (received.Count < afterSwitch.Length)
            received.AddRange((await raw.ReadAsync(Ct)).Message!.Data.ToArray());
        Assert.Equal(afterSwitch, received);

        ReadResult sent = await transport.Client.Input.ReadAsync();
        Assert.Equal(Toy.Frame(2, 42), sent.Buffer.ToArray());
    }

    [Fact]
    public async Task SwitchingIsRefusedInAStateNotDescribedAsSwitchable()
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<ToyMessage, ToyMessage> toy = await connection.OpenAsync(Toy.Server().Build(), Ct);

        await Assert.ThrowsAsync<ProtocolStateException>(() => toy.SwitchAsync(Raw.Definition, Ct).AsTask());
        Assert.Equal(SessionStatus.Open, toy.Status);
    }

    public sealed class ProxyHeader
    {
        public required string Source { get; init; }
    }

    private sealed class ProxyCodec : IFrameCodec<ProxyHeader, ProxyHeader>
    {
        public DecodeResult Decode(in Frame frame, out ProxyHeader message)
        {
            string[] parts = Encoding.ASCII.GetString(frame.Memory.Span).Split(' ');
            message = null!;
            if (parts.Length != 6 || parts[0] != "PROXY")
                return DecodeResult.Invalid(ViolationCode.Malformed, "Not a PROXY v1 header.");
            message = new ProxyHeader { Source = parts[2] };
            return DecodeResult.Ok;
        }

        public void Encode(ProxyHeader message, IBufferWriter<byte> output) => throw new NotSupportedException();
    }

    // A load balancer's PROXY header is a tiny protocol of its own, followed by the real one.
    [Fact]
    public async Task APrefixProtocolHandsOverToTheRealOneWithoutAFinalMessage()
    {
        ProtocolDefinition<ProxyHeader, ProxyHeader> proxy = Protocol.Describe<ProxyHeader, ProxyHeader>("proxy-v1")
            .Framing(Framers.Delimited("\r\n"u8, maxFrameSize: 107))
            .FrameCodec(() => new ProxyCodec())
            .States(s => s.Start("Await").In("Await").On<ProxyHeader>().Delegate().Switchable())
            .Build();
        TransportPair transport = InMemoryTransport.CreatePair();
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<ProxyHeader, ProxyHeader> header = await connection.OpenAsync(proxy, Ct);
        await transport.Client.Output.WriteAsync(Toy.Concat("PROXY TCP4 1.2.3.4 5.6.7.8 1 2\r\n"u8.ToArray(), Toy.HelloFrame("ada")));

        ProxyHeader received = (await header.ReadAsync(Ct)).Message!;
        Session<ToyMessage, ToyMessage> toy = await header.SwitchAsync(Toy.Server().Build(), Ct);

        Assert.Equal("1.2.3.4", received.Source);
        Assert.Equal("ada", Assert.IsType<Hello>((await toy.ReadAsync(Ct)).Message).Name);
        Assert.Same(toy, connection.Current);
    }

    // ---- per-state readers: an SMTP-like DATA section ------------------------------------------

    public abstract class MailIn
    {
    }

    public sealed class Command : MailIn
    {
        public required string Verb { get; init; }
    }

    public sealed class MailBody : MailIn
    {
        public required string Text { get; init; }
    }

    public sealed class Reply
    {
        public required int Code { get; init; }
    }

    private sealed class CommandCodec : IFrameCodec<MailIn, Reply>
    {
        public DecodeResult Decode(in Frame frame, out MailIn message)
        {
            message = new Command { Verb = Encoding.ASCII.GetString(frame.Memory.Span).ToUpperInvariant() };
            return DecodeResult.Ok;
        }

        public void Encode(Reply message, IBufferWriter<byte> output) => output.Write(Encoding.ASCII.GetBytes($"{message.Code}"));
    }

    private sealed class DotTerminatedReader : IMessageReader<MailIn>
    {
        public ParseResult TryParse(ref MessageParseContext context, out MailIn message)
        {
            message = null!;
            byte[] input = context.Input.ToArray();
            int end = input.AsSpan().StartsWith(".\r\n"u8) ? -2 : input.AsSpan().IndexOf("\r\n.\r\n"u8);
            if (end == -1)
                return context.Input.Length > 4096 ? context.Invalid(ViolationCode.LimitExceeded, "Body too long.") : context.NeedMore();

            string body = end < 0 ? "" : Encoding.ASCII.GetString(input, 0, end + 2);
            // Dot-unstuffing: a line that starts with two dots carried one dot.
            message = new MailBody { Text = string.Join("\r\n", body.Split("\r\n").Select(line => line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line)) };
            return context.Done(context.Input.GetPosition(end + 5));
        }
    }

    [Fact]
    public async Task AStateCanReadWithItsOwnReaderWhilePipelinedCommandsWait()
    {
        ProtocolDefinition<MailIn, Reply> mail = Protocol.Describe<MailIn, Reply>("mail")
            .Framing(Framers.Delimited("\r\n"u8, maxFrameSize: 512))
            .FrameCodec(() => new CommandCodec())
            .States(s => s
                .Start("Command")
                .In("Command").On<Command>().Delegate().OnSend<Reply>().GoToIf(r => r.Code == 354, "Data", "Command")
                .In("Data").Reads(() => new DotTerminatedReader()).On<MailBody>().Delegate().GoTo("Command"))
            .Limits(l => l.MaxBufferedBytes(8192))
            .Build();
        TransportPair transport = InMemoryTransport.CreatePair();
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<MailIn, Reply> server = await connection.OpenAsync(mail, Ct);
        await transport.Client.Output.WriteAsync("DATA\r\nline1\r\n..leading dot\r\n.\r\nQUIT\r\n"u8.ToArray());

        Assert.Equal("DATA", Assert.IsType<Command>((await server.ReadAsync(Ct)).Message).Verb);
        await server.WriteAsync(new Reply { Code = 354 }, Ct);
        Assert.Equal("line1\r\n.leading dot\r\n", Assert.IsType<MailBody>((await server.ReadAsync(Ct)).Message).Text);
        Assert.Equal("QUIT", Assert.IsType<Command>((await server.ReadAsync(Ct)).Message).Verb);
    }

    // ---- transport switch ----------------------------------------------------------------------

    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await output.WriteAsync(buffer, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        public override void Flush() => output.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // Stands in for TLS: the bytes on the wire change, the protocol above does not.
    private sealed class XorStream(Stream inner, byte key) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            Span<byte> span = buffer.Span[..read];
            for (int i = 0; i < span.Length; i++)
                span[i] ^= key;
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] copy = buffer.ToArray();
            for (int i = 0; i < copy.Length; i++)
                copy[i] ^= key;
            return inner.WriteAsync(copy, cancellationToken);
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static byte[] Xor(byte[] bytes) => bytes.Select(b => (byte)(b ^ 0x5A)).ToArray();

    [Fact]
    public async Task ATransportSwitchWrapsTheStreamAfterTheFinalMessage()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        await using Connection connection = Connection.FromStream(new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream()));
        Session<ToyMessage, ToyMessage> plain = await connection.OpenAsync(Toy.Server().Build(), Ct);
        await toServer.Writer.WriteAsync(Toy.HelloFrame("plain"));
        await plain.ReadAsync(Ct);

        Session<ToyMessage, ToyMessage> wrapped = await plain.SwitchAsync(
            new Data { Payload = [1] }, Toy.Server().Build(), (stream, _) => ValueTask.FromResult<Stream>(new XorStream(stream, 0x5A)), Ct);

        ReadResult acknowledged = await toClient.Reader.ReadAsync();
        Assert.Equal(Toy.Frame(2, 1), acknowledged.Buffer.ToArray());
        await toServer.Writer.WriteAsync(Xor(Toy.HelloFrame("secret")));
        Assert.Equal("secret", Assert.IsType<Hello>((await wrapped.ReadAsync(Ct)).Message).Name);
    }

    // Bytes sent before the peer could know the transport changed are the classic STARTTLS injection.
    [Fact]
    public async Task BytesSentBeforeATransportSwitchAreAViolation()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        await using Connection connection = Connection.FromStream(new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream()));
        Session<ToyMessage, ToyMessage> plain = await connection.OpenAsync(Toy.Server().Build(), Ct);
        await toServer.Writer.WriteAsync(Toy.Concat(Toy.HelloFrame("plain"), Toy.Frame(2, 9)));
        await plain.ReadAsync(Ct);

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => plain.SwitchAsync(
            new Data { Payload = [1] }, Toy.Server().Build(), (stream, _) => ValueTask.FromResult<Stream>(new XorStream(stream, 0x5A)), Ct).AsTask());
        Assert.Equal(ViolationCode.UnexpectedMessage, ex.Violation.Code);
    }
}
