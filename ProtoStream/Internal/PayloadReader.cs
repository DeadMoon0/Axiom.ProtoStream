using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ProtoStream.Codecs;

namespace ProtoStream.Internal;

/// <summary>What a <see cref="PayloadReader"/> needs from its session.</summary>
internal interface IPayloadHost
{
    void ArmPayloadTimeout();

    void DisarmTimeout();

    /// <summary>True when the last cancelled read was cancelled by the session's timeout.</summary>
    bool TimedOut { get; }

    /// <summary>Records a violation, closes the session by its policy and returns the exception to throw.</summary>
    Exception PayloadViolation(ViolationCode code, string detail);
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

    public bool IsActive => _decoder is not null && !_ended;

    public void Bind(PipeReader inner) => _inner = inner;

    public PipeReader Start(IPayloadDecoder decoder)
    {
        if (IsActive)
            throw new InvalidOperationException("A payload was started while the previous one was still active.");
        _decoder = decoder;
        _ended = false;
        _dataPending = false;
        _completedByUser = false;
        return this;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_decoder is null || _completedByUser)
            throw new InvalidOperationException("The payload can no longer be read: its message is over or reading was completed.");
        return await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public override bool TryRead(out ReadResult result)
    {
        if (_decoder is null || _completedByUser)
            throw new InvalidOperationException("The payload can no longer be read: its message is over or reading was completed.");

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
        if (!_dataPending)
            return; // The last result was the end of the payload; the connection was already advanced.

        _dataPending = false;
        _decoder!.OnConsumed(_data.Slice(_data.Start, consumed).Length);
        _inner!.AdvanceTo(consumed, examined);
    }

    public override void CancelPendingRead() => _inner?.CancelPendingRead();

    public override void Complete(Exception? exception = null) => _completedByUser = true;

    /// <summary>Discards whatever the user did not read of the current payload, up to <paramref name="limit"/> bytes.</summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
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

        host.ArmPayloadTimeout();
        try
        {
            while (true)
            {
                ReadResult read = await _inner!.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (read.IsCanceled)
                {
                    _inner.AdvanceTo(read.Buffer.Start);
                    if (host.TimedOut)
                        throw host.PayloadViolation(ViolationCode.Timeout, "The payload did not arrive in time.");
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
                _data = buffer.Slice(step.Skip, step.Length);
                _dataPending = true;
                result = new ReadResult(_data, isCanceled: false, isCompleted: false);
                return true;

            case PayloadStepKind.End:
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
