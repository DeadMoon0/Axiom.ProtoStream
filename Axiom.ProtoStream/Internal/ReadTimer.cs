using System;
using System.IO.Pipelines;
using System.Threading;

namespace Axiom.ProtoStream.Internal;

/// <summary>
/// One timer per session that cancels the pending read when a message takes too long. Arming and
/// disarming reuse the same timer, so timeouts allocate nothing per message.
/// </summary>
/// <remarks>
/// A timer that fires just after a read completed leaves a cancellation behind for the next read. The
/// session tells such a stale cancellation apart from a real one by <see cref="Fired"/>, which arming resets.
/// </remarks>
internal sealed class ReadTimer : IDisposable
{
    private readonly ITimer _timer;
    private readonly PipeReader _input;
    private volatile bool _fired;

    public ReadTimer(TimeProvider timeProvider, PipeReader input)
    {
        _input = input;
        _timer = timeProvider.CreateTimer(static state => ((ReadTimer)state!).Fire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool Fired => _fired;

    public void Arm(TimeSpan due)
    {
        _fired = false;
        if (due != Timeout.InfiniteTimeSpan)
            _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    public void Disarm() => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    public void Dispose() => _timer.Dispose();

    private void Fire()
    {
        _fired = true;
        _input.CancelPendingRead();
    }
}
