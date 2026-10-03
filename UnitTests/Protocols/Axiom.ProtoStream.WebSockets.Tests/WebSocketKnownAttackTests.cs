using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Http;
using Axiom.ProtoStream.Testing;

namespace Axiom.ProtoStream.WebSockets.Tests;

/// <summary>
/// Inputs behind published vulnerabilities in other WebSocket implementations (CVE or advisory named per case).
/// </summary>
public sealed partial class WebSocketTests
{
    private const byte MaskedLength64 = 0x80 | 127;

    // A continuation whose 63-bit length overflowed "assembled + length" in gorilla/websocket (CVE-2020-27813):
    // it must be refused from its header with 1009, before a byte of it is read.
    [Fact]
    public async Task AContinuationWithAHugeLengthIsRefusedFromItsHeader()
    {
        await using WsPair ws = await WsPair.OpenAsync();
        await ws.SendAsync(
            [BinaryOpcode, 0x80 | 1, 0, 0, 0, 0, (byte)'a'],
            [Fin | ContinuationOpcode, MaskedLength64, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0]);

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ws.Session.ReadAsync(Ct).AsTask());

        Assert.Equal(WsCloseCode.MessageTooBig, ex.Violation.ProtocolErrorCode);
        Assert.Equal([Fin | CloseOpcode, 2, 0x03, 0xF1], await ws.ReceiveAsync(4));
    }

    // The size limit holds for the message, not the frame: fragments under the limit add up (Bandit advisory).
    [Fact]
    public async Task FragmentsAddingUpPastTheLimitAreRefused()
    {
        await using WsPair ws = await WsPair.OpenAsync(WebSocket.Server(new WebSocketOptions { MaxMessageSize = 10 }));
        await ws.SendAsync(ClientFrame(BinaryOpcode, new byte[6]), ClientFrame(Fin | ContinuationOpcode, new byte[6]));

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ws.Session.ReadAsync(Ct).AsTask());

        Assert.Equal(WsCloseCode.MessageTooBig, ex.Violation.ProtocolErrorCode);
    }

    [Fact]
    public async Task ALengthWithItsMostSignificantBitSetIsAProtocolError() // RFC 6455 section 5.2
    {
        await using WsPair ws = await WsPair.OpenAsync();
        await ws.SendAsync([Fin | BinaryOpcode, MaskedLength64, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ws.Session.ReadAsync(Ct).AsTask());

        Assert.Equal(WsCloseCode.ProtocolError, ex.Violation.ProtocolErrorCode);
    }

    // A ping flood is a policy violation (1008), not an oversized message (CVE-2019-9512 class).
    [Fact]
    public async Task APingFloodIsClosedAsAPolicyViolation()
    {
        await using WsPair ws = await WsPair.OpenAsync();
        byte[] ping = ClientFrame(Fin | PingOpcode, []);
        await ws.SendAsync(Enumerable.Repeat(ping, 2000).SelectMany(frame => frame).ToArray());

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ws.Session.ReadAsync(Ct).AsTask());

        Assert.Equal(ViolationCode.RateExceeded, ex.Violation.Code);
        const int AnsweredPongs = 1000; // the default budget per second
        byte[] received = await ws.ReceiveAsync((AnsweredPongs * 2) + 4);
        Assert.Equal([Fin | CloseOpcode, 2, 0x03, 0xF0], received[^4..]); // 1008
    }

    // An unfinished message kept alive by pings the framework answers: the message's deadline must stand.
    [Fact]
    public async Task PingsDoNotKeepAnUnfinishedMessageAlive()
    {
        var time = new FakeTimeProvider();
        await using WsPair ws = await WsPair.OpenAsync(WebSocket.Server(new WebSocketOptions { KeepAliveInterval = null }), new ConnectionOptions { TimeProvider = time });
        await ws.SendAsync(ClientFrame(TextOpcode, "a"u8.ToArray()));
        Task<ProtocolReadResult<WsMessage>> read = ws.Session.ReadAsync(Ct).AsTask();

        for (int i = 0; i < 3 && !read.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(50));
            await ws.SendAsync(ClientFrame(Fin | PingOpcode, []));
            await ws.ReceiveAsync(2); // the pong
        }

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => read);
        Assert.Equal(ViolationCode.Timeout, ex.Violation.Code);
    }

    // Pings without a message in progress are keep-alive traffic and do keep the connection open.
    [Fact]
    public async Task PingsBetweenMessagesKeepTheConnectionOpen()
    {
        var time = new FakeTimeProvider();
        await using WsPair ws = await WsPair.OpenAsync(WebSocket.Server(new WebSocketOptions { KeepAliveInterval = null }), new ConnectionOptions { TimeProvider = time });
        Task<ProtocolReadResult<WsMessage>> read = ws.Session.ReadAsync(Ct).AsTask();

        for (int i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(50));
            await ws.SendAsync(ClientFrame(Fin | PingOpcode, []));
            await ws.ReceiveAsync(2);
        }

        Assert.False(read.IsCompleted);
        await ws.SendAsync(ClientFrame(Fin | TextOpcode, "hi"u8.ToArray()));
        Assert.Equal("hi", Assert.IsType<WsText>((await read).Message).Text);
    }

    // A close handshake the peer never finishes is bounded by the close timeout (CVE-2024-23672 class).
    [Fact]
    public async Task ACloseThePeerNeverAnswersEndsAfterTheCloseTimeout()
    {
        var time = new FakeTimeProvider();
        await using WsPair ws = await WsPair.OpenAsync(WebSocket.Server(new WebSocketOptions { KeepAliveInterval = null }), new ConnectionOptions { TimeProvider = time });

        Task close = ws.Session.CloseAsync(Ct).AsTask();
        await ws.ReceiveAsync(4);
        Assert.False(close.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(6));

        await close.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SessionStatus.Closed, ws.Session.Status);
    }

    // Bytes sent as the body of an upgrade request are not WebSocket frames (WebSocket/h2c smuggling).
    [Fact]
    public async Task TheBodyOfAnUpgradeRequestIsNotReadAsFrames()
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<HttpRequest, HttpResponse> http = await connection.OpenAsync(Http11.Server(), Ct);
        byte[] smuggled = [Fin | TextOpcode, 0x80, 0, 0, 0, 0];
        await transport.Client.Output.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /ws HTTP/1.1\r\nHost: x\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\n" +
            $"Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nContent-Length: {smuggled.Length}\r\n\r\n").Concat(smuggled).ToArray());
        HttpRequest request = (await http.ReadAsync(Ct)).Message!;
        Assert.True(WebSocket.TryAccept(request, new WebSocketAcceptOptions(), out var accepted, out _));

        Session<WsMessage, WsMessage> ws = await http.SwitchAsync(accepted!, Ct);
        await transport.Client.Output.WriteAsync(ClientFrame(Fin | TextOpcode, "real"u8.ToArray()));

        Assert.Equal("real", Assert.IsType<WsText>((await ws.ReadAsync(Ct)).Message).Text);
    }

    // A refused upgrade closes the connection: a request pipelined behind it is never parsed.
    [Fact]
    public async Task ARefusedUpgradeClosesTheConnection()
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        await using Connection connection = Connection.FromPipe(transport.Server);
        Session<HttpRequest, HttpResponse> http = await connection.OpenAsync(Http11.Server(), Ct);
        await transport.Client.Output.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /ws HTTP/1.1\r\nHost: x\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 8\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\nGET /admin HTTP/1.1\r\nHost: x\r\n\r\n"));
        HttpRequest request = (await http.ReadAsync(Ct)).Message!;

        Assert.False(WebSocket.TryAccept(request, new WebSocketAcceptOptions(), out _, out HttpResponse? rejection));
        await http.WriteAsync(rejection!, Ct);

        ReadResult response = await transport.Client.Input.ReadAsync(Ct);
        string head = Encoding.ASCII.GetString(response.Buffer.ToArray());
        Assert.StartsWith("HTTP/1.1 426 ", head);
        Assert.Contains("Connection: close\r\n", head);
        Assert.True((await http.ReadAsync(Ct)).IsCompleted);
    }
}
