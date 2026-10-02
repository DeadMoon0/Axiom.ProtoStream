namespace ProtoStream.Errors;

/// <summary>
/// The peer broke the protocol, or a limit or timeout protecting the connection was hit.
/// The connection has already been closed according to the violation policy of the protocol.
/// </summary>
public sealed class ProtocolViolationException : ProtoStreamException
{
    /// <summary>Creates the exception for the given violation.</summary>
    public ProtocolViolationException(Violation violation)
        : base($"Protocol '{violation.Protocol}' violated in state '{violation.State}': {violation.Code} - {violation.Detail}")
    {
        Violation = violation;
    }

    /// <summary>What went wrong.</summary>
    public Violation Violation { get; }
}
