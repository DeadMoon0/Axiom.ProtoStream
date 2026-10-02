using System;

namespace ProtoStream.Http;

/// <summary>Limits of the HTTP/1.1 server role. The defaults follow Kestrel's.</summary>
public sealed class Http11Options
{
    /// <summary>Longest request line. Default 8 KiB; longer gets 414.</summary>
    public int MaxRequestLineBytes { get; init; } = 8 * 1024;

    /// <summary>Largest request head (request line plus header fields). Default 32 KiB; larger gets 431.</summary>
    public int MaxRequestHeadBytes { get; init; } = 32 * 1024;

    /// <summary>Most header fields. Default 100; more gets 431.</summary>
    public int MaxHeaderCount { get; init; } = 100;

    /// <summary>Largest request body. Default 30 MB; a larger declared length gets 413.</summary>
    public long MaxRequestBodySize { get; init; } = 30_000_000;

    /// <summary>Largest trailer section of a chunked body. Default 8 KiB.</summary>
    public int MaxTrailerBytes { get; init; } = 8 * 1024;

    /// <summary>Most bytes of an unread body discarded to reach the next request. Default 1 MiB.</summary>
    public long MaxUnreadBodyDrain { get; init; } = 1024 * 1024;

    /// <summary>Time allowed for the first request head of a connection. Default 30 seconds.</summary>
    public TimeSpan RequestHeadersTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time a kept-alive connection may wait for its next complete request head. Default 130 seconds.</summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(130);

    /// <summary>Clock for the Date field every response carries (RFC 9110 section 6.6.1).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>HTTP/1.1 protocol definitions.</summary>
public static class Http11
{
    /// <summary>State: waiting for the next request.</summary>
    public const string AwaitRequest = "AwaitRequest";

    /// <summary>State: the application owes the response to the last request.</summary>
    public const string AwaitResponse = "AwaitResponse";

    /// <summary>
    /// Buffer room beyond the largest head, so the codec's own head limit (and its precise 431) always triggers
    /// before the session's generic buffer limit.
    /// </summary>
    private const int BufferHeadroom = 4096;

    private static readonly Lazy<ProtocolDefinition<HttpRequest, HttpResponse>> DefaultServer = new(() => Server(new Http11Options()));

    /// <summary>The server role with default limits: reads requests, writes responses, one response per request.</summary>
    public static ProtocolDefinition<HttpRequest, HttpResponse> Server() => DefaultServer.Value;

    /// <summary>The server role with the given limits.</summary>
    public static ProtocolDefinition<HttpRequest, HttpResponse> Server(Http11Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Protocol.Describe<HttpRequest, HttpResponse>("http/1.1")
            .Codec(() => new Http11ServerCodec(options))
            .States(s => s
                .Start(AwaitRequest)
                .In(AwaitRequest).On<HttpRequest>().Delegate().GoTo(AwaitResponse)
                .In(AwaitResponse)
                    // Interim (1xx) responses precede the final one; the request is answered only by the final one.
                    .OnSend<HttpResponse>().GoToIf(response => HttpStatus.IsInformational(response.StatusCode), AwaitResponse, AwaitRequest)
                    .Switchable())
            .Limits(l => l
                .FirstMessageTimeout(options.RequestHeadersTimeout)
                .IdleTimeout(options.KeepAliveTimeout)
                .MaxBufferedBytes(options.MaxRequestHeadBytes + BufferHeadroom)
                .MaxPayloadDrain(options.MaxUnreadBodyDrain))
            .ReplyToViolations(ReplyTo)
            .Build();
    }

    // A timeout or a truncated request gets no reply: on an idle kept-alive connection the client may already
    // be sending its next request and would read the error as that request's response.
    private static HttpResponse? ReplyTo(Violation violation) => violation.Code switch
    {
        ViolationCode.Timeout or ViolationCode.Truncated => null,
        _ => HttpResponse.Error(violation.ProtocolErrorCode ?? violation.Code switch
        {
            ViolationCode.LimitExceeded => HttpStatus.RequestHeaderFieldsTooLarge,
            ViolationCode.Unsupported => HttpStatus.NotImplemented,
            _ => HttpStatus.BadRequest,
        }),
    };
}
