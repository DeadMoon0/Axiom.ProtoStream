using System;
using System.Buffers;
using ProtoStream.Codecs;
using ProtoStream.Internal;

namespace ProtoStream.Framing;

/// <summary>Combines a framer and a frame codec into a codec for one session.</summary>
internal sealed class FramedCodec<TIn, TOut>(IFramer framer, IFrameCodec<TIn, TOut> frameCodec) : ICodec<TIn, TOut>, IDisposable
{
    private readonly PooledBufferWriter _encoded = new(framer.MaxFrameSize);

    // Framing stays intact when a frame's content is invalid, so reading can always resume after it.
    public bool CanResync => true;

    public ParseResult TryParse(ref MessageParseContext context, out TIn message)
    {
        message = default!;
        switch (framer.TryReadFrame(context.Input, out ReadOnlySequence<byte> frameBytes, out SequencePosition consumed))
        {
            case FrameStatus.NeedMore:
                return context.NeedMore();
            case FrameStatus.TooLarge:
                return context.Invalid(ViolationCode.LimitExceeded, $"A frame exceeds the maximum of {framer.MaxFrameSize} bytes.");
            case FrameStatus.Malformed:
                return context.Invalid(ViolationCode.Malformed, "A frame header is malformed.");
        }

        var frame = new Frame(context.AsContiguous(frameBytes), context.Stamp);
        DecodeResult decoded = frameCodec.Decode(frame, out message);
        return decoded.IsOk
            ? context.Done(consumed)
            : context.Invalid(decoded.Code, decoded.Detail ?? "A frame could not be decoded.", consumed);
    }

    public WriteResult Write(TOut message, IBufferWriter<byte> output)
    {
        _encoded.Reset();
        frameCodec.Encode(message, _encoded);
        framer.WriteFrame(_encoded.WrittenSpan, output);
        return WriteResult.Done;
    }

    public void Dispose() => _encoded.Dispose();
}
