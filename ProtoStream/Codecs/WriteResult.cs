using System;
using System.IO;

namespace ProtoStream.Codecs;

/// <summary>The outcome of <see cref="IMessageWriter{TOut}.Write"/>.</summary>
public readonly struct WriteResult
{
    private WriteResult(Stream? payload, long? expectedLength, IPayloadEncoder? encoder, bool closeAfter, bool handsOver)
    {
        Payload = payload;
        ExpectedLength = expectedLength;
        Encoder = encoder;
        CloseAfter = closeAfter;
        HandsOver = handsOver;
    }

    /// <summary>Everything was written.</summary>
    public static WriteResult Done => default;

    /// <summary>
    /// What was written is followed by a payload the session copies from <paramref name="source"/>
    /// through <paramref name="encoder"/>. When <paramref name="expectedLength"/> is set, a source that
    /// yields more or fewer bytes aborts the connection, because the peer was promised exactly that many.
    /// </summary>
    public static WriteResult WithPayload(Stream source, long? expectedLength, IPayloadEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(encoder);
        if (expectedLength < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedLength), "A payload length cannot be negative.");
        return new WriteResult(source, expectedLength, encoder, closeAfter: false, handsOver: false);
    }

    /// <summary>The same result, and the session closes gracefully once it was written.</summary>
    public WriteResult ThenClose() => new(Payload, ExpectedLength, Encoder, closeAfter: true, HandsOver);

    /// <summary>
    /// The same result, and the message hands the connection over to another protocol (HTTP 101, a 2xx answer to
    /// CONNECT). Such a message may only be written by <c>SwitchAsync</c>; a plain write is refused before any
    /// byte is sent, because the session would otherwise go on parsing the old protocol.
    /// </summary>
    public WriteResult ThenHandOver() => new(Payload, ExpectedLength, Encoder, CloseAfter, handsOver: true);

    internal Stream? Payload { get; }

    internal long? ExpectedLength { get; }

    internal IPayloadEncoder? Encoder { get; }

    internal bool CloseAfter { get; }

    internal bool HandsOver { get; }
}
