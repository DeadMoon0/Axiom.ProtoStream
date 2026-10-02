using System;
using System.Buffers;
using System.IO.Pipelines;
using ProtoStream.Errors;

namespace ProtoStream.Codecs;

/// <summary>
/// What a reader sees during one <see cref="IMessageReader{TIn}.TryParse"/> call, and the only way to
/// report its outcome.
/// </summary>
/// <remarks>
/// <para>Exactly one outcome method must be called and its result returned. The session checks the
/// positions (a message must consume at least one byte, positions must lie in <see cref="Input"/>) and
/// aborts the connection with a <see cref="CodecContractException"/> otherwise.</para>
/// <para>There is deliberately no way to report "need more" while claiming to have examined less than the
/// whole input: that combination makes the next read return the same bytes forever.</para>
/// </remarks>
public ref struct MessageParseContext
{
    private readonly ParseHost _host;
    private readonly ReadOnlySequence<byte> _input;
    private readonly bool _isCompleted;
    private bool _scratchUsed;

    internal ParseResultKind Outcome;
    internal SequencePosition Consumed;
    internal SequencePosition ResumeAt;
    internal bool HasResumeAt;
    internal ViolationCode Code;
    internal string? Detail;
    internal int? ProtocolErrorCode;

    internal MessageParseContext(ParseHost host, ReadOnlySequence<byte> input, bool isCompleted)
    {
        _host = host;
        _input = input;
        _isCompleted = isCompleted;
        _scratchUsed = false;
        Outcome = ParseResultKind.None;
        Consumed = default;
        ResumeAt = default;
        HasResumeAt = false;
        Code = default;
        Detail = null;
        ProtocolErrorCode = null;
    }

    /// <summary>The unconsumed bytes received so far.</summary>
    public readonly ReadOnlySequence<byte> Input => _input;

    /// <summary>True when the peer has finished sending: no more bytes will arrive after <see cref="Input"/>.</summary>
    public readonly bool IsCompleted => _isCompleted;

    /// <summary>Stamp for pooled messages produced by this parse. See <see cref="MessageStamp"/>.</summary>
    public readonly MessageStamp Stamp => _host.Stamp;

    /// <summary>
    /// A message ends at <paramref name="consumed"/>. The message may reference <see cref="Input"/>
    /// memory; it stays valid until the next read on the session.
    /// </summary>
    public ParseResult Done(SequencePosition consumed)
    {
        SetOutcome(ParseResultKind.Done);
        Consumed = consumed;
        return new ParseResult(ParseResultKind.Done);
    }

    /// <summary>A message ends with the detached <paramref name="head"/>; it references only the detached copy.</summary>
    public ParseResult Done(DetachedHead head)
    {
        VerifyHead(head);
        SetOutcome(ParseResultKind.DoneDetached);
        Consumed = head.End;
        return new ParseResult(ParseResultKind.DoneDetached);
    }

    /// <summary>
    /// A message head ends with the detached <paramref name="head"/> and is followed by a payload that
    /// <paramref name="decoder"/> delimits. <paramref name="body"/> reads the decoded payload straight
    /// from the connection. A payload the user does not read is drained by the session before the next
    /// message, up to the protocol's drain limit.
    /// </summary>
    public ParseResult DoneWithPayload(DetachedHead head, IPayloadDecoder decoder, out PipeReader body)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        VerifyHead(head);
        SetOutcome(ParseResultKind.DoneWithPayload);
        Consumed = head.End;
        body = _host.StartPayload(decoder);
        return new ParseResult(ParseResultKind.DoneWithPayload);
    }

    /// <summary>More bytes are needed; nothing was consumed.</summary>
    public ParseResult NeedMore()
    {
        SetOutcome(ParseResultKind.NeedMore);
        Consumed = _input.Start;
        return new ParseResult(ParseResultKind.NeedMore);
    }

    /// <summary>
    /// More bytes are needed, and the reader took ownership of the bytes before <paramref name="consumed"/>
    /// (it copied them, for example a completed fragment of a larger message).
    /// </summary>
    public ParseResult NeedMore(SequencePosition consumed)
    {
        SetOutcome(ParseResultKind.NeedMore);
        Consumed = consumed;
        return new ParseResult(ParseResultKind.NeedMore);
    }

    /// <summary>The input violates the protocol and the reader cannot continue.</summary>
    public ParseResult Invalid(ViolationCode code, string detail)
    {
        SetOutcome(ParseResultKind.Invalid);
        Code = code;
        Detail = detail;
        return new ParseResult(ParseResultKind.Invalid);
    }

    /// <summary>
    /// The input violates the protocol and the reader cannot continue; <paramref name="protocolErrorCode"/> is the
    /// protocol's own code for it (an HTTP status, a WebSocket close code), available to violation replies.
    /// </summary>
    public ParseResult Invalid(ViolationCode code, int protocolErrorCode, string detail)
    {
        SetOutcome(ParseResultKind.Invalid);
        Code = code;
        Detail = detail;
        ProtocolErrorCode = protocolErrorCode;
        return new ParseResult(ParseResultKind.Invalid);
    }

    /// <summary>
    /// The input violates the protocol, but reading can resume at <paramref name="resumeAt"/>, after the
    /// bad message. Whether it does is the violation policy's decision.
    /// </summary>
    public ParseResult Invalid(ViolationCode code, string detail, SequencePosition resumeAt)
    {
        SetOutcome(ParseResultKind.Invalid);
        Code = code;
        Detail = detail;
        ResumeAt = resumeAt;
        HasResumeAt = true;
        return new ParseResult(ParseResultKind.Invalid);
    }

    /// <summary>
    /// Copies the bytes from the start of <see cref="Input"/> up to <paramref name="end"/> into memory the
    /// session owns. Required before claiming a payload. One copy per parse.
    /// </summary>
    public DetachedHead Detach(SequencePosition end)
    {
        ReadOnlySequence<byte> head = _input.Slice(_input.Start, end);
        if (head.IsEmpty)
            throw new CodecContractException("Detach was called for an empty head.");

        UseScratch();
        return new DetachedHead(_host.CopyToScratch(head), end, _host.ParseToken);
    }

    /// <summary>
    /// Returns <paramref name="slice"/> as one contiguous block: the connection memory itself when it already
    /// is one, otherwise a copy the session owns. Either is valid until the next read. One copy per parse.
    /// </summary>
    public ReadOnlyMemory<byte> AsContiguous(in ReadOnlySequence<byte> slice)
    {
        if (slice.IsSingleSegment)
            return slice.First;

        UseScratch();
        return _host.CopyToScratch(slice);
    }

    private void UseScratch()
    {
        if (_scratchUsed)
            throw new CodecContractException("A reader may copy into the session buffer only once per parse.");
        _scratchUsed = true;
    }

    private readonly void VerifyHead(DetachedHead head)
    {
        if (head.Token == 0 || head.Token != _host.ParseToken)
            throw new CodecContractException("The detached head does not come from this parse.");
    }

    private void SetOutcome(ParseResultKind kind)
    {
        if (Outcome != ParseResultKind.None)
            throw new CodecContractException("A reader reported two outcomes for one parse.");
        Outcome = kind;
    }
}

/// <summary>The session side of a <see cref="MessageParseContext"/>.</summary>
internal abstract class ParseHost
{
    public abstract MessageStamp Stamp { get; }

    public abstract int ParseToken { get; }

    public abstract ReadOnlyMemory<byte> CopyToScratch(in ReadOnlySequence<byte> bytes);

    public abstract PipeReader StartPayload(IPayloadDecoder decoder);
}
