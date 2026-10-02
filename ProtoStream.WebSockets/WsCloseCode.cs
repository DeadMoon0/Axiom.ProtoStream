namespace ProtoStream.WebSockets;

/// <summary>WebSocket close status codes (RFC 6455 section 7.4.1 and the IANA registry).</summary>
public static class WsCloseCode
{
    /// <summary>1000: the purpose of the connection was fulfilled.</summary>
    public const int NormalClosure = 1000;

    /// <summary>1001: the endpoint is going away, such as a server shutting down or a page navigated away.</summary>
    public const int GoingAway = 1001;

    /// <summary>1002: the peer broke the protocol.</summary>
    public const int ProtocolError = 1002;

    /// <summary>1003: the endpoint cannot accept this type of data.</summary>
    public const int UnsupportedData = 1003;

    /// <summary>1005: reserved; means "no status code was present". Never sent.</summary>
    public const int NoStatusReceived = 1005;

    /// <summary>1006: reserved; means "closed without a close frame". Never sent.</summary>
    public const int AbnormalClosure = 1006;

    /// <summary>1007: a message's data does not match its type, such as invalid UTF-8 in text.</summary>
    public const int InvalidPayloadData = 1007;

    /// <summary>1008: a message violates the endpoint's policy.</summary>
    public const int PolicyViolation = 1008;

    /// <summary>1009: a message is too big to process.</summary>
    public const int MessageTooBig = 1009;

    /// <summary>1010: the client expected an extension the server did not negotiate.</summary>
    public const int MandatoryExtension = 1010;

    /// <summary>1011: the server hit an unexpected condition.</summary>
    public const int InternalError = 1011;

    /// <summary>1012: the service is restarting.</summary>
    public const int ServiceRestart = 1012;

    /// <summary>1013: try again later.</summary>
    public const int TryAgainLater = 1013;

    /// <summary>1014: a gateway received an invalid response upstream.</summary>
    public const int BadGateway = 1014;

    /// <summary>1015: reserved; means "the TLS handshake failed". Never sent.</summary>
    public const int TlsHandshake = 1015;

    /// <summary>3000: first code registered for libraries and frameworks.</summary>
    public const int FirstRegistered = 3000;

    /// <summary>4999: last code of the private-use range.</summary>
    public const int LastPrivateUse = 4999;

    /// <summary>True for codes an endpoint may put on the wire.</summary>
    public static bool IsSendable(int code) =>
        code is >= NormalClosure and <= UnsupportedData
            or >= InvalidPayloadData and <= BadGateway
            or >= FirstRegistered and <= LastPrivateUse;
}
