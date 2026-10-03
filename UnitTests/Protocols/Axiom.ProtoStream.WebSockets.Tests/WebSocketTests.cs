using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Http;
using Axiom.ProtoStream.Testing;
using Axiom.ProtoStream.WebSockets;

using Axiom.ProtoStream.Tests.Shared;

namespace Axiom.ProtoStream.WebSockets.Tests;

public sealed partial class WebSocketTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>The masking key of the examples in RFC 6455 section 5.7.</summary>
    private static readonly byte[] ExampleKey = [0x37, 0xFA, 0x21, 0x3D];

    private const byte Fin = 0x80;
    private const byte TextOpcode = 0x1;
    private const byte BinaryOpcode = 0x2;
    private const byte ContinuationOpcode = 0x0;
    private const byte CloseOpcode = 0x8;
    private const byte PingOpcode = 0x9;
    private const byte PongOpcode = 0xA;

    /// <summary>A client frame: masked with the RFC example key unless told otherwise.</summary>
    private static byte[] ClientFrame(byte firstByte, byte[] payload, bool masked = true)
    {
        var frame = new List<byte> { firstByte };
        byte maskBit = masked ? (byte)0x80 : (byte)0;
        if (payload.Length <= 125)
            frame.Add((byte)(maskBit | payload.Length));
        else if (payload.Length <= ushort.MaxValue)
        {
            frame.Add((byte)(maskBit | 126));
            frame.Add((byte)(payload.Length >> 8));
            frame.Add((byte)payload.Length);
        }
        else
        {
            frame.Add((byte)(maskBit | 127));
            for (int shift = 56; shift >= 0; shift -= 8)
                frame.Add((byte)((long)payload.Length >> shift));
        }

        if (masked)
            frame.AddRange(ExampleKey);
        frame.AddRange(masked ? payload.Select((b, i) => (byte)(b ^ ExampleKey[i % 4])) : payload);
        return [.. frame];
    }

    private static byte[] CloseFrame(int code, string reason = "") =>
        ClientFrame(Fin | CloseOpcode, [(byte)(code >> 8), (byte)code, .. Encoding.UTF8.GetBytes(reason)]);

    private sealed class WsPair : IAsyncDisposable
    {
        private WsPair(TransportPair transport, Connection connection, Session<WsMessage, WsMessage> session)
        {
            Transport = transport;
            Connection = connection;
            Session = session;
        }

        public TransportPair Transport { get; }

        public Connection Connection { get; }

        public Session<WsMessage, WsMessage> Session { get; }

        public static async Task<WsPair> OpenAsync(ProtocolDefinition<WsMessage, WsMessage>? definition = null, ConnectionOptions? options = null)
        {
            TransportPair transport = InMemoryTransport.CreatePair();
            Connection connection = Connection.FromPipe(transport.Server, options ?? ConnectionOptions.Default);
            Session<WsMessage, WsMessage> session = await connection.OpenAsync(definition ?? WebSocket.Server(), Ct);
            return new WsPair(transport, connection, session);
        }

        public async Task SendAsync(params byte[][] frames)
        {
            foreach (byte[] frame in frames)
                await Transport.Client.Output.WriteAsync(frame);
        }

        public async Task<byte[]> ReceiveAsync(int count)
        {
            var received = new List<byte>();
            while (received.Count < count)
            {
                ReadResult read = await Transport.Client.Input.ReadAsync();
                ReadOnlySequence<byte> taken = read.Buffer.Slice(0, Math.Min(read.Buffer.Length, count - received.Count));
                received.AddRange(taken.ToArray());
                Transport.Client.Input.AdvanceTo(taken.End);
                if (read.IsCompleted)
                    break;
            }

            return [.. received];
        }

        // Disposing closes the session, which waits for the peer's close frame. Ending the client side first lets
        // that wait end at once instead of after the close timeout (which never passes on a fake clock).
        public async ValueTask DisposeAsync()
        {
            await Transport.Client.Output.CompleteAsync();
            await Connection.DisposeAsync();
        }
    }

    // ---- opening handshake: RFC 6455 section 4 ---------------------------------------------------

    [Fact]
    public void TheAcceptKeyMatchesTheRfcExample() =>
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", WebSocket.ComputeAcceptKey("dGhlIHNhbXBsZSBub25jZQ=="));

    private const string Handshake =
        "GET /chat HTTP/1.1\r\nHost: server.example.com\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
        "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nOrigin: http://example.com\r\nSec-WebSocket-Protocol: chat, superchat\r\n" +
        "Sec-WebSocket-Version: 13\r\n\r\n";

    // The client may send its first frame right behind the handshake; the switch must not lose it.
    [Fact]
    public async Task AnUpgradeSwitchesTheConnectionAndKeepsFramesSentAlongWithTheHandshake()
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<HttpRequest, HttpResponse> http = await connection.OpenAsync(Http11.Server(), Ct);
        byte[] handshakeAndFrame = [.. Encoding.ASCII.GetBytes(Handshake), .. ClientFrame(Fin | TextOpcode, "Hello"u8.ToArray())];
        await transport.Client.Output.WriteAsync(handshakeAndFrame);

        HttpRequest request = (await http.ReadAsync(Ct)).Message!;
        Assert.True(WebSocket.IsUpgrade(request));
        Assert.True(WebSocket.TryAccept(request, new WebSocketAcceptOptions { SubProtocol = "chat" }, out var accepted, out _));
        Session<WsMessage, WsMessage> ws = await http.SwitchAsync(accepted!, Ct);

        WsText hello = Assert.IsType<WsText>((await ws.ReadAsync(Ct)).Message);
        Assert.Equal("Hello", hello.Text);
        await ws.WriteAsync(hello, Ct);

        ReadResult sent = await transport.Client.Input.ReadAsync();
        string all = Encoding.ASCII.GetString(sent.Buffer.ToArray());
        Assert.StartsWith("HTTP/1.1 101 Switching Protocols\r\n", all);
        Assert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n", all);
        Assert.Contains("Sec-WebSocket-Protocol: chat\r\n", all);
        Assert.Contains("Connection: upgrade\r\n", all);
        Assert.EndsWith("\r\n\r\n\u0081\u0005Hello", Encoding.Latin1.GetString(sent.Buffer.ToArray()));
    }

    [Theory]
    [InlineData("Sec-WebSocket-Version: 13\r\n", "Sec-WebSocket-Version: 8\r\n", HttpStatus.UpgradeRequired)]
    [InlineData("Sec-WebSocket-Version: 13\r\n", "", HttpStatus.UpgradeRequired)]
    [InlineData("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n", "", HttpStatus.BadRequest)]
    [InlineData("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n", "Sec-WebSocket-Key: c2hvcnQ=\r\n", HttpStatus.BadRequest)]
    [InlineData("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n", "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n", HttpStatus.BadRequest)]
    [InlineData("GET /chat", "POST /chat", HttpStatus.BadRequest)]
    [InlineData("Upgrade: websocket\r\n", "Upgrade: other\r\n", HttpStatus.BadRequest)]
    public async Task AnInvalidHandshakeGetsTheResponseTheRfcAsksFor(string original, string replacement, int status)
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<HttpRequest, HttpResponse> http = await connection.OpenAsync(Http11.Server(), Ct);
        await transport.Client.Output.WriteAsync(Encoding.ASCII.GetBytes(Handshake.Replace(original, replacement, StringComparison.Ordinal)));

        HttpRequest request = (await http.ReadAsync(Ct)).Message!;

        Assert.False(WebSocket.TryAccept(request, new WebSocketAcceptOptions(), out _, out HttpResponse? rejection));
        Assert.Equal(status, rejection!.StatusCode);
        if (status == HttpStatus.UpgradeRequired)
            Assert.Contains(rejection.Headers, f => f.Key == WebSocket.VersionField && f.Value == WebSocket.SupportedVersion);
        await http.WriteAsync(rejection, Ct);
        Assert.Throws<ProtocolStateException>(() => WebSocket.Accept(request));
    }

    [Fact]
    public async Task ASubprotocolTheClientDidNotOfferIsRefused()
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<HttpRequest, HttpResponse> http = await connection.OpenAsync(Http11.Server(), Ct);
        await transport.Client.Output.WriteAsync(Encoding.ASCII.GetBytes(Handshake));

        HttpRequest request = (await http.ReadAsync(Ct)).Message!;

        Assert.False(WebSocket.TryAccept(request, new WebSocketAcceptOptions { SubProtocol = "mqtt" }, out _, out HttpResponse? rejection));
        Assert.Equal(HttpStatus.BadRequest, rejection!.StatusCode);
    }

    // ---- framing: RFC 6455 section 5 -------------------------------------------------------------

    [Fact]
    public void TheMaskedTextExampleOfTheRfcDecodes()
    {
        byte[] example = [0x81, 0x85, 0x37, 0xFA, 0x21, 0x3D, 0x7F, 0x9F, 0x4D, 0x51, 0x58];
        using var runner = new CodecRunner<WsMessage, WsMessage>(WebSocket.Server());

        foreach (ReadOnlySequence<byte> input in Segments.EverySplit(example))
        {
            DecodeOutcome<WsMessage> outcome = runner.Decode(input, isCompleted: true);
            Assert.Equal("Hello", Assert.IsType<WsText>(outcome.Message).Text);
        }
    }

    [Fact]
    public void TheUnmaskedExamplesOfTheRfcDecodeOnTheClientSide()
    {
        using var runner = new CodecRunner<WsMessage, WsMessage>(WebSocket.Client());
        byte[] fragmented = [0x01, 0x03, 0x48, 0x65, 0x6C, 0x80, 0x02, 0x6C, 0x6F];
        byte[] ping = [0x89, 0x05, 0x48, 0x65, 0x6C, 0x6C, 0x6F];

        Assert.Equal("Hello", Assert.IsType<WsText>(runner.Decode(new ReadOnlySequence<byte>(fragmented), true).Message).Text);
        Assert.Equal("Hello", Encoding.ASCII.GetString(Assert.IsType<WsPing>(runner.Decode(new ReadOnlySequence<byte>(ping), true).Message).Data.Span));
    }

    // Section 5.3: the client masks every frame with a fresh key from a strong source, so a page cannot steer
    // the bytes an intermediary sees. Two identical messages must therefore differ on the wire.
    [Fact]
    public void TheClientMasksEveryFrameWithAFreshKey()
    {
        using var client = new CodecRunner<WsMessage, WsMessage>(WebSocket.Client());
        using var server = new CodecRunner<WsMessage, WsMessage>(WebSocket.Server());
        var first = new ArrayBufferWriter<byte>();
        var second = new ArrayBufferWriter<byte>();

        client.Encode(WsMessage.CreateText("Hello"), first);
        client.Encode(WsMessage.CreateText("Hello"), second);

        Assert.Equal(0x80, first.WrittenSpan[1] & 0x80);
        Assert.Equal(0x80, second.WrittenSpan[1] & 0x80);
        Assert.NotEqual(first.WrittenSpan[2..6].ToArray(), second.WrittenSpan[2..6].ToArray());
        Assert.NotEqual(first.WrittenSpan.ToArray(), second.WrittenSpan.ToArray());
        Assert.Equal("Hello", Assert.IsType<WsText>(server.Decode(new ReadOnlySequence<byte>(first.WrittenMemory), true).Message).Text);
        Assert.Equal("Hello", Assert.IsType<WsText>(server.Decode(new ReadOnlySequence<byte>(second.WrittenMemory), true).Message).Text);
    }

    // A ping the framework answers between two texts must not move the texts' rotation: the second text would
    // land in the object the application still holds for the first, which then shows new content without
    // throwing.
    [Fact]
    public async Task APingBetweenTwoTextsLeavesTheFirstTextStale()
    {
        await using WsPair ws = await WsPair.OpenAsync();
        await ws.SendAsync(
            ClientFrame(Fin | TextOpcode, "one"u8.ToArray()),
            ClientFrame(Fin | PingOpcode, []),
            ClientFrame(Fin | TextOpcode, "two"u8.ToArray()));
        WsText first = Assert.IsType<WsText>((await ws.Session.ReadAsync(Ct)).Message);
        Assert.Equal("one", first.Text);

        WsText second = Assert.IsType<WsText>((await ws.Session.ReadAsync(Ct)).Message);

        Assert.Equal("two", second.Text);
        Assert.NotSame(first, second);
        Assert.Throws<StaleMessageException>(() => first.Text);
    }

    // Control frames may be injected between the fragments of a message (section 5.4).
    [Fact]
    public async Task APingBetweenFragmentsIsAnsweredWhileTheMessageAssembles()
    {
        await using WsPair ws = await WsPair.OpenAsync();
        await ws.SendAsync(
            ClientFrame(TextOpcode, "Hel"u8.ToArray()),
            ClientFrame(Fin | PingOpcode, "probe"u8.ToArray()),
            ClientFrame(Fin | ContinuationOpcode, "lo"u8.ToArray()));

        Assert.Equal("Hello", Assert.IsType<WsText>((await ws.Session.ReadAsync(Ct)).Message).Text);
        Assert.Equal([Fin | PongOpcode, 5, .. "probe"u8.ToArray()], await ws.ReceiveAsync(7));
    }

    [Fact]
    public async Task SixteenAndSixtyFourBitLengthsAreDecoded()
    {
        await using WsPair ws = await WsPair.OpenAsync();
        byte[] medium = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        byte[] large = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7)).ToArray();
        await ws.SendAsync(ClientFrame(Fin | BinaryOpcode, medium), ClientFrame(Fin | BinaryOpcode, large));

        Assert.Equal(medium, Assert.IsType<WsBinary>((await ws.Session.ReadAsync(Ct)).Message).Data.ToArray());
        Assert.Equal(large, Assert.IsType<WsBinary>((await ws.Session.ReadAsync(Ct)).Message).Data.ToArray());
    }

    public static TheoryData<string, byte[], int> ProtocolErrors => new()
    {
        { "unmasked client frame", ClientFrame(Fin | TextOpcode, "x"u8.ToArray(), masked: false), WsCloseCode.ProtocolError },
        { "reserved bit", ClientFrame(Fin | 0x40 | TextOpcode, "x"u8.ToArray()), WsCloseCode.ProtocolError },
        { "reserved opcode", ClientFrame(Fin | 0x3, "x"u8.ToArray()), WsCloseCode.ProtocolError },
        { "fragmented ping", ClientFrame(PingOpcode, "x"u8.ToArray()), WsCloseCode.ProtocolError },
        { "long ping", ClientFrame(Fin | PingOpcode, new byte[126]), WsCloseCode.ProtocolError },
        { "orphan continuation", ClientFrame(Fin | ContinuationOpcode, "x"u8.ToArray()), WsCloseCode.ProtocolError },
        { "interleaved message", [.. ClientFrame(TextOpcode, "a"u8.ToArray()), .. ClientFrame(Fin | TextOpcode, "b"u8.ToArray())], WsCloseCode.ProtocolError },
        { "non-minimal length", [Fin | BinaryOpcode, 0x80 | 126, 0, 5, .. ExampleKey, 1, 2, 3, 4, 5], WsCloseCode.ProtocolError },
        { "one-byte close", ClientFrame(Fin | CloseOpcode, [3]), WsCloseCode.ProtocolError },
        { "reserved close code", CloseFrame(WsCloseCode.NoStatusReceived), WsCloseCode.ProtocolError },
        { "invalid UTF-8 text", ClientFrame(Fin | TextOpcode, [0xC3, 0x28]), WsCloseCode.InvalidPayloadData },
        { "invalid UTF-8 close reason", ClientFrame(Fin | CloseOpcode, [0x03, 0xE8, 0xC3, 0x28]), WsCloseCode.InvalidPayloadData },
    };

    [Theory]
    [MemberData(nameof(ProtocolErrors))]
    public async Task AProtocolErrorFailsTheConnectionWithTheRightCloseCode(string reason, byte[] input, int closeCode)
    {
        await using WsPair ws = await WsPair.OpenAsync();
        await ws.SendAsync(input);

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ws.Session.ReadAsync(Ct).AsTask());

        Assert.True(closeCode == ex.Violation.ProtocolErrorCode, reason);
        Assert.Equal([Fin | CloseOpcode, 2, (byte)(closeCode >> 8), (byte)closeCode], await ws.ReceiveAsync(4));
    }

    // Section 8.1 and Autobahn 6.4: invalid UTF-8 fails the connection at the fragment that contains it.
    [Fact]
    public async Task InvalidUtf8FailsAtTheFragmentThatContainsItNotAtTheEnd()
    {
        await using WsPair ws = await WsPair.OpenAsync();
        // "κόσμε" then a code point above U+10FFFF, in a message whose last fragment never arrives.
        await ws.SendAsync(ClientFrame(TextOpcode, [0xCE, 0xBA, 0xE1, 0xBD, 0xB9, 0xCF, 0x83, 0xCE, 0xBC, 0xCE, 0xB5, 0xF4, 0x90, 0x80, 0x80]));

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ws.Session.ReadAsync(Ct).AsTask());

        Assert.Equal(WsCloseCode.InvalidPayloadData, ex.Violation.ProtocolErrorCode);
    }

    // Autobahn 6.4.3/6.4.4: one text frame arrives in pieces; the invalid byte fails it before the frame is complete.
    [Fact]
    public async Task InvalidUtf8FailsInsideAFrameThatIsStillArriving()
    {
        await using WsPair ws = await WsPair.OpenAsync();
        byte[] frame = ClientFrame(Fin | TextOpcode, [.. "valid"u8.ToArray(), 0xF4, 0x90, 0x80, 0x80, .. new byte[100]]);

        await ws.SendAsync(frame[..14]); // header, key and the first bytes of the payload, including the invalid ones

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ws.Session.ReadAsync(Ct).AsTask());
        Assert.Equal(WsCloseCode.InvalidPayloadData, ex.Violation.ProtocolErrorCode);
    }

    // A frame's payload is consumed as it arrives, so a pipe that pauses its writer early cannot deadlock it.
    [Fact]
    public async Task AFrameLargerThanThePipesPauseThresholdIsReceived()
    {
        TransportPair transport = InMemoryTransport.CreatePair(new PipeOptions(pauseWriterThreshold: 4096, resumeWriterThreshold: 2048, useSynchronizationContext: false));
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<WsMessage, WsMessage> session = await connection.OpenAsync(WebSocket.Server(), Ct);
        byte[] payload = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();

        Task send = transport.Client.Output.WriteAsync(ClientFrame(Fin | BinaryOpcode, payload)).AsTask();
        WsBinary received = Assert.IsType<WsBinary>((await session.ReadAsync(Ct)).Message);
        Assert.Equal(payload, received.Data.ToArray());

        // The bytes of a delivered message are released by the next read, which lets the paused writer finish.
        Task<ProtocolReadResult<WsMessage>> next = session.ReadAsync(Ct).AsTask();
        await send;
        await transport.Client.Output.CompleteAsync();
        Assert.True((await next).IsCompleted);
    }

    // A code point cut in half by a fragment boundary is valid once the next fragment completes it.
    [Fact]
    public async Task ACodePointSplitAcrossFragmentsIsValid()
    {
        await using WsPair ws = await WsPair.OpenAsync();
        byte[] euro = "€"u8.ToArray();
        await ws.SendAsync(ClientFrame(TextOpcode, euro[..1]), ClientFrame(ContinuationOpcode, euro[1..2]), ClientFrame(Fin | ContinuationOpcode, euro[2..]));

        Assert.Equal("€", Assert.IsType<WsText>((await ws.Session.ReadAsync(Ct)).Message).Text);
    }

    // The limit applies to the declared length: the peer cannot make the server wait for, or buffer, a huge frame.
    [Fact]
    public async Task AnOversizedMessageIsRefusedFromItsHeaderAlone()
    {
        await using WsPair ws = await WsPair.OpenAsync(WebSocket.Server(new WebSocketOptions { MaxMessageSize = 16 }));
        await ws.SendAsync([Fin | BinaryOpcode, 0x80 | 100, .. ExampleKey]);

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ws.Session.ReadAsync(Ct).AsTask());

        Assert.Equal(WsCloseCode.MessageTooBig, ex.Violation.ProtocolErrorCode);
    }

    // ---- closing: RFC 6455 section 5.5.1 and 7 ---------------------------------------------------

    [Fact]
    public async Task APeerCloseIsEchoedAndEndsTheSession()
    {
        await using WsPair ws = await WsPair.OpenAsync();
        await ws.SendAsync(CloseFrame(WsCloseCode.GoingAway, "bye"));

        Assert.True((await ws.Session.ReadAsync(Ct)).IsCompleted);
        Assert.Equal(SessionStatus.Closed, ws.Session.Status);
        Assert.Equal([Fin | CloseOpcode, 2, 0x03, 0xE9], await ws.ReceiveAsync(4));
    }

    [Fact]
    public async Task ClosingRunsTheHandshakeAndRefusesDataAfterTheCloseFrame()
    {
        await using WsPair ws = await WsPair.OpenAsync();

        Task close = ws.Session.CloseAsync(Ct).AsTask();
        Assert.Equal([Fin | CloseOpcode, 2, 0x03, 0xE8], await ws.ReceiveAsync(4));
        Assert.Equal(WebSocket.Closing, ws.Session.State);
        await Assert.ThrowsAsync<ProtocolStateException>(() => ws.Session.WriteAsync(WsMessage.CreateText("late"), Ct).AsTask());

        await ws.SendAsync(ClientFrame(Fin | TextOpcode, "ignored"u8.ToArray()), CloseFrame(WsCloseCode.NormalClosure));
        await close;
        Assert.Equal(SessionStatus.Closed, ws.Session.Status);
    }

    [Fact]
    public void OnlySendableCloseCodesCanBeCreated()
    {
        Assert.Throws<ProtocolStateException>(() => WsMessage.CreateClose(WsCloseCode.NoStatusReceived, ""));
        Assert.Throws<ProtocolStateException>(() => WsMessage.CreateClose(WsCloseCode.AbnormalClosure, ""));
        Assert.Throws<ProtocolStateException>(() => WsMessage.CreateClose(WsCloseCode.NormalClosure, new string('x', WsMessage.MaxCloseReasonBytes + 1)));
        Assert.Throws<ProtocolStateException>(() => WsMessage.CreateClose(null, "reason without code"));
        Assert.Throws<ProtocolStateException>(() => WsMessage.CreatePing(new byte[WsMessage.MaxControlPayload + 1]));
    }

    // ---- roles, masking, fragmentation -------------------------------------------------------------

    [Fact]
    public async Task AClientMasksEveryFrameWithAFreshKeyAndRefusesMaskedServerFrames()
    {
        await using WsPair ws = await WsPair.OpenAsync(WebSocket.Client());

        await ws.Session.WriteAsync(WsMessage.CreateText("Hello"), Ct);
        await ws.Session.WriteAsync(WsMessage.CreateText("Hello"), Ct);
        byte[] frames = await ws.ReceiveAsync(22);

        Assert.Equal(0x80 | 5, frames[1]);
        byte[] firstKey = frames[2..6];
        byte[] secondKey = frames[13..17];
        Assert.Equal("Hello"u8.ToArray(), frames[6..11].Select((b, i) => (byte)(b ^ firstKey[i % 4])).ToArray());
        Assert.NotEqual(firstKey, secondKey);

        await ws.SendAsync(ClientFrame(Fin | TextOpcode, "x"u8.ToArray()));
        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ws.Session.ReadAsync(Ct).AsTask());
        Assert.Equal(WsCloseCode.ProtocolError, ex.Violation.ProtocolErrorCode);
    }

    [Fact]
    public async Task LongMessagesAreSplitIntoFragmentsWhenAsked()
    {
        await using WsPair ws = await WsPair.OpenAsync(WebSocket.Server(new WebSocketOptions { FragmentSize = 4 }));

        await ws.Session.WriteAsync(WsMessage.CreateText("Hello"), Ct);

        Assert.Equal([TextOpcode, 4, .. "Hell"u8.ToArray(), Fin | ContinuationOpcode, 1, (byte)'o'], await ws.ReceiveAsync(9));
    }

    [Fact]
    public async Task AKeepAlivePingIsSentWhenTheConnectionIsQuiet()
    {
        var time = new FakeTimeProvider();
        await using WsPair ws = await WsPair.OpenAsync(
            WebSocket.Server(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) }),
            new ConnectionOptions { TimeProvider = time });

        time.Advance(TimeSpan.FromSeconds(11));

        Assert.Equal([Fin | PingOpcode, 0], await ws.ReceiveAsync(2));
    }

    [Fact]
    public async Task AReceivedMessageGoesStaleAfterTheNextReadAndARetainedCopySurvives()
    {
        await using WsPair ws = await WsPair.OpenAsync();
        await ws.SendAsync(ClientFrame(Fin | TextOpcode, "first"u8.ToArray()), ClientFrame(Fin | TextOpcode, "second"u8.ToArray()));

        WsText first = Assert.IsType<WsText>((await ws.Session.ReadAsync(Ct)).Message);
        WsText kept = first.Retain();
        await ws.Session.ReadAsync(Ct);

        Assert.Throws<StaleMessageException>(() => first.Text);
        Assert.Equal("first", kept.Text);
    }

    // ---- harness and allocations ---------------------------------------------------------------

    [Fact]
    public void TheFrameDecoderSurvivesMutationFuzzing()
    {
        byte[][] samples =
        [
            ClientFrame(Fin | TextOpcode, "Hello"u8.ToArray()),
            [.. ClientFrame(TextOpcode, "Hel"u8.ToArray()), .. ClientFrame(Fin | PingOpcode, "p"u8.ToArray()), .. ClientFrame(Fin | ContinuationOpcode, "lo"u8.ToArray())],
            ClientFrame(Fin | BinaryOpcode, new byte[200]),
            CloseFrame(WsCloseCode.NormalClosure, "bye"),
        ];

        FuzzReport report = CodecHarness.Fuzz(WebSocket.Server(), samples, seed: 6455, iterations: 20_000);

        Assert.True(report.Messages > 0 && report.Invalid > 0);
    }

    [Fact]
    public void TheServerDefinitionHasNoWarnings() => DefinitionAssert.NoWarnings(WebSocket.Server());

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }

    // Echoing a received message decodes into the session's buffer and encodes from it: no copies on the heap.
    [ReleaseFact]
    public async Task EchoingMessagesAllocatesNothingPerMessage()
    {
        const int Messages = 5_000;
        byte[] frame = ClientFrame(Fin | TextOpcode, "Hello, WebSocket!"u8.ToArray());
        var unbounded = new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false);
        Pipe rawIn = new(unbounded), rawOut = new(unbounded), input = new(unbounded), output = new(unbounded);
        for (int i = 0; i < Messages; i++)
        {
            rawIn.Writer.Write(frame);
            input.Writer.Write(frame);
        }

        await rawIn.Writer.FlushAsync();
        await input.Writer.FlushAsync();

        await using Connection connection = Connection.FromPipe(new Duplex(input.Reader, output.Writer));
        Session<WsMessage, WsMessage> session = await connection.OpenAsync(WebSocket.Server(new WebSocketOptions { KeepAliveInterval = null }), Ct);

        async Task EchoAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await session.WriteAsync((await session.ReadAsync(Ct)).Message!, Ct);
                if (output.Reader.TryRead(out ReadResult sent))
                    output.Reader.AdvanceTo(sent.Buffer.End);
            }
        }

        async Task RawAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                ReadResult read = await rawIn.Reader.ReadAsync();
                rawIn.Reader.AdvanceTo(read.Buffer.GetPosition(frame.Length));
                rawOut.Writer.Write(frame.AsSpan(0, frame.Length - 4));
                await rawOut.Writer.FlushAsync();
                if (rawOut.Reader.TryRead(out ReadResult sent))
                    rawOut.Reader.AdvanceTo(sent.Buffer.End);
            }
        }

        await EchoAsync(100);
        await RawAsync(100);

        long before = GC.GetAllocatedBytesForCurrentThread();
        await RawAsync(Messages - 100);
        long pipes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        await EchoAsync(Messages - 100);
        long echoed = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(echoed - pipes < 2048, $"Echoing allocated {echoed} bytes where the bare pipes allocated {pipes}.");
    }
}
