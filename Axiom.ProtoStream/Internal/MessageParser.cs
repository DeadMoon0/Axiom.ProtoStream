using System;
using System.Buffers;
using System.IO.Pipelines;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Errors;

namespace Axiom.ProtoStream.Internal;

internal enum ParseOutcomeKind : byte
{
    Message,
    NeedMore,
    Invalid,
}

/// <summary>What one parse produced, with every position already checked against the input.</summary>
internal readonly struct ParseOutcome
{
    public ParseOutcomeKind Kind { get; init; }

    /// <summary>End of the message, or the bytes a reader took ownership of while needing more.</summary>
    public SequencePosition Consumed { get; init; }

    /// <summary>The message references only session memory; the pipe can be advanced at once.</summary>
    public bool Detached { get; init; }

    public bool HasPayload { get; init; }

    public ViolationCode Code { get; init; }

    public string? Detail { get; init; }

    public int? ProtocolErrorCode { get; init; }

    public bool HasResumeAt { get; init; }

    public SequencePosition ResumeAt { get; init; }
}

/// <summary>
/// Runs readers against input and enforces the reader contract. Owns the scratch memory detached heads
/// and contiguous copies live in, and the read generation pooled messages are stamped with.
/// </summary>
internal sealed class MessageParser(int maxBufferedBytes, PayloadReader? payload) : ParseHost, IDisposable
{
    private readonly PooledBufferWriter _scratch = new(maxBufferedBytes);
    private int _parseToken;

    public ReadGeneration Generation { get; } = new();

    public override MessageStamp Stamp => new(Generation);

    public override int ParseToken => _parseToken;

    public override ReadOnlyMemory<byte> CopyToScratch(in ReadOnlySequence<byte> bytes)
    {
        _scratch.Reset();
        int length = (int)bytes.Length;
        bytes.CopyTo(_scratch.GetSpan(length));
        _scratch.Advance(length);
        return _scratch.WrittenMemory;
    }

    /// <summary>The decoder of the payload the last parse claimed, for runners outside a session.</summary>
    public IPayloadDecoder? LastPayloadDecoder { get; private set; }

    public override PipeReader StartPayload(IPayloadDecoder decoder)
    {
        LastPayloadDecoder = decoder;
        return payload is null ? DetachedPayload.Instance : payload.Start(decoder);
    }

    public ParseOutcome Parse<TIn>(IMessageReader<TIn> reader, in ReadOnlySequence<byte> input, bool isCompleted, out TIn message)
    {
        if (++_parseToken == 0)
            _parseToken = 1;

        var context = new MessageParseContext(this, input, isCompleted);
        ParseResult result;
        try
        {
            result = reader.TryParse(ref context, out message);
        }
        catch (ProtoStreamException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CodecContractException($"The reader {reader.GetType().Name} threw while parsing.", ex);
        }

        if (result.Kind == ParseResultKind.None)
            throw new CodecContractException($"The reader {reader.GetType().Name} returned a result no outcome method produced.");
        if (result.Kind != context.Outcome)
            throw new CodecContractException($"The reader {reader.GetType().Name} returned a different outcome than it reported.");

        switch (result.Kind)
        {
            case ParseResultKind.Done:
                if (LengthTo(input, context.Consumed, reader) == 0)
                    throw new CodecContractException($"The reader {reader.GetType().Name} reported a message that consumed no bytes.");
                return new ParseOutcome { Kind = ParseOutcomeKind.Message, Consumed = context.Consumed };

            case ParseResultKind.DoneDetached:
            case ParseResultKind.DoneWithPayload:
                return new ParseOutcome
                {
                    Kind = ParseOutcomeKind.Message,
                    Consumed = context.Consumed,
                    Detached = true,
                    HasPayload = result.Kind == ParseResultKind.DoneWithPayload,
                };

            case ParseResultKind.NeedMore:
                LengthTo(input, context.Consumed, reader);
                message = default!;
                return new ParseOutcome { Kind = ParseOutcomeKind.NeedMore, Consumed = context.Consumed };

            default:
                if (context.HasResumeAt && LengthTo(input, context.ResumeAt, reader) == 0)
                    throw new CodecContractException($"The reader {reader.GetType().Name} reported a resume position that skips nothing.");
                message = default!;
                return new ParseOutcome
                {
                    Kind = ParseOutcomeKind.Invalid,
                    Code = context.Code,
                    Detail = context.Detail,
                    ProtocolErrorCode = context.ProtocolErrorCode,
                    HasResumeAt = context.HasResumeAt,
                    ResumeAt = context.ResumeAt,
                };
        }
    }

    public void Dispose() => _scratch.Dispose();

    /// <summary>The body of a message decoded outside a session; its bytes are in the runner's outcome instead.</summary>
    private sealed class DetachedPayload : PipeReader
    {
        public static readonly DetachedPayload Instance = new();

        public override void AdvanceTo(SequencePosition consumed) => throw NotInSession();

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => throw NotInSession();

        public override void CancelPendingRead() { }

        public override void Complete(Exception? exception = null) { }

        public override System.Threading.Tasks.ValueTask<ReadResult> ReadAsync(System.Threading.CancellationToken cancellationToken = default) => throw NotInSession();

        public override bool TryRead(out ReadResult result) => throw NotInSession();

        private static InvalidOperationException NotInSession() =>
            new("This payload was decoded outside a session; read DecodeOutcome.Payload instead.");
    }

    private static long LengthTo<TIn>(in ReadOnlySequence<byte> input, SequencePosition position, IMessageReader<TIn> reader)
    {
        try
        {
            return input.Slice(input.Start, position).Length;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            throw new CodecContractException($"The reader {reader.GetType().Name} reported a position outside its input.", ex);
        }
    }
}
