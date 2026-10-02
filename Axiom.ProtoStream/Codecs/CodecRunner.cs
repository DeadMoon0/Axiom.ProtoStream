using System;
using System.Buffers;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Internal;

namespace Axiom.ProtoStream.Codecs;

/// <summary>Outcome kinds of <see cref="CodecRunner{TIn, TOut}.Decode"/>.</summary>
public enum DecodeStatus
{
    /// <summary>A message was decoded.</summary>
    Message,

    /// <summary>The input ends inside a message.</summary>
    NeedMore,

    /// <summary>The input violates the protocol.</summary>
    Invalid,
}

/// <summary>Outcome of <see cref="CodecRunner{TIn, TOut}.Decode"/>.</summary>
public readonly struct DecodeOutcome<TIn>
{
    /// <summary>What happened.</summary>
    public DecodeStatus Status { get; init; }

    /// <summary>The message when <see cref="Status"/> is <see cref="DecodeStatus.Message"/>; valid until the next decode.</summary>
    public TIn? Message { get; init; }

    /// <summary>Where the message ended (after its payload), or the bytes the reader took ownership of while needing more.</summary>
    public SequencePosition Consumed { get; init; }

    /// <summary>The decoded payload of the message, when it claimed one; empty otherwise.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>The protocol's own error code for an invalid input, when the reader named one.</summary>
    public int? ProtocolErrorCode { get; init; }

    /// <summary>The violation category when <see cref="Status"/> is <see cref="DecodeStatus.Invalid"/>.</summary>
    public ViolationCode Code { get; init; }

    /// <summary>The violation detail when <see cref="Status"/> is <see cref="DecodeStatus.Invalid"/>.</summary>
    public string? Detail { get; init; }

    /// <summary>True when decoding may resume at <see cref="ResumeAt"/> after an invalid message.</summary>
    public bool CanResume { get; init; }

    /// <summary>Where decoding may resume after an invalid message.</summary>
    public SequencePosition ResumeAt { get; init; }
}

/// <summary>
/// Runs a definition's codec over bytes outside any connection: for parsing captured traffic or files,
/// and for codec tests. Enforces the same reader contract as a session. A payload a message claims is decoded
/// from the same input and returned in <see cref="DecodeOutcome{TIn}.Payload"/>.
/// </summary>
public sealed class CodecRunner<TIn, TOut> : IDisposable
    where TIn : class
    where TOut : class
{
    private readonly ICodec<TIn, TOut> _codec;
    private readonly MessageParser _parser;

    /// <summary>Creates a runner with a fresh codec instance of <paramref name="definition"/>.</summary>
    public CodecRunner(ProtocolDefinition<TIn, TOut> definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _codec = definition.CodecFactory();
        _parser = new MessageParser(definition.Limits.MaxBufferedBytes, payload: null);
    }

    /// <summary>Decodes the next message from <paramref name="input"/>. Messages from earlier calls become stale.</summary>
    public DecodeOutcome<TIn> Decode(in ReadOnlySequence<byte> input, bool isCompleted)
    {
        _parser.Generation.Advance();
        ParseOutcome outcome = _parser.Parse(_codec, input, isCompleted, out TIn message);
        if (outcome.Kind == ParseOutcomeKind.Message && outcome.HasPayload)
            return DecodePayload(input, isCompleted, outcome.Consumed, message);

        return outcome.Kind switch
        {
            ParseOutcomeKind.Message => new DecodeOutcome<TIn> { Status = DecodeStatus.Message, Message = message, Consumed = outcome.Consumed },
            ParseOutcomeKind.NeedMore => new DecodeOutcome<TIn> { Status = DecodeStatus.NeedMore, Consumed = outcome.Consumed },
            _ => new DecodeOutcome<TIn>
            {
                Status = DecodeStatus.Invalid,
                Code = outcome.Code,
                Detail = outcome.Detail,
                ProtocolErrorCode = outcome.ProtocolErrorCode,
                CanResume = outcome.HasResumeAt,
                ResumeAt = outcome.ResumeAt,
                Consumed = input.Start,
            },
        };
    }

    private DecodeOutcome<TIn> DecodePayload(in ReadOnlySequence<byte> input, bool isCompleted, SequencePosition headEnd, TIn message)
    {
        IPayloadDecoder decoder = _parser.LastPayloadDecoder!;
        var payload = new ArrayBufferWriter<byte>();
        ReadOnlySequence<byte> rest = input.Slice(headEnd);
        while (true)
        {
            PayloadStep step = decoder.Next(rest, isCompleted);
            switch (step.Kind)
            {
                case PayloadStepKind.Data when step.Length > 0:
                    foreach (ReadOnlyMemory<byte> segment in rest.Slice(step.Skip, step.Length))
                        payload.Write(segment.Span);
                    decoder.OnConsumed(step.Length);
                    rest = rest.Slice(step.Skip + step.Length);
                    continue;

                case PayloadStepKind.End:
                    return new DecodeOutcome<TIn>
                    {
                        Status = DecodeStatus.Message,
                        Message = message,
                        Consumed = rest.GetPosition(step.Skip),
                        Payload = payload.WrittenMemory,
                    };

                case PayloadStepKind.Invalid:
                    return new DecodeOutcome<TIn> { Status = DecodeStatus.Invalid, Code = step.Code, Detail = step.Detail, Consumed = input.Start };

                default:
                    if (isCompleted)
                        return new DecodeOutcome<TIn> { Status = DecodeStatus.Invalid, Code = ViolationCode.Truncated, Detail = "The input ends inside a payload.", Consumed = input.Start };
                    return new DecodeOutcome<TIn> { Status = DecodeStatus.NeedMore, Consumed = input.Start };
            }
        }
    }

    /// <summary>Encodes <paramref name="message"/> into <paramref name="output"/>.</summary>
    public void Encode(TOut message, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(output);
        if (_codec.Write(message, output).Payload is not null)
            throw new ProtocolStateException("A codec runner cannot stream payloads; use a session.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _parser.Dispose();
        (_codec as IDisposable)?.Dispose();
    }
}
