using ProtoStream.Errors;

namespace ProtoStream.Codecs;

/// <summary>
/// Remembers which read of a session produced a pooled message, so the message can refuse to be used
/// after the next read reused its memory.
/// </summary>
/// <remarks>
/// Codecs that hand out reused message objects store the stamp from
/// <see cref="MessageParseContext.Stamp"/> and call <see cref="ThrowIfStale"/> in every accessor.
/// The default value belongs to no session and is always current: use it for owned messages.
/// </remarks>
public readonly struct MessageStamp
{
    private readonly ReadGeneration? _source;
    private readonly int _generation;

    internal MessageStamp(ReadGeneration source)
    {
        _source = source;
        _generation = source.Current;
    }

    /// <summary>True while the message is still valid.</summary>
    public bool IsCurrent => _source is null || _source.Current == _generation;

    /// <summary>Throws <see cref="StaleMessageException"/> when the session has read past the message.</summary>
    public void ThrowIfStale()
    {
        if (_source is not null && _source.Current != _generation)
            throw new StaleMessageException();
    }
}

/// <summary>Counts the reads of one session; bumped at the start of every read.</summary>
internal sealed class ReadGeneration
{
    public int Current;

    public void Advance() => Current++;
}
