using System;

namespace Axiom.ProtoStream.Errors;

/// <summary>
/// The transport under the session failed or was closed while a message was being read or written.
/// The inner exception is the stream's own error.
/// </summary>
public sealed class TransportException : ProtoStreamException
{
    /// <summary>Creates the exception.</summary>
    public TransportException(string message, Exception innerException) : base(message, innerException) { }
}
