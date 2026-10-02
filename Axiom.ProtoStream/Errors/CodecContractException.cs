using System;

namespace Axiom.ProtoStream.Errors;

/// <summary>
/// A codec broke its contract with the session, for example by reporting a message that consumed no
/// bytes, or by throwing. This is a bug in the codec, not a peer violation; the connection was aborted.
/// </summary>
public sealed class CodecContractException : ProtoStreamException
{
    /// <summary>Creates the exception with a message stating what the codec did.</summary>
    public CodecContractException(string message) : base(message) { }

    /// <summary>Creates the exception for a codec that threw.</summary>
    public CodecContractException(string message, Exception innerException) : base(message, innerException) { }
}
