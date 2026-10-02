using System;
using System.Buffers;
using ProtoStream.Codecs;

namespace ProtoStream.Framing;

/// <summary>Result of <see cref="IFramer.TryReadFrame"/>.</summary>
public enum FrameStatus
{
    /// <summary>A whole frame is available.</summary>
    Complete,

    /// <summary>More bytes are needed.</summary>
    NeedMore,

    /// <summary>The frame is, or declares itself, larger than the framer's maximum.</summary>
    TooLarge,

    /// <summary>The frame header cannot be parsed.</summary>
    Malformed,
}

/// <summary>
/// Cuts the byte stream into frames and puts frames back on the wire. Stateless; one instance can serve
/// every session of a protocol.
/// </summary>
/// <remarks>
/// A framer must decide <see cref="FrameStatus.TooLarge"/> as soon as a declared length exceeds
/// <see cref="MaxFrameSize"/>, before waiting for the bytes, so a peer cannot make the session buffer
/// what it merely announced. Every built-in framer in <see cref="Framers"/> does.
/// </remarks>
public interface IFramer
{
    /// <summary>Largest frame the framer accepts or writes, in bytes.</summary>
    int MaxFrameSize { get; }

    /// <summary>Tries to cut one frame from the start of <paramref name="input"/>.</summary>
    FrameStatus TryReadFrame(in ReadOnlySequence<byte> input, out ReadOnlySequence<byte> frame, out SequencePosition consumed);

    /// <summary>Writes <paramref name="encoded"/> as one frame.</summary>
    void WriteFrame(ReadOnlySpan<byte> encoded, IBufferWriter<byte> output);
}

/// <summary>A frame handed to an <see cref="IFrameCodec{TIn, TOut}"/>.</summary>
public readonly struct Frame
{
    internal Frame(ReadOnlyMemory<byte> memory, MessageStamp stamp)
    {
        Memory = memory;
        Stamp = stamp;
    }

    /// <summary>The frame bytes as one block, valid until the next read on the session.</summary>
    public ReadOnlyMemory<byte> Memory { get; }

    /// <summary>Stamp for pooled messages decoded from this frame.</summary>
    public MessageStamp Stamp { get; }
}

/// <summary>Outcome of <see cref="IFrameCodec{TIn, TOut}.Decode"/>.</summary>
public readonly struct DecodeResult
{
    private DecodeResult(bool isOk, ViolationCode code, string? detail)
    {
        IsOk = isOk;
        Code = code;
        Detail = detail;
    }

    /// <summary>The frame decoded into a message.</summary>
    public static DecodeResult Ok => new(true, default, null);

    /// <summary>The frame content violates the protocol. Framing is intact, so reading may resume after it.</summary>
    public static DecodeResult Invalid(ViolationCode code, string detail) => new(false, code, detail);

    /// <summary>True when a message was decoded.</summary>
    public bool IsOk { get; }

    internal ViolationCode Code { get; }

    internal string? Detail { get; }
}

/// <summary>Decodes whole frames into messages and encodes messages into frame content.</summary>
/// <typeparam name="TIn">Base type of the messages read.</typeparam>
/// <typeparam name="TOut">Base type of the messages written.</typeparam>
public interface IFrameCodec<TIn, TOut>
{
    /// <summary>Decodes one frame.</summary>
    DecodeResult Decode(in Frame frame, out TIn message);

    /// <summary>Encodes one message; the framer adds the frame around it.</summary>
    void Encode(TOut message, IBufferWriter<byte> output);
}
