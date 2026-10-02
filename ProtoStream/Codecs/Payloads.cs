using System;
using System.Buffers;

namespace ProtoStream.Codecs;

/// <summary>
/// Delimits a payload that follows a message head, reading the raw connection bytes. Stateful; one
/// instance decodes one payload at a time and may be reused after its payload ended.
/// </summary>
/// <remarks>
/// The session calls <see cref="Next"/> with the raw bytes following everything already processed.
/// The <c>skip</c> of every returned step is always consumed by the caller before the next call, so a
/// decoder may advance its own state past framing bytes (such as chunk headers) as it reports them.
/// </remarks>
public interface IPayloadDecoder
{
    /// <summary>Inspects <paramref name="input"/> and says where the next payload data is.</summary>
    PayloadStep Next(in ReadOnlySequence<byte> input, bool isCompleted);

    /// <summary>Reports that <paramref name="dataBytes"/> bytes of the last reported data were consumed.</summary>
    void OnConsumed(long dataBytes);
}

/// <summary>
/// Implemented by a payload decoder whose peer waits for permission before sending the payload, such as an HTTP
/// client that sent <c>Expect: 100-continue</c>. The session sends the preamble the first time the application
/// reads the payload, and never when it does not.
/// </summary>
public interface IPayloadPreamble
{
    /// <summary>Returns the bytes to send once; later calls return false.</summary>
    bool TryTakePreamble(out ReadOnlyMemory<byte> preamble);
}

/// <summary>One step of payload decoding. See <see cref="IPayloadDecoder"/>.</summary>
public readonly struct PayloadStep
{
    private PayloadStep(PayloadStepKind kind, long skip, long length, ViolationCode code, string? detail)
    {
        Kind = kind;
        Skip = skip;
        Length = length;
        Code = code;
        Detail = detail;
    }

    /// <summary>After <paramref name="skip"/> framing bytes, <paramref name="length"/> data bytes are available.</summary>
    public static PayloadStep Data(long skip, long length) => new(PayloadStepKind.Data, skip, length, default, null);

    /// <summary>After <paramref name="skip"/> framing bytes, more input is needed.</summary>
    public static PayloadStep NeedMore(long skip) => new(PayloadStepKind.NeedMore, skip, 0, default, null);

    /// <summary>After <paramref name="skip"/> framing bytes, the payload has ended.</summary>
    public static PayloadStep End(long skip) => new(PayloadStepKind.End, skip, 0, default, null);

    /// <summary>The payload framing violates the protocol.</summary>
    public static PayloadStep Invalid(ViolationCode code, string detail) => new(PayloadStepKind.Invalid, 0, 0, code, detail);

    internal PayloadStepKind Kind { get; }

    internal long Skip { get; }

    internal long Length { get; }

    internal ViolationCode Code { get; }

    internal string? Detail { get; }
}

internal enum PayloadStepKind : byte
{
    NeedMore = 0,
    Data,
    End,
    Invalid,
}

/// <summary>A payload of a length known in advance, such as an HTTP body with <c>Content-Length</c>.</summary>
public sealed class FixedLengthPayloadDecoder : IPayloadDecoder
{
    private long _remaining;

    /// <summary>Prepares the decoder for a payload of <paramref name="length"/> bytes.</summary>
    public void Reset(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _remaining = length;
    }

    /// <inheritdoc />
    public PayloadStep Next(in ReadOnlySequence<byte> input, bool isCompleted)
    {
        if (_remaining == 0)
            return PayloadStep.End(0);
        if (input.IsEmpty)
            return isCompleted
                ? PayloadStep.Invalid(ViolationCode.Truncated, "The connection closed before the payload was complete.")
                : PayloadStep.NeedMore(0);
        return PayloadStep.Data(0, Math.Min(_remaining, input.Length));
    }

    /// <inheritdoc />
    public void OnConsumed(long dataBytes) => _remaining -= dataBytes;
}

/// <summary>A payload that lasts until the peer closes its sending side, bounded by a maximum.</summary>
public sealed class UntilClosePayloadDecoder : IPayloadDecoder
{
    private long _remainingAllowed;

    /// <summary>Prepares the decoder for a payload of at most <paramref name="maxLength"/> bytes.</summary>
    public void Reset(long maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        _remainingAllowed = maxLength;
    }

    /// <inheritdoc />
    public PayloadStep Next(in ReadOnlySequence<byte> input, bool isCompleted)
    {
        if (input.IsEmpty)
            return isCompleted ? PayloadStep.End(0) : PayloadStep.NeedMore(0);
        if (input.Length > _remainingAllowed)
            return PayloadStep.Invalid(ViolationCode.LimitExceeded, "The payload exceeded its maximum length.");
        return PayloadStep.Data(0, input.Length);
    }

    /// <inheritdoc />
    public void OnConsumed(long dataBytes) => _remainingAllowed -= dataBytes;
}

/// <summary>Frames payload data on the way out, such as HTTP chunked encoding.</summary>
public interface IPayloadEncoder
{
    /// <summary>Writes one block of payload data.</summary>
    void WriteData(ReadOnlySpan<byte> data, IBufferWriter<byte> output);

    /// <summary>Writes whatever marks the end of the payload.</summary>
    void WriteEnd(IBufferWriter<byte> output);
}

/// <summary>Writes payload data unframed; the length was announced by the message head.</summary>
public sealed class IdentityPayloadEncoder : IPayloadEncoder
{
    private IdentityPayloadEncoder() { }

    /// <summary>The shared instance; the encoder is stateless.</summary>
    public static IdentityPayloadEncoder Instance { get; } = new();

    /// <inheritdoc />
    public void WriteData(ReadOnlySpan<byte> data, IBufferWriter<byte> output) => output.Write(data);

    /// <inheritdoc />
    public void WriteEnd(IBufferWriter<byte> output) { }
}
