using System;
using System.Security.Cryptography;
using System.Text;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Http;

namespace Axiom.ProtoStream.WebSockets;

/// <summary>Limits and keep-alive of a WebSocket session.</summary>
public sealed class WebSocketOptions
{
    /// <summary>Largest message, after reassembling fragments. Default 1 MiB; larger closes with 1009.</summary>
    public int MaxMessageSize { get; init; } = 1024 * 1024;

    /// <summary>Split outgoing messages into frames of at most this many bytes; null sends each message as one frame.</summary>
    public int? FragmentSize { get; init; }

    /// <summary>Time allowed for the next message to arrive. Default 2 minutes; pongs to the keep-alive pings count.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Time the close handshake may take. Default 5 seconds.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Send a ping when nothing was sent for this long, so a healthy peer answers before the idle timeout. Default 30 seconds; null turns it off.</summary>
    public TimeSpan? KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>What the server agrees to when accepting a handshake.</summary>
public sealed class WebSocketAcceptOptions
{
    /// <summary>The subprotocol to select; the client must have offered it. Null selects none.</summary>
    public string? SubProtocol { get; init; }

    /// <summary>The options of the session that follows.</summary>
    public WebSocketOptions WebSocket { get; init; } = new();
}

/// <summary>RFC 6455 WebSocket definitions and the HTTP/1.1 opening handshake.</summary>
public static class WebSocket
{
    /// <summary>State: messages flow both ways.</summary>
    public const string Open = "Open";

    /// <summary>State: this side sent its close frame and waits for the peer's.</summary>
    public const string Closing = "Closing";

    /// <summary>State: the close handshake is complete.</summary>
    public const string Closed = "Closed";

    /// <summary>The upgrade token of the protocol.</summary>
    public const string UpgradeToken = "websocket";

    /// <summary>The protocol version this implementation speaks (RFC 6455 section 4.1).</summary>
    public const string SupportedVersion = "13";

    /// <summary>The GUID appended to the client key to compute the accept value (RFC 6455 section 1.3).</summary>
    public const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Request field carrying the client nonce.</summary>
    public const string KeyField = "Sec-WebSocket-Key";

    /// <summary>Request and rejection field carrying the protocol version.</summary>
    public const string VersionField = "Sec-WebSocket-Version";

    /// <summary>Response field proving the server understood the handshake.</summary>
    public const string AcceptField = "Sec-WebSocket-Accept";

    /// <summary>Field listing (request) or selecting (response) a subprotocol.</summary>
    public const string ProtocolField = "Sec-WebSocket-Protocol";

    /// <summary>A client nonce is 16 random bytes, base64-encoded (RFC 6455 section 4.1).</summary>
    private const int NonceLength = 16;

    /// <summary>Base64 length of a 16-byte nonce.</summary>
    private const int EncodedNonceLength = 24;

    private static readonly Lazy<ProtocolDefinition<WsMessage, WsMessage>> DefaultServer = new(() => Server(new WebSocketOptions()));
    private static readonly Lazy<ProtocolDefinition<WsMessage, WsMessage>> DefaultClient = new(() => Client(new WebSocketOptions()));

    /// <summary>The server role with default options: client frames must be masked, server frames are not.</summary>
    public static ProtocolDefinition<WsMessage, WsMessage> Server() => DefaultServer.Value;

    /// <summary>The server role with the given options.</summary>
    public static ProtocolDefinition<WsMessage, WsMessage> Server(WebSocketOptions options) => Build(isServer: true, options);

    /// <summary>The client role with default options: frames are masked with a fresh random key each.</summary>
    public static ProtocolDefinition<WsMessage, WsMessage> Client() => DefaultClient.Value;

    /// <summary>The client role with the given options.</summary>
    public static ProtocolDefinition<WsMessage, WsMessage> Client(WebSocketOptions options) => Build(isServer: false, options);

    /// <summary>True when <paramref name="request"/> asks to open a WebSocket.</summary>
    public static bool IsUpgrade(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.IsUpgradeRequest && request.Headers.ContainsToken("Upgrade", UpgradeToken);
    }

    /// <summary>
    /// Accepts the opening handshake with default options. Throws <see cref="ProtocolStateException"/> when the
    /// request is not a valid handshake; use <see cref="TryAccept"/> to answer such requests with the right error.
    /// </summary>
    public static ProtocolSwitch<HttpResponse, WsMessage, WsMessage> Accept(HttpRequest request) => Accept(request, new WebSocketAcceptOptions());

    /// <summary>Accepts the opening handshake with the given options. See <see cref="Accept(HttpRequest)"/>.</summary>
    public static ProtocolSwitch<HttpResponse, WsMessage, WsMessage> Accept(HttpRequest request, WebSocketAcceptOptions options)
    {
        if (!TryAccept(request, options, out ProtocolSwitch<HttpResponse, WsMessage, WsMessage>? accepted, out HttpResponse? rejection))
            throw new ProtocolStateException($"The request is not a valid WebSocket handshake (it would be answered with {rejection!.StatusCode}).")
            {
                Guidance = "Use TryAccept and send the rejection response.",
            };
        return accepted!;
    }

    /// <summary>
    /// Validates the opening handshake (RFC 6455 section 4.2.1). On success <paramref name="accepted"/> is the
    /// switch to send with <c>Session.SwitchAsync</c>; otherwise <paramref name="rejection"/> is the response
    /// the RFC asks for: 426 with the supported version for a version mismatch, 400 for anything else.
    /// </summary>
    public static bool TryAccept(HttpRequest request, WebSocketAcceptOptions options, out ProtocolSwitch<HttpResponse, WsMessage, WsMessage>? accepted, out HttpResponse? rejection)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        accepted = null;

        if (request.Method != HttpRequestMethod.Get || request.Version != HttpProtocolVersion.Http11 || !IsUpgrade(request))
            return Reject(HttpStatus.BadRequest, out rejection);

        if (!TrySingleField(request, VersionField, out string version) || version != SupportedVersion)
        {
            rejection = HttpResponse.Error(HttpStatus.UpgradeRequired);
            rejection.Headers.Add("Upgrade", UpgradeToken).Add(VersionField, SupportedVersion);
            return false;
        }

        Span<byte> nonce = stackalloc byte[NonceLength];
        if (!TrySingleField(request, KeyField, out string key) || key.Length != EncodedNonceLength
            || !Convert.TryFromBase64String(key, nonce, out int nonceLength) || nonceLength != NonceLength)
            return Reject(HttpStatus.BadRequest, out rejection);

        if (options.SubProtocol is { } subProtocol && !request.Headers.ContainsToken(ProtocolField, subProtocol))
            return Reject(HttpStatus.BadRequest, out rejection);

        HttpResponse response = HttpResponse.SwitchingProtocols(UpgradeToken);
        response.Headers.Add(AcceptField, ComputeAcceptKey(key));
        if (options.SubProtocol is not null)
            response.Headers.Add(ProtocolField, options.SubProtocol);

        accepted = ProtocolSwitch.Create(response, Server(options.WebSocket));
        rejection = null;
        return true;
    }

    /// <summary>The Sec-WebSocket-Accept value for a client key: base64(SHA-1(key + GUID)).</summary>
    public static string ComputeAcceptKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[] hash = SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid));
        return Convert.ToBase64String(hash);
    }

    private static bool Reject(int status, out HttpResponse? rejection)
    {
        rejection = HttpResponse.Error(status);
        return false;
    }

    private static bool TrySingleField(HttpRequest request, string name, out string value)
    {
        value = string.Empty;
        int found = 0;
        HttpRequestHeaders headers = request.Headers;
        for (int i = 0; i < headers.Count; i++)
        {
            HttpHeader field = headers[i];
            if (field.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = field.Value;
                found++;
            }
        }

        return found == 1;
    }

    private static ProtocolDefinition<WsMessage, WsMessage> Build(bool isServer, WebSocketOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IPolicyStage<WsMessage, WsMessage> description = Protocol.Describe<WsMessage, WsMessage>(isServer ? "websocket server" : "websocket client")
            .Codec(() => new WsCodec(isServer, options))
            .States(s => s
                .Start(Open)
                .In(Open)
                    .On<WsText>().Delegate()
                    .On<WsBinary>().Delegate()
                    // RFC 6455 section 5.5.2: a ping is answered with a pong carrying the same data.
                    .On<WsPing>().Respond(ping => WsMessage.CreatePong(ping.Data.ToArray()))
                    .On<WsPong>().Wait()
                    // Section 5.5.1: a close frame is answered with a close frame, usually echoing the code.
                    .On<WsClose>().Respond(close => WsMessage.CreateClose(close.Code, string.Empty)).GoTo(Closed)
                    .OnSend<WsText>()
                    .OnSend<WsBinary>()
                    .OnSend<WsPing>()
                    .OnSend<WsPong>()
                    .OnSend<WsClose>().GoTo(Closing)
                // Section 5.5.1: after sending close, no more data frames; wait for the peer's close.
                .In(Closing)
                    .On<WsClose>().Wait().GoTo(Closed)
                    .On<WsText>().Wait()
                    .On<WsBinary>().Wait()
                    .On<WsPing>().Wait()
                    .On<WsPong>().Wait()
                .Final(Closed))
            .Limits(l => l
                .FirstMessageTimeout(options.IdleTimeout)
                .IdleTimeout(options.IdleTimeout)
                .CloseTimeout(options.CloseTimeout)
                .MaxBufferedBytes(checked(options.MaxMessageSize + WsFrame.MaxHeaderSize)))
            .OnClose(static () => WsMessage.CreateClose(WsCloseCode.NormalClosure, string.Empty))
            .ReplyToViolations(ReplyTo);

        if (options.KeepAliveInterval is { } interval)
            description = description.Heartbeat(interval, static () => WsMessage.CreatePing(ReadOnlyMemory<byte>.Empty));
        return description.Build();
    }

    // Section 7.1.7: failing the connection sends a close frame with a fitting code, unless the peer is gone.
    private static WsMessage? ReplyTo(Violation violation) => violation.Code switch
    {
        ViolationCode.Truncated => null,
        _ => WsMessage.CreateClose(violation.ProtocolErrorCode ?? violation.Code switch
        {
            ViolationCode.LimitExceeded => WsCloseCode.MessageTooBig,
            ViolationCode.InvalidData => WsCloseCode.InvalidPayloadData,
            ViolationCode.Timeout => WsCloseCode.GoingAway,
            ViolationCode.Unsupported => WsCloseCode.UnsupportedData,
            _ => WsCloseCode.ProtocolError,
        }, string.Empty),
    };
}
