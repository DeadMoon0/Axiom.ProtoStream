namespace ProtoStream;

/// <summary>Category of a protocol violation; protocol packages map it to their own error replies.</summary>
public enum ViolationCode
{
    /// <summary>Bytes that cannot be parsed as the protocol.</summary>
    Malformed,

    /// <summary>A size or count limit was exceeded.</summary>
    LimitExceeded,

    /// <summary>Well-formed but invalid content, such as invalid UTF-8 in a text message.</summary>
    InvalidData,

    /// <summary>A message the current state does not accept.</summary>
    UnexpectedMessage,

    /// <summary>The peer closed the connection in the middle of a message.</summary>
    Truncated,

    /// <summary>A message did not arrive in time.</summary>
    Timeout,

    /// <summary>A feature of the protocol this implementation does not support.</summary>
    Unsupported,
}

/// <summary>Describes one protocol violation.</summary>
public sealed class Violation
{
    /// <summary>Category of the violation.</summary>
    public required ViolationCode Code { get; init; }

    /// <summary>Human-readable detail for diagnostics. Never contains peer payload bytes.</summary>
    public required string Detail { get; init; }

    /// <summary>Name of the protocol.</summary>
    public required string Protocol { get; init; }

    /// <summary>Name of the state the session was in.</summary>
    public required string State { get; init; }

    /// <summary>
    /// The protocol's own error code for the violation when the reader named one, such as HTTP 413 or
    /// WebSocket close code 1009. Violation replies use it to answer precisely.
    /// </summary>
    public int? ProtocolErrorCode { get; init; }
}
