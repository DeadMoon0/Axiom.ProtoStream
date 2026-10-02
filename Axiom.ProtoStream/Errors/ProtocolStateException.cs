using System;

namespace Axiom.ProtoStream.Errors;

/// <summary>
/// The calling code used a session in a way its protocol does not allow in the current state, for
/// example writing a message the state does not declare with <c>OnSend</c>. Nothing was sent.
/// </summary>
public class ProtocolStateException : ProtoStreamException
{
    /// <summary>Creates the exception with a message stating what is true.</summary>
    public ProtocolStateException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public ProtocolStateException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The session was switched to another protocol; use the session <c>SwitchAsync</c> returned.
/// </summary>
public sealed class ProtocolSwitchedException : ProtocolStateException
{
    /// <summary>Creates the exception naming the protocol the session switched away from.</summary>
    public ProtocolSwitchedException(string protocolName)
        : base($"The '{protocolName}' session was switched to another protocol and can no longer be used.")
    {
        Guidance = "Continue on the session returned by SwitchAsync.";
    }
}

/// <summary>
/// A pooled message was used after the session read the next message, which reused its memory.
/// </summary>
/// <remarks>
/// Pooled messages make the steady state allocation-free. The price is a lifetime: a message is valid
/// until the next read on its session. This exception turns a use after that point from silent data
/// corruption into a loud error.
/// </remarks>
public sealed class StaleMessageException : ProtocolStateException
{
    /// <summary>Creates the exception.</summary>
    public StaleMessageException()
        : base("The message was used after the session read the next message, which reused its memory.")
    {
        Guidance = "Finish with a message before reading the next one, or call Retain() to keep an owned copy.";
    }
}
