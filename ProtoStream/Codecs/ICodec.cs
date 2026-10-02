using System.Buffers;

namespace ProtoStream.Codecs;

/// <summary>
/// Turns bytes from the connection into messages. Implementations frame themselves; for protocols
/// with a uniform frame format, prefer an <see cref="Framing.IFramer"/> plus an
/// <see cref="Framing.IFrameCodec{TIn, TOut}"/>.
/// </summary>
/// <typeparam name="TIn">Base type of the messages read.</typeparam>
/// <remarks>
/// A reader instance belongs to one session (it is created by the factory given to the describer),
/// so it may keep state between calls, for example the fragments of a message that is still arriving.
/// </remarks>
public interface IMessageReader<TIn>
{
    /// <summary>
    /// Tries to parse one message from <see cref="MessageParseContext.Input"/>. The result must come
    /// from one of the context's outcome methods (<c>Done</c>, <c>NeedMore</c>, <c>Invalid</c>, ...).
    /// </summary>
    ParseResult TryParse(ref MessageParseContext context, out TIn message);

    /// <summary>
    /// True when the reader reports a resume position with <c>Invalid</c>, so a <c>Skip</c> violation
    /// policy can discard a bad message and keep reading. Used by the definition linter.
    /// </summary>
    bool CanResync => false;
}

/// <summary>Turns messages into bytes for the connection.</summary>
/// <typeparam name="TOut">Base type of the messages written.</typeparam>
public interface IMessageWriter<TOut>
{
    /// <summary>
    /// Writes <paramref name="message"/> to <paramref name="output"/>. Return <see cref="WriteResult.Done"/>,
    /// or a result carrying a payload the session streams after what was written.
    /// </summary>
    WriteResult Write(TOut message, IBufferWriter<byte> output);
}

/// <summary>A reader and writer for one protocol role. One instance serves one session.</summary>
/// <typeparam name="TIn">Base type of the messages read.</typeparam>
/// <typeparam name="TOut">Base type of the messages written.</typeparam>
/// <remarks>
/// Because one instance sees both directions, a codec can use what it wrote to parse what it reads,
/// for example the method of an outstanding HTTP request when framing its response.
/// </remarks>
public interface ICodec<TIn, TOut> : IMessageReader<TIn>, IMessageWriter<TOut>
{
}
