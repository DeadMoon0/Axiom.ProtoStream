using System;
using System.Buffers;
using Axiom.ProtoStream.Errors;

namespace Axiom.ProtoStream.WebSockets.Internal;

/// <summary>A pooled byte buffer that grows up to a maximum and is reused for every message of a session.</summary>
internal sealed class GrowableBuffer(int maxLength) : IDisposable
{
    private const int InitialLength = 256;
    private const int GrowthFactor = 2;

    private byte[] _array = [];

    public int Length { get; private set; }

    public ReadOnlyMemory<byte> Memory => _array.AsMemory(0, Length);

    public void Reset() => Length = 0;

    /// <summary>Extends the content by <paramref name="count"/> bytes and returns them for writing.</summary>
    public Span<byte> Append(int count)
    {
        int needed = Length + count;
        if (needed > maxLength)
            throw new ProtocolStateException($"A message of more than {maxLength} bytes exceeds the limit.");
        if (needed > _array.Length)
        {
            byte[] next = ArrayPool<byte>.Shared.Rent(Math.Max(needed, Math.Max(InitialLength, _array.Length * GrowthFactor)));
            _array.AsSpan(0, Length).CopyTo(next);
            if (_array.Length > 0)
                ArrayPool<byte>.Shared.Return(_array);
            _array = next;
        }

        Span<byte> appended = _array.AsSpan(Length, count);
        Length = needed;
        return appended;
    }

    public void Dispose()
    {
        if (_array.Length > 0)
            ArrayPool<byte>.Shared.Return(_array);
        _array = [];
        Length = 0;
    }
}
