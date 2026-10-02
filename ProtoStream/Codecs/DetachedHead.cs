using System;
using System.Buffers;

namespace ProtoStream.Codecs;

/// <summary>
/// The head of a message, copied out of the connection buffer by <see cref="MessageParseContext.Detach"/>.
/// </summary>
/// <remarks>
/// Claiming a payload (<see cref="MessageParseContext.DoneWithPayload"/>) requires this value: reading the
/// payload releases the connection buffer the head was parsed from, so a message that carries a payload must
/// reference the detached copy, never <see cref="MessageParseContext.Input"/>. The memory is owned by the
/// session and valid until its next read.
/// </remarks>
public readonly struct DetachedHead
{
    internal DetachedHead(ReadOnlyMemory<byte> memory, SequencePosition end, int token)
    {
        Memory = memory;
        End = end;
        Token = token;
    }

    /// <summary>The copied head bytes.</summary>
    public ReadOnlyMemory<byte> Memory { get; }

    internal SequencePosition End { get; }

    internal int Token { get; }
}
