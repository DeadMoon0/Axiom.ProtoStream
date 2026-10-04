using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Errors;

namespace Axiom.ProtoStream.Internal;

/// <summary>What a <see cref="PayloadReader"/> needs from its session.</summary>
internal interface IPayloadHost
{
    /// <summary>Arms the session's read timer for one payload read.</summary>
    void ArmPayloadTimeout(TimeSpan due);

    ProtocolLimits Limits { get; }

    TimeProvider Time { get; }

    void DisarmTimeout();

    /// <summary>True when the last cancelled read was cancelled by the session's timeout.</summary>
    bool TimedOut { get; }

    /// <summary>Records a violation, closes the session by its policy and returns the exception to throw.</summary>
    Exception PayloadViolation(ViolationCode code, string detail);

    /// <summary>Writes raw bytes to the peer under the connection's write lock, before the payload is read.</summary>
    ValueTask WritePreambleAsync(ReadOnlyMemory<byte> preamble, CancellationToken cancellationToken);

    /// <summary>Sends output held back by <see cref="FlushPolicy.WhileInputIsBuffered"/> before input is awaited.</summary>
    ValueTask FlushDeferredAsync(CancellationToken cancellationToken);

    /// <summary>True once the connection is being disposed; payload reads must not start any more.</summary>
    bool IsTornDown { get; }
}

/// <summary>
/// The body of a message, read straight from the connection input through an <see cref="IPayloadDecoder"/>.
/// One instance per session, reused for every payload.
/// </summary>
/// <remarks>
/// Data is handed out as slices of the connection buffer, never copied. Positions the caller passes to
/// <see cref="AdvanceTo(SequencePosition, SequencePosition)"/> are positions in that same buffer, so
/// advancing is forwarded unchanged; the decoder only learns how many data bytes were consumed.
/// </remarks>
internal sealed class PayloadReader(IPayloadHost host) : PipeReader
{
    private PipeReader? _inner;
    private IPayloadDecoder? _decoder;
    private ReadOnlySequence<byte> _data;
    private bool _dataPending;
    private bool _ended;
    private bool _completedByUser;
    private bool _emptyPayload;
    private bool _inputAfterEnd;
    private int _outstanding;
    private long _received;
    private TimeSpan _waited;

    public bool IsActive => _decoder is not null && !_ended;

    /// <summary>
    /// True when the payload is over and the next input is already buffered behind it.
    /// <paramref name="inputAfterMessage"/> says whether bytes followed the message itself, which decides for
    /// messages without a payload or with an empty one.
    /// </summary>
    public bool InputFollows(bool inputAfterMessage) =>
        _decoder is null || _emptyPayload ? inputAfterMessage : _ended && _inputAfterEnd;

    public void Bind(PipeReader inner) => _inner = inner;

    /// <summary>True between a read the application started and its AdvanceTo: it may still look at the buffer.</summary>
    public bool HasOutstandingRead => Volatile.Read(ref _outstanding) != 0;

    /// <summary>
    /// Starts the next payload. The application gets a view bound to the message's <paramref name="stamp"/>, so a
    /// body reader kept past the next read throws instead of reading the next message's payload.
    /// </summary>
    public PipeReader Start(IPayloadDecoder decoder, MessageStamp stamp)
    {
        StartCore(decoder);
        return _emptyPayload ? EmptyPayloadReader.Instance : new PayloadView(this, stamp);
    }

    private void StartCore(IPayloadDecoder decoder)
    {
        if (IsActive)
            throw new InvalidOperationException("A payload was started while the previous one was still active.");
        _decoder = decoder;
        _ended = false;
        _dataPending = false;
        _completedByUser = false;
        _inputAfterEnd = false;
        _received = 0;
        _waited = TimeSpan.Zero;
        _emptyPayload = EndsWithoutInput();
    }

    [AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder<>))]
    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        BeginApplicationRead();
        try
        {
            if (_decoder is IPayloadPreamble waiting && waiting.TryTakePreamble(out ReadOnlyMemory<byte> preamble))
                await host.WritePreambleAsync(preamble, cancellationToken).ConfigureAwait(false);
            return await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref _outstanding, 0);
            throw;
        }
    }

    public override bool TryRead(out ReadResult result)
    {
        BeginApplicationRead();
        try
        {
            if (TryReadCore(out result))
                return true;
        }
        catch
        {
            Volatile.Write(ref _outstanding, 0);
            throw;
        }

        Volatile.Write(ref _outstanding, 0);
        return false;
    }

    private bool TryReadCore(out ReadResult result)
    {
        if (_ended || EndsWithoutInput())
        {
            result = Ended();
            return true;
        }

        while (_inner!.TryRead(out ReadResult read))
        {
            if (read.IsCanceled)
            {
                _inner.AdvanceTo(read.Buffer.Start);
                result = new ReadResult(default, isCanceled: true, isCompleted: false);
                return true;
            }

            if (TryStep(read, out result))
                return true;
        }

        result = default;
        return false;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        try
        {
            if (!_dataPending)
                return; // The last result was the end of the payload; the connection was already advanced.

            _dataPending = false;
            _decoder!.OnConsumed(_data.Slice(_data.Start, consumed).Length);
            _inner!.AdvanceTo(consumed, examined);
        }
        finally
        {
            Volatile.Write(ref _outstanding, 0);
        }
    }

    public override void CancelPendingRead() => _inner?.CancelPendingRead();

    public override void Complete(Exception? exception = null)
    {
        _completedByUser = true;
        Volatile.Write(ref _outstanding, 0);
    }

    // Pairs with the session's teardown (both full fences): either the teardown sees this read and keeps the
    // connection's buffers out of the shared pools, or this read sees the teardown and does not start.
    private void BeginApplicationRead()
    {
        if (_decoder is null || _completedByUser)
            throw new InvalidOperationException("The payload can no longer be read: its message is over or reading was completed.");
        Interlocked.Exchange(ref _outstanding, 1);
        if (host.IsTornDown)
        {
            Volatile.Write(ref _outstanding, 0);
            throw new ProtocolStateException("The connection was disposed.");
        }
    }

    /// <summary>Discards whatever the user did not read of the current payload, up to <paramref name="limit"/> bytes.</summary>
    [AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder))]
    public async ValueTask DrainAsync(long limit, CancellationToken cancellationToken)
    {
        if (_decoder is null)
            return;

        if (_dataPending)
            AdvanceTo(_data.Start);

        long drained = 0;
        while (!_ended)
        {
            ReadResult read = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (read.IsCanceled)
                throw new OperationCanceledException(cancellationToken);
            drained += read.Buffer.Length;
            if (drained > limit)
                throw host.PayloadViolation(ViolationCode.LimitExceeded, $"More than {limit} unread payload bytes would have to be discarded.");
            AdvanceTo(read.Buffer.End);
        }

        _decoder = null;
    }

    /// <summary>True while a message's payload is claimed and not yet fully read or drained.</summary>
    public bool HasPayload => _decoder is not null;

    /// <summary>Forgets the payload without draining it, when the session is closing anyway.</summary>
    public void Abandon()
    {
        if (_dataPending)
        {
            _dataPending = false;
            _inner!.AdvanceTo(_data.Start);
        }

        _decoder = null;
    }

    private async ValueTask<ReadResult> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (_ended || EndsWithoutInput())
            return Ended();

        TimeSpan allowed = Allowance();
        if (allowed <= TimeSpan.Zero)
            throw host.PayloadViolation(ViolationCode.Timeout, SlowPayload());

        host.ArmPayloadTimeout(allowed);
        try
        {
            while (true)
            {
                ValueTask<ReadResult> pending = _inner!.ReadAsync(cancellationToken);
                ReadResult read;
                if (pending.IsCompleted)
                {
                    read = await pending.ConfigureAwait(false);
                }
                else
                {
                    await host.FlushDeferredAsync(cancellationToken).ConfigureAwait(false);
                    long started = host.Time.GetTimestamp();
                    read = await pending.ConfigureAwait(false);
                    _waited += host.Time.GetElapsedTime(started);
                }

                if (read.IsCanceled)
                {
                    _inner.AdvanceTo(read.Buffer.Start);
                    if (host.TimedOut)
                        throw host.PayloadViolation(ViolationCode.Timeout, SlowPayload());
                    return new ReadResult(default, isCanceled: true, isCompleted: false);
                }

                if (TryStep(read, out ReadResult result))
                    return result;
            }
        }
        finally
        {
            host.DisarmTimeout();
        }
    }

    /// <returns>True with a result for the caller; false when the connection was advanced and more input is needed.</returns>
    private bool TryStep(in ReadResult read, out ReadResult result)
    {
        ReadOnlySequence<byte> buffer = read.Buffer;
        PayloadStep step = _decoder!.Next(buffer, read.IsCompleted);

        switch (step.Kind)
        {
            case PayloadStepKind.Data when step.Length > 0:
                _received += step.Length;
                _data = buffer.Slice(step.Skip, step.Length);
                _dataPending = true;
                result = new ReadResult(_data, isCanceled: false, isCompleted: false);
                return true;

            case PayloadStepKind.End:
                _inputAfterEnd = buffer.Length > step.Skip;
                _inner!.AdvanceTo(buffer.GetPosition(step.Skip));
                result = Ended();
                return true;

            case PayloadStepKind.Invalid:
                _inner!.AdvanceTo(buffer.Start);
                throw host.PayloadViolation(step.Code, step.Detail ?? "The payload is malformed.");

            default:
                if (read.IsCompleted)
                {
                    _inner!.AdvanceTo(buffer.GetPosition(step.Skip));
                    throw host.PayloadViolation(ViolationCode.Truncated, "The connection closed before the payload was complete.");
                }

                _inner!.AdvanceTo(buffer.GetPosition(step.Skip), buffer.End);
                result = default;
                return false;
        }
    }

    /// <summary>
    /// How long the next read may wait: the idle timeout, or less when the payload has used up the time its
    /// minimum rate allows for the bytes received so far. Only time spent waiting on the peer counts.
    /// </summary>
    private TimeSpan Allowance()
    {
        TimeSpan idle = host.Limits.IdleTimeout;
        if (host.Limits.MinPayloadRate is not { } rate)
            return idle;
        TimeSpan left = rate.AllowedFor(_received) - _waited;
        return idle == Timeout.InfiniteTimeSpan || left < idle ? left : idle;
    }

    private string SlowPayload() => host.Limits.MinPayloadRate is { } rate
        ? $"The payload did not arrive in time, or slower than {rate.BytesPerSecond} bytes per second."
        : "The payload did not arrive in time.";

    // A payload that is already complete (for example a fixed length fully consumed) must end without
    // waiting for connection bytes that belong to the next message, or may never come.
    private bool EndsWithoutInput() => _decoder!.Next(ReadOnlySequence<byte>.Empty, isCompleted: false).Kind == PayloadStepKind.End;

    private ReadResult Ended()
    {
        _ended = true;
        _dataPending = false;
        return new ReadResult(default, isCanceled: false, isCompleted: true);
    }
}

/// <summary>
/// The application's handle on one payload. It turns stale with its message, so a body reader kept past the
/// next read throws <see cref="StaleMessageException"/> instead of reading the payload of the next message.
/// </summary>
internal sealed class PayloadView(PayloadReader payload, MessageStamp stamp) : PipeReader
{
    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        stamp.ThrowIfStale();
        return payload.ReadAsync(cancellationToken);
    }

    public override bool TryRead(out ReadResult result)
    {
        stamp.ThrowIfStale();
        return payload.TryRead(out result);
    }

    public override void AdvanceTo(SequencePosition consumed)
    {
        stamp.ThrowIfStale();
        payload.AdvanceTo(consumed);
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        stamp.ThrowIfStale();
        payload.AdvanceTo(consumed, examined);
    }

    public override void CancelPendingRead()
    {
        if (stamp.IsCurrent)
            payload.CancelPendingRead();
    }

    public override void Complete(Exception? exception = null)
    {
        if (stamp.IsCurrent)
            payload.Complete(exception);
    }
}

/// <summary>An empty payload: holds no connection memory, so one instance serves every message.</summary>
internal sealed class EmptyPayloadReader : PipeReader
{
    public static EmptyPayloadReader Instance { get; } = new();

    private EmptyPayloadReader()
    {
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => new(Ended);

    public override bool TryRead(out ReadResult result)
    {
        result = Ended;
        return true;
    }

    public override void AdvanceTo(SequencePosition consumed)
    {
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
    }

    public override void CancelPendingRead()
    {
    }

    public override void Complete(Exception? exception = null)
    {
    }

    private static ReadResult Ended => new(default, isCanceled: false, isCompleted: true);
}
