namespace Axiom.ProtoStream.Codecs;

/// <summary>
/// The outcome of <see cref="IMessageReader{TIn}.TryParse"/>. Only <see cref="MessageParseContext"/>
/// can create a meaningful value, so a reader cannot report an outcome without stating its positions.
/// </summary>
public readonly struct ParseResult
{
    internal ParseResult(ParseResultKind kind) => Kind = kind;

    internal ParseResultKind Kind { get; }
}

internal enum ParseResultKind : byte
{
    None = 0,
    Done,
    DoneDetached,
    DoneWithPayload,
    NeedMore,
    Invalid,
}
