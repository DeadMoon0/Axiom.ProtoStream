using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.ProtoStream.Internal;

/// <summary>
/// The connection's input over a stream: what <c>PipeReader.Create(stream)</c> does, without its async state per read.
/// A read that waits keeps its state in a box from <see cref="PooledValueTaskMethodBuilder{TResult}"/>, and buffers
/// and segments are reused, so reading allocates nothing in steady state.
/// </summary>
/// <remarks>
/// <para>
/// A read with nothing buffered first waits with a zero-byte read and rents its buffer only once input arrived, so
/// an idle connection holds no buffer. Everything consumed goes back to the pool at once.
/// </para>
/// <para>
/// The reader never owns the stream. <see cref="CancelPendingRead"/> may be called from any thread; everything else
/// follows the <see cref="PipeReader"/> contract (one read at a time, each followed by <c>AdvanceTo</c>).
/// </para>
/// </remarks>
internal sealed class StreamInputReader : PipeReader
{
    /// <summary>A read into the last segment needs at least this much room, or a new segment is appended.</summary>
    private const int MinimumFreeSpace = 1024;

    /// <summary>Segments kept for reuse; more than this are dropped (a connection rarely holds more at once).</summary>
    private const int SegmentCacheSize = 8;

    private readonly Stream _stream;
    private readonly MemoryPool<byte>? _pool;
    private readonly int _bufferSize;
    private readonly int _minimumFreeSpace;
    private readonly object _cancelGate = new();
    private readonly Segment?[] _segmentCache = new Segment?[SegmentCacheSize];
    private int _cachedSegments;

    private CancellationTokenSource? _readCancel;
    private Segment? _head;
    private int _headIndex;
    private Segment? _tail;
    private long _buffered;
    private bool _examinedEverything;
    private bool _streamCompleted;
    private bool _completed;

    /// <param name="stream">The input; left open.</param>
    /// <param name="pool">Pool for the buffers; <see cref="MemoryPool{T}.Shared"/> rents straight from the shared array pool.</param>
    /// <param name="bufferSize">Smallest buffer a read rents.</param>
    public StreamInputReader(Stream stream, MemoryPool<byte> pool, int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        _stream = stream;
        _pool = ReferenceEquals(pool, MemoryPool<byte>.Shared) ? null : pool;
        _bufferSize = _pool is { MaxBufferSize: > 0 } custom ? Math.Min(bufferSize, custom.MaxBufferSize) : bufferSize;
        _minimumFreeSpace = Math.Min(MinimumFreeSpace, _bufferSize);
    }

    public override bool TryRead(out ReadResult result)
    {
        ThrowIfCompleted();
        return TryReadBuffered(out result);
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        if (TryReadBuffered(out ReadResult result))
            return new ValueTask<ReadResult>(result);
        if (_streamCompleted)
            return new ValueTask<ReadResult>(new ReadResult(Buffered(), isCanceled: false, isCompleted: true));
        return ReadStreamAsync(cancellationToken);
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        ThrowIfCompleted();
        if (consumed.GetObject() is not Segment consumedSegment || examined.GetObject() is not Segment examinedSegment)
            return; // positions of an empty buffer: nothing to release
        if (_head is null || _tail is null)
            throw new InvalidOperationException("The position does not belong to the buffer the last read returned.");

        int consumedIndex = consumed.GetInteger();
        _buffered -= Length(_head, _headIndex, consumedSegment, consumedIndex);
        // By offset, not by segment: the end of one segment and the start of an empty one after it are the same place.
        _examinedEverything = examinedSegment.RunningIndex + examined.GetInteger() == _tail.RunningIndex + _tail.End;

        Segment? keepFrom;
        if (_buffered == 0)
        {
            keepFrom = null;
            _tail = null;
            _headIndex = 0;
        }
        else if (consumedIndex == consumedSegment.End)
        {
            keepFrom = consumedSegment.NextSegment;
            _headIndex = 0;
        }
        else
        {
            keepFrom = consumedSegment;
            _headIndex = consumedIndex;
        }

        ReleaseSegments(_head, keepFrom);
        _head = keepFrom;
    }

    public override void CancelPendingRead() => ReadCancel().Cancel();

    public override void Complete(Exception? exception = null)
    {
        if (_completed)
            return;
        _completed = true;
        ReleaseSegments(_head, null);
        _head = _tail = null;
        _headIndex = 0;
        _buffered = 0;
    }

    /// <summary>The buffered input, when there is some the caller has not examined yet, or a cancellation is pending.</summary>
    private bool TryReadBuffered(out ReadResult result)
    {
        CancellationTokenSource source = ReadCancel();
        bool canceled = source.IsCancellationRequested;
        if (canceled || (_buffered > 0 && (!_examinedEverything || _streamCompleted)))
        {
            if (canceled)
                ForgetReadCancel(source);
            result = new ReadResult(Buffered(), canceled, _streamCompleted);
            return true;
        }

        result = default;
        return false;
    }

    [AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder<>))]
    private async ValueTask<ReadResult> ReadStreamAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource source = ReadCancel();
        CancellationTokenRegistration caller = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), source)
            : default;
        bool canceled = false;
        try
        {
            // Wait for input without a buffer: an idle connection holds none.
            if (_buffered == 0)
                await _stream.ReadAsync(Memory<byte>.Empty, source.Token).ConfigureAwait(false);

            Segment tail = WritableTail();
            int read = await _stream.ReadAsync(tail.Writable, source.Token).ConfigureAwait(false);
            tail.Commit(read);
            _buffered += read;
            if (read == 0)
                _streamCompleted = true;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // CancelPendingRead: the read ends with IsCanceled instead of throwing.
            canceled = true;
        }
        finally
        {
            caller.Dispose();
            if (source.IsCancellationRequested)
                ForgetReadCancel(source);
        }

        ReleaseEmptyTail();
        return new ReadResult(Buffered(), canceled, _streamCompleted);
    }

    /// <summary>The last segment, with room for a read: a new one when there is none or the last is nearly full.</summary>
    private Segment WritableTail()
    {
        if (_tail is null)
        {
            _head = _tail = RentSegment();
            _headIndex = 0;
        }
        else if (_tail.Writable.Length < _minimumFreeSpace)
        {
            Segment next = RentSegment();
            _tail.Append(next);
            _tail = next;
        }

        return _tail;
    }

    /// <summary>A read that ended without input (a cancellation, the end of the stream) gives back the buffer it rented.</summary>
    private void ReleaseEmptyTail()
    {
        if (_buffered == 0 && _head is not null)
        {
            ReleaseSegments(_head, null);
            _head = _tail = null;
            _headIndex = 0;
        }
    }

    private ReadOnlySequence<byte> Buffered() =>
        _head is null || _tail is null ? default : new ReadOnlySequence<byte>(_head, _headIndex, _tail, _tail.End);

    private static long Length(Segment from, int fromIndex, Segment to, int toIndex) =>
        to.RunningIndex + toIndex - (from.RunningIndex + fromIndex);

    private Segment RentSegment()
    {
        Segment segment = _cachedSegments > 0 ? TakeCachedSegment() : new Segment();
        if (_pool is null)
            segment.Use(ArrayPool<byte>.Shared.Rent(_bufferSize));
        else
            segment.Use(_pool.Rent(_bufferSize));
        return segment;
    }

    private Segment TakeCachedSegment()
    {
        Segment segment = _segmentCache[--_cachedSegments]!;
        _segmentCache[_cachedSegments] = null;
        return segment;
    }

    /// <summary>Gives back the segments from <paramref name="from"/> up to, not including, <paramref name="until"/>.</summary>
    private void ReleaseSegments(Segment? from, Segment? until)
    {
        while (from is not null && !ReferenceEquals(from, until))
        {
            Segment? next = from.NextSegment;
            from.Release();
            if (_cachedSegments < SegmentCacheSize)
                _segmentCache[_cachedSegments++] = from;
            from = next;
        }
    }

    private CancellationTokenSource ReadCancel()
    {
        lock (_cancelGate)
            return _readCancel ??= new CancellationTokenSource();
    }

    /// <summary>A source that was canceled is used up: the next read gets a fresh one.</summary>
    private void ForgetReadCancel(CancellationTokenSource source)
    {
        lock (_cancelGate)
        {
            if (ReferenceEquals(_readCancel, source))
                _readCancel = null;
        }
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("Reading is not allowed after the reader was completed.");
    }

    /// <summary>One rented buffer in the chain; <see cref="End"/> bytes of it hold input.</summary>
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        private byte[]? _array;
        private IMemoryOwner<byte>? _owner;
        private Memory<byte> _buffer;

        public int End { get; private set; }

        public Segment? NextSegment => (Segment?)Next;

        public Memory<byte> Writable => _buffer[End..];

        public void Use(byte[] array)
        {
            _array = array;
            Reset(array);
        }

        public void Use(IMemoryOwner<byte> owner)
        {
            _owner = owner;
            Reset(owner.Memory);
        }

        public void Commit(int count)
        {
            End += count;
            Memory = _buffer[..End];
        }

        public void Append(Segment next)
        {
            next.RunningIndex = RunningIndex + End;
            Next = next;
        }

        public void Release()
        {
            if (_array is not null)
                ArrayPool<byte>.Shared.Return(_array);
            _owner?.Dispose();
            _array = null;
            _owner = null;
            _buffer = default;
            Memory = default;
            Next = null;
            RunningIndex = 0;
            End = 0;
        }

        private void Reset(Memory<byte> buffer)
        {
            _buffer = buffer;
            Memory = default;
            Next = null;
            RunningIndex = 0;
            End = 0;
        }
    }
}
