using System;
using System.Buffers;
using Axiom.ProtoStream.Errors;

namespace Axiom.ProtoStream.Internal;

/// <summary>
/// A growable buffer rented from <see cref="ArrayPool{T}.Shared"/>, reused across messages, bounded by a
/// maximum capacity so an oversized message fails before a large array is rented.
/// </summary>
internal sealed class PooledBufferWriter(int maxCapacity) : IBufferWriter<byte>, IDisposable
{
    private const int InitialCapacity = 256;
    private const int GrowthFactor = 2;

    private byte[] _buffer = [];
    private int _written;

    public int WrittenCount => _written;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <summary>The size of the array held now; what one large message leaves behind until it is trimmed.</summary>
    internal int Capacity => _buffer.Length;

    public void Reset() => _written = 0;

    /// <summary>
    /// Starts over, and gives an array grown above <paramref name="retained"/> bytes back to the pool: one large
    /// message must not pin its size for the rest of a long-lived connection.
    /// </summary>
    public void Reset(int retained)
    {
        _written = 0;
        if (_buffer.Length <= retained)
            return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    public void Advance(int count)
    {
        if ((uint)count > (uint)(_buffer.Length - _written))
            throw new ArgumentOutOfRangeException(nameof(count), "Advanced past the end of the buffer.");
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Dispose()
    {
        if (_buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _written = 0;
    }

    private void Ensure(int sizeHint)
    {
        int needed = _written + Math.Max(sizeHint, 1);
        if (needed <= _buffer.Length)
            return;
        if (needed > maxCapacity)
            throw new ProtocolStateException($"A message of more than {maxCapacity} bytes exceeds the limit of the protocol.");

        int size = (int)Math.Min(Math.Max((long)_buffer.Length * GrowthFactor, Math.Max(needed, InitialCapacity)), Math.Max(maxCapacity, needed));
        byte[] next = ArrayPool<byte>.Shared.Rent(size);
        _buffer.AsSpan(0, _written).CopyTo(next);
        if (_buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }
}
