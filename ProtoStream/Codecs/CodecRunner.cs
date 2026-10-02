using System;
using System.Buffers;
using ProtoStream.Errors;
using ProtoStream.Internal;

namespace ProtoStream.Codecs;

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

    /// <summary>Where the message ended, or the bytes the reader took ownership of while needing more.</summary>
    public SequencePosition Consumed { get; init; }

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
/// and for codec tests. Enforces the same reader contract as a session. Payloads are not supported.
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
        return outcome.Kind switch
        {
            ParseOutcomeKind.Message => new DecodeOutcome<TIn> { Status = DecodeStatus.Message, Message = message, Consumed = outcome.Consumed },
            ParseOutcomeKind.NeedMore => new DecodeOutcome<TIn> { Status = DecodeStatus.NeedMore, Consumed = outcome.Consumed },
            _ => new DecodeOutcome<TIn>
            {
                Status = DecodeStatus.Invalid,
                Code = outcome.Code,
                Detail = outcome.Detail,
                CanResume = outcome.HasResumeAt,
                ResumeAt = outcome.ResumeAt,
                Consumed = input.Start,
            },
        };
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
