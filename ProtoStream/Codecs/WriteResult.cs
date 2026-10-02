using System;
using System.IO;

namespace ProtoStream.Codecs;

/// <summary>The outcome of <see cref="IMessageWriter{TOut}.Write"/>.</summary>
public readonly struct WriteResult
{
    private WriteResult(Stream? payload, long? expectedLength, IPayloadEncoder? encoder, bool closeAfter)
    {
        Payload = payload;
        ExpectedLength = expectedLength;
        Encoder = encoder;
        CloseAfter = closeAfter;
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
        return new WriteResult(source, expectedLength, encoder, closeAfter: false);
    }

    /// <summary>The same result, and the session closes gracefully once it was written.</summary>
    public WriteResult ThenClose() => new(Payload, ExpectedLength, Encoder, closeAfter: true);

    internal Stream? Payload { get; }

    internal long? ExpectedLength { get; }

    internal IPayloadEncoder? Encoder { get; }

    internal bool CloseAfter { get; }
}
