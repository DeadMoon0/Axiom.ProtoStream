using System;

namespace ProtoStream.Errors;

/// <summary>
/// Base of every exception ProtoStream throws, apart from argument guards.
/// </summary>
/// <remarks>
/// One exception family means one <c>catch</c> covers everything the framework can raise, and the
/// guidance text survives instead of being lost in a BCL exception. Derived types say <em>who</em> is at
/// fault: the protocol author (<see cref="ProtocolDefinitionException"/>, <see cref="CodecContractException"/>),
/// the peer (<see cref="ProtocolViolationException"/>) or the calling code (<see cref="ProtocolStateException"/>).
/// </remarks>
public class ProtoStreamException : Exception
{
    /// <summary>Creates the exception with a message stating what is true.</summary>
    public ProtoStreamException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public ProtoStreamException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>What to do about it, phrased for the developer reading the log. Null when there is nothing to add.</summary>
    public string? Guidance { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        Guidance is null ? base.ToString() : $"{base.ToString()}{Environment.NewLine}Guidance: {Guidance}";
}
