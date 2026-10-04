using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.ProtoStream.Http;

/// <summary>
/// Content already in memory, read as a payload: the session copies it to the connection slice by slice. Read-only
/// over memory it does not own, so a frozen response shared by many sessions gets one of these per write.
/// </summary>
internal sealed class MemoryPayloadSource(ReadOnlyMemory<byte> content) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => content.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count = Math.Min(buffer.Length, content.Length - _position);
        content.Span.Slice(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested ? ValueTask.FromCanceled<int>(cancellationToken) : new(Read(buffer.Span));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
