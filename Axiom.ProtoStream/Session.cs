using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Definition;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Internal;

namespace Axiom.ProtoStream;

/// <summary>
/// One protocol running on a <see cref="Connection"/>. Read with <c>await foreach</c> over
/// <see cref="Messages"/> from one loop; write from anywhere.
/// </summary>
/// <typeparam name="TIn">Base type of the messages read.</typeparam>
/// <typeparam name="TOut">Base type of the messages written.</typeparam>
/// <remarks>
/// <para>The session never schedules work of its own: the task that awaits a read drives parsing,
/// state changes and automatic replies. The one exception is a described heartbeat.</para>
/// <para>A message returned by a read is valid until the next read on the session. Pooled messages
/// enforce this with <see cref="StaleMessageException"/>.</para>
/// </remarks>
public sealed class Session<TIn, TOut> : IClosableSession, IPayloadHost
    where TIn : class
    where TOut : class
{
    private const int MaxOutboundMessageBytes = 64 * 1024 * 1024;

    /// <summary>How often per heartbeat interval the session checks whether it has been quiet; a heartbeat is at most half an interval late.</summary>
    private const int HeartbeatChecksPerInterval = 2;

    /// <summary>Size of the pooled buffer a streamed payload is copied through.</summary>
    private const int PayloadCopyBufferSize = 16 * 1024;

    /// <summary>Output that <see cref="FlushPolicy.WhileInputIsBuffered"/> holds back at most before it flushes anyway.</summary>
    private const int MaxDeferredFlushBytes = 64 * 1024;
    private static readonly TimeSpan BudgetWindow = TimeSpan.FromSeconds(1);

    // States of _readInFlight. TornDown is final: the connection was disposed and no read may start again.
    private const int ReadIdle = 0;
    private const int ReadRunning = 1;
    private const int ReadTornDown = 2;

    private readonly Connection _connection;
    private readonly ProtocolDefinition<TIn, TOut> _definition;
    private readonly ProtocolLimits _limits;
    private readonly CompiledState<TIn, TOut>[] _states;
    private readonly PipeReader _input;
    private readonly PipeWriter _output;
    private readonly ICodec<TIn, TOut> _codec;
    private readonly IMessageReader<TIn>[] _stateReaders;
    private readonly MessageParser _parser;
    private readonly PayloadReader _payload;
    private readonly ReadTimer _timer;
    private readonly PooledBufferWriter _writeBuffer = new(MaxOutboundMessageBytes);
    private readonly TimeProvider _time;
    private readonly IProtocolObserver? _observer;
    private readonly object _stateLock = new();

    private ITimer? _heartbeat;
    private long _lastWrite;
    private long _budgetWindowStart;
    private int _budgetCount;
    private int _state;
    private int _status;
    private int _readInFlight;
    private int _flushDeferred;
    private int _heartbeatPending;
    private int _tornDown;
    private TaskCompletionSource? _quiesceWaiter;
    private int _released;
    private bool _firstMessageSeen;
    private ReadOnlySequence<byte> _buffer;
    private bool _bufferCompleted;
    private bool _hasBuffer;
    private bool _inputAfterDetached;
    private bool _timerArmed;
    private MessageEnumerable? _messages;

    internal Session(Connection connection, ProtocolDefinition<TIn, TOut> definition)
    {
        _connection = connection;
        _definition = definition;
        _limits = definition.Limits;
        _states = definition.CompiledStates;
        _state = definition.StartState;
        _input = connection.Input;
        _output = connection.Output;
        _time = connection.Options.TimeProvider;
        _observer = connection.Options.Observer;
        _codec = Create(definition.CodecFactory, "codec");
        _stateReaders = new IMessageReader<TIn>[definition.StateReaders.Count];
        for (int i = 0; i < _stateReaders.Length; i++)
            _stateReaders[i] = Create(definition.StateReaders[i], "state reader");
        _payload = new PayloadReader(this);
        _payload.Bind(_input);
        _parser = new MessageParser(_limits.MaxBufferedBytes, _payload);
        _timer = new ReadTimer(_time, _input);
        _lastWrite = _budgetWindowStart = _time.GetTimestamp();
    }

    /// <inheritdoc />
    public Connection Connection => _connection;

    /// <inheritdoc />
    public string Protocol => _definition.Name;

    /// <inheritdoc />
    public string State => _states[Volatile.Read(ref _state)].Name;

    /// <inheritdoc />
    public SessionStatus Status => (SessionStatus)Volatile.Read(ref _status);

    /// <summary>The definition the session runs.</summary>
    public ProtocolDefinition<TIn, TOut> Definition => _definition;

    /// <summary>
    /// The messages the protocol hands to the user, for <c>await foreach</c>. The loop ends when the session
    /// is closed, switched or the peer finished; violations and timeouts are thrown.
    /// </summary>
    public IAsyncEnumerable<TIn> Messages => _messages ??= new MessageEnumerable(this);

    /// <summary>Reads the next message handed to the user. Only one read may be in progress.</summary>
    /// <remarks>
    /// A message that is already buffered and needs no write to dispatch is parsed and returned synchronously;
    /// everything else (waiting for input, automatic replies, violations, payload draining) takes the
    /// asynchronous path.
    /// </remarks>
    public ValueTask<ProtocolReadResult<TIn>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!TryEnterRead())
                return default;
        }
        catch (ProtocolStateException ex)
        {
            return ValueTask.FromException<ProtocolReadResult<TIn>>(ex);
        }

        bool handedOver = false;
        try
        {
            if (Status == SessionStatus.Switched)
                throw new ProtocolSwitchedException(Protocol);
            if (Status != SessionStatus.Open)
                return default;
            if (_payload.HasPayload)
            {
                handedOver = true;
                return ReadSlowAsync(releasePrevious: true, default, cancellationToken);
            }

            _parser.Generation.Advance();
            while (_hasBuffer && !_buffer.IsEmpty)
            {
                ParseOutcome outcome = ParseBuffered(out TIn message);
                DispatchStep step = outcome.Kind == ParseOutcomeKind.Message ? TryDispatch(message) : DispatchStep.Async;
                if (step == DispatchStep.Deliver)
                    return new ValueTask<ProtocolReadResult<TIn>>(new ProtocolReadResult<TIn>(message));
                if (step == DispatchStep.Async)
                {
                    handedOver = true;
                    return ReadSlowAsync(releasePrevious: false, new PendingParse(outcome, message), cancellationToken);
                }

                if (Status != SessionStatus.Open)
                    return default;
            }

            handedOver = true;
            return ReadSlowAsync(releasePrevious: false, default, cancellationToken);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<ProtocolReadResult<TIn>>(ex);
        }
        finally
        {
            if (!handedOver)
                ExitRead();
        }
    }

    /// <summary>
    /// Writes a message the current state allows (see <c>OnSend</c>); anything else throws
    /// <see cref="ProtocolStateException"/> before a byte is sent. Concurrent writes are serialized.
    /// </summary>
    public ValueTask WriteAsync(TOut message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (cancellationToken.IsCancellationRequested || !TryEnterWriteLock())
            return WriteMessageAsync(message, WriteMode.User, cancellationToken);

        // The lock was free: a write that completes synchronously never enters an asynchronous method.
        bool handedOver = false;
        try
        {
            ValueTask written = WriteLockedAsync(message, WriteMode.User, cancellationToken);
            if (written.IsCompletedSuccessfully)
            {
                written.GetAwaiter().GetResult();
                return default;
            }

            handedOver = true;
            return ReleaseWriteLockAfterAsync(written);
        }
        catch (Exception ex)
        {
            return ValueTask.FromException(ex);
        }
        finally
        {
            if (!handedOver)
                _connection.WriteLock.Release();
        }
    }

    /// <summary>
    /// Ends this session without a final message and continues the connection with <paramref name="target"/>,
    /// for prefix protocols such as the PROXY protocol header. Same rules as the other overload.
    /// </summary>
    public ValueTask<Session<TIn2, TOut2>> SwitchAsync<TIn2, TOut2>(ProtocolDefinition<TIn2, TOut2> target, CancellationToken cancellationToken)
        where TIn2 : class
        where TOut2 : class
    {
        ArgumentNullException.ThrowIfNull(target);
        return SwitchAsync(new ProtocolSwitch<TOut, TIn2, TOut2>(null, target, null), cancellationToken);
    }

    /// <summary>Writes <paramref name="finalMessage"/>, then continues the connection with <paramref name="target"/>. Same rules as the other overloads.</summary>
    public ValueTask<Session<TIn2, TOut2>> SwitchAsync<TIn2, TOut2>(TOut finalMessage, ProtocolDefinition<TIn2, TOut2> target, CancellationToken cancellationToken)
        where TIn2 : class
        where TOut2 : class
    {
        ArgumentNullException.ThrowIfNull(finalMessage);
        ArgumentNullException.ThrowIfNull(target);
        return SwitchAsync(new ProtocolSwitch<TOut, TIn2, TOut2>(finalMessage, target, null), cancellationToken);
    }

    /// <summary>
    /// Writes <paramref name="finalMessage"/>, wraps the transport with <paramref name="transport"/> (for example
    /// in an <c>SslStream</c> for STARTTLS), then continues with <paramref name="target"/>. Any byte the peer sent
    /// after the switch point is a violation. Only for connections created from one duplex stream.
    /// </summary>
    public ValueTask<Session<TIn2, TOut2>> SwitchAsync<TIn2, TOut2>(TOut finalMessage, ProtocolDefinition<TIn2, TOut2> target, Func<Stream, CancellationToken, ValueTask<Stream>> transport, CancellationToken cancellationToken)
        where TIn2 : class
        where TOut2 : class
    {
        ArgumentNullException.ThrowIfNull(finalMessage);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(transport);
        return SwitchAsync(new ProtocolSwitch<TOut, TIn2, TOut2>(finalMessage, target, transport), cancellationToken);
    }

    /// <summary>
    /// Writes <see cref="ProtocolSwitch{TOut, TIn2, TOut2}.FinalMessage"/>, ends this session and continues the
    /// connection with the target protocol. Bytes the peer already sent after the switch point go to the new
    /// session. Allowed only in states described as <c>Switchable</c> and while no read is in progress.
    /// </summary>
    public async ValueTask<Session<TIn2, TOut2>> SwitchAsync<TIn2, TOut2>(ProtocolSwitch<TOut, TIn2, TOut2> protocolSwitch, CancellationToken cancellationToken)
        where TIn2 : class
        where TOut2 : class
    {
        ArgumentNullException.ThrowIfNull(protocolSwitch);
        if (!TryEnterRead())
            throw new ProtocolStateException("The connection was disposed.");
        try
        {
            ThrowIfNotWritable();
            if (!_states[Volatile.Read(ref _state)].IsSwitchable)
                throw new ProtocolStateException($"The '{Protocol}' session cannot switch protocols in state '{State}'.");

            await ReleasePreviousMessageAsync(releaseInput: true, cancellationToken).ConfigureAwait(false);
            await AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (protocolSwitch.FinalMessage is { } finalMessage)
                    await WriteLockedAsync(finalMessage, WriteMode.Switch, cancellationToken).ConfigureAwait(false);
                else if (Volatile.Read(ref _flushDeferred) != 0)
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                SetStatus(SessionStatus.Switched);
                ReleaseResources();
            }
            finally
            {
                _connection.WriteLock.Release();
            }
        }
        finally
        {
            ExitRead();
        }

        if (protocolSwitch.Transport is { } wrap)
        {
            if (_input.TryRead(out ReadResult leftover))
            {
                bool hasBytes = !leftover.Buffer.IsEmpty;
                _input.AdvanceTo(leftover.Buffer.Start);
                if (hasBytes)
                    throw ViolationNow(ViolationCode.UnexpectedMessage, "The peer sent bytes after the switch point of a transport switch.", SessionStatus.Switched);
            }

            await _connection.WrapTransportAsync(wrap, cancellationToken).ConfigureAwait(false);
        }

        Session<TIn2, TOut2> next = await _connection.OpenSessionAsync(protocolSwitch.Target, cancellationToken).ConfigureAwait(false);
        Notify(static (o, s, n) => o.OnSwitched(s, (ISession)n!), next);
        return next;
    }

    /// <summary>
    /// Closes the session gracefully: sends the protocol's closing message if it has one and, when that
    /// starts a close handshake and no other read is running, waits for the peer's side, bounded by the close
    /// timeout. The transport is released when the connection is disposed.
    /// </summary>
    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (Status != SessionStatus.Open)
            return;

        using var timeout = new CancellationTokenSource(_limits.CloseTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            await FlushDeferredAsync(linked.Token).ConfigureAwait(false);
            if (_definition.OnCloseMessage is { } closing)
            {
                await WriteMessageAsync(closing(), WriteMode.Closing, linked.Token).ConfigureAwait(false);
                while (Status == SessionStatus.Open && Volatile.Read(ref _readInFlight) == 0)
                {
                    if ((await ReadAsync(linked.Token).ConfigureAwait(false)).IsCompleted)
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The peer did not finish its side in time; the session closes anyway.
        }
        catch (ProtoStreamException)
        {
            // A peer that misbehaves while closing does not stop the close.
        }

        SetStatus(SessionStatus.Closed);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Protocol} #{_connection.Id} [{State}, {Status}]";

    internal async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        Metrics.AddActive(Protocol, 1);
        Notify(static (o, s, _) => o.OnOpened(s), null);

        CompiledState<TIn, TOut> start = _states[_state];
        if (start.OnEnter is { } greeting)
            await WriteMessageAsync(greeting(), WriteMode.Automatic, cancellationToken).ConfigureAwait(false);

        if (_definition.HeartbeatMessage is not null)
        {
            TimeSpan period = _definition.HeartbeatInterval / HeartbeatChecksPerInterval;
            _heartbeat = _time.CreateTimer(static state => ((Session<TIn, TOut>)state!).OnHeartbeat(), this, period, period);
        }
    }

    ValueTask IClosableSession.CloseForDisposeAsync() => CloseAsync(CancellationToken.None);

    void IClosableSession.ReleaseResources() => ReleaseResources();

    /// <summary>
    /// Stops the session for disposal. True once no read, payload read or write runs any more, so its buffers
    /// may go back to the shared pools; false when one still runs after the close timeout.
    /// </summary>
    async ValueTask<bool> IClosableSession.QuiesceAsync()
    {
        SetStatus(SessionStatus.Closed);
        Interlocked.Exchange(ref _tornDown, 1);
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _quiesceWaiter, waiter);
        _output.CancelPendingFlush();
        _input.CancelPendingRead();

        bool readStopped = Interlocked.CompareExchange(ref _readInFlight, ReadTornDown, ReadIdle) == ReadIdle
            || await CompletesWithinAsync(waiter.Task, _limits.CloseTimeout).ConfigureAwait(false);
        bool writeStopped = await _connection.WriteLock.WaitAsync(_limits.CloseTimeout).ConfigureAwait(false);
        if (writeStopped)
            _connection.WriteLock.Release(); // later writers find the session closed before they touch a buffer
        return readStopped && writeStopped && !_payload.HasOutstandingRead;
    }

    private async ValueTask<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout, _time).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // ---- reading -------------------------------------------------------------------------------

    /// <returns>False when the connection was disposed: nothing can be read any more.</returns>
    private bool TryEnterRead() => Interlocked.CompareExchange(ref _readInFlight, ReadRunning, ReadIdle) switch
    {
        ReadIdle => true,
        ReadTornDown => false,
        _ => throw new ProtocolStateException("Another read is already in progress on this session.") { Guidance = "Read from one loop only; writing is allowed from anywhere." },
    };

    // Pairs with QuiesceAsync (both full fences): a read that ends while the connection is being disposed hands
    // the teardown over, so the session's buffers are released only once nothing reads them.
    private void ExitRead()
    {
        Interlocked.Exchange(ref _readInFlight, ReadIdle);
        if (Volatile.Read(ref _quiesceWaiter) is { } waiter && Interlocked.CompareExchange(ref _readInFlight, ReadTornDown, ReadIdle) == ReadIdle)
            waiter.TrySetResult();
    }

    private async ValueTask ReleasePreviousMessageAsync(bool releaseInput, CancellationToken cancellationToken)
    {
        if (releaseInput)
            ReleaseBuffer();
        await _payload.DrainAsync(_limits.MaxPayloadDrain, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands the input back to the pipe before something else reads it (a switch, a payload): everything parsed so
    /// far is consumed, and nothing beyond is marked examined, so the next reader sees the remaining bytes at once.
    /// </summary>
    private void ReleaseBuffer()
    {
        if (_hasBuffer)
        {
            _hasBuffer = false;
            _input.AdvanceTo(_buffer.Start);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ProtocolReadResult<TIn>> ReadSlowAsync(bool releasePrevious, PendingParse pending, CancellationToken cancellationToken)
    {
        try
        {
            if (releasePrevious)
            {
                await ReleasePreviousMessageAsync(releaseInput: false, cancellationToken).ConfigureAwait(false);
                _parser.Generation.Advance();
            }

            return await ReadNextMessageAsync(pending, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitRead();
        }
    }

    private async ValueTask<ProtocolReadResult<TIn>> ReadNextMessageAsync(PendingParse pending, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                ParseOutcome outcome;
                TIn message;
                if (pending.IsSet)
                {
                    // Parsed by the synchronous path, which stopped because dispatching it needs to wait.
                    outcome = pending.Outcome;
                    message = pending.Message!;
                    pending = default;
                }
                else
                {
                    // Messages are parsed from the bytes of the last pipe read until more are needed. Pipelined
                    // messages then cost no pipe operations, and a delivered message stays valid: the pipe is
                    // advanced past it only when the session reads from the pipe again.
                    if (!_hasBuffer || (_buffer.IsEmpty && !_bufferCompleted))
                    {
                        if (_hasBuffer)
                        {
                            _hasBuffer = false;
                            _input.AdvanceTo(_buffer.Start, _buffer.End);
                        }

                        ReadResult read = await ReadInputAsync(cancellationToken).ConfigureAwait(false);
                        if (read.IsCanceled)
                        {
                            _input.AdvanceTo(read.Buffer.Start);
                            if (_timerArmed && _timer.Fired)
                                throw await ViolateAsync(ViolationCode.Timeout, _firstMessageSeen ? "No complete message arrived within the idle timeout." : "No complete first message arrived in time.").ConfigureAwait(false);
                            if (Status != SessionStatus.Open)
                                return default; // the connection is being disposed
                            continue; // a cancellation left behind by a timer that fired after an earlier read completed
                        }

                        _buffer = read.Buffer;
                        _bufferCompleted = read.IsCompleted;
                        _hasBuffer = true;
                    }

                    if (_buffer.IsEmpty && _bufferCompleted)
                    {
                        _hasBuffer = false;
                        _input.AdvanceTo(_buffer.End);
                        await FlushDeferredBeforeEndAsync(cancellationToken).ConfigureAwait(false);
                        SetStatus(SessionStatus.Closed);
                        return default;
                    }

                    outcome = ParseBuffered(out message);
                }

                // Only a message moves the buffer; for the other outcomes it still starts where parsing started.
                ReadOnlySequence<byte> buffer = _buffer;
                switch (outcome.Kind)
                {
                    case ParseOutcomeKind.NeedMore:
                        _hasBuffer = false;
                        _input.AdvanceTo(outcome.Consumed, buffer.End);
                        if (_bufferCompleted)
                            throw await ViolateAsync(ViolationCode.Truncated, "The connection closed in the middle of a message.").ConfigureAwait(false);
                        if (buffer.Slice(outcome.Consumed).Length >= _limits.MaxBufferedBytes)
                            throw await ViolateAsync(ViolationCode.LimitExceeded, $"A message did not complete within {_limits.MaxBufferedBytes} buffered bytes.").ConfigureAwait(false);
                        continue;

                    case ParseOutcomeKind.Invalid:
                        if (outcome.HasResumeAt && _definition.ViolationAction == ViolationAction.Skip)
                        {
                            _buffer = buffer.Slice(outcome.ResumeAt);
                            ReportSkipped(outcome.Code, outcome.Detail ?? "An invalid message was skipped.");
                            continue;
                        }

                        _hasBuffer = false;
                        _input.AdvanceTo(buffer.End);
                        throw await ViolateAsync(outcome.Code, outcome.Detail ?? "The input violates the protocol.", outcome.ProtocolErrorCode).ConfigureAwait(false);

                    default:
                        if (await DispatchAsync(message, cancellationToken).ConfigureAwait(false))
                            return new ProtocolReadResult<TIn>(message);
                        if (Status != SessionStatus.Open)
                            return default;

                        // Handled by the framework; the next message gets its own time window, unless the reader is
                        // still assembling one: then its deadline stands.
                        if (!CurrentReader().HasPartialMessage)
                            DisarmTimer();
                        continue;
                }
            }
        }
        finally
        {
            DisarmTimer();
        }
    }

    /// <summary>
    /// Parses the next message from the bytes of the last pipe read. A parsed message is booked: the buffer moves
    /// past it (or the pipe, when the message was detached into session memory) and it is counted.
    /// </summary>
    private ParseOutcome ParseBuffered(out TIn message)
    {
        ReadOnlySequence<byte> buffer = _buffer;
        ParseOutcome outcome;
        try
        {
            outcome = _parser.Parse(CurrentReader(), buffer, _bufferCompleted, out message);
        }
        catch (CodecContractException ex)
        {
            _hasBuffer = false;
            _input.AdvanceTo(buffer.Start, buffer.End);
            throw Fault(ex);
        }

        if (outcome.Kind == ParseOutcomeKind.Message)
        {
            if (outcome.Detached)
            {
                // The message lives in session memory and may claim a payload that reads the pipe directly.
                _inputAfterDetached = !buffer.Slice(outcome.Consumed).IsEmpty;
                _hasBuffer = false;
                _input.AdvanceTo(outcome.Consumed);
            }
            else
            {
                _buffer = buffer.Slice(outcome.Consumed);
            }

            _firstMessageSeen = true;
            Metrics.Add(Metrics.MessagesReceived, Protocol);
        }

        return outcome;
    }

    // The peer finished sending; answers it may still read go out before the session ends.
    private async ValueTask FlushDeferredBeforeEndAsync(CancellationToken cancellationToken)
    {
        try
        {
            await FlushDeferredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException)
        {
            // The peer is gone in both directions; ending the session is all that is left.
        }
    }

    /// <summary>
    /// Reads from the pipe. The timeout is armed only when the read actually waits, once per message: buffered
    /// messages cost no timer operations, and a peer trickling bytes cannot restart the clock.
    /// </summary>
    private async ValueTask<ReadResult> ReadInputAsync(CancellationToken cancellationToken)
    {
        try
        {
            ValueTask<ReadResult> pending = _input.ReadAsync(cancellationToken);
            if (!pending.IsCompleted)
            {
                // Held-back answers must reach the peer before the session waits for it, or both would wait.
                await FlushDeferredAsync(cancellationToken).ConfigureAwait(false);
                if (!_timerArmed)
                {
                    _timer.Arm(_firstMessageSeen ? _limits.IdleTimeout : _limits.FirstMessageTimeout);
                    _timerArmed = true;
                }
            }

            return await pending.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw Fault(new TransportException("Reading from the connection failed.", ex));
        }
    }

    private void DisarmTimer()
    {
        if (_timerArmed)
        {
            _timerArmed = false;
            _timer.Disarm();
        }
    }

    private enum DispatchStep : byte
    {
        /// <summary>The message goes to the user.</summary>
        Deliver,

        /// <summary>The framework handled it without writing; read on.</summary>
        Handled,

        /// <summary>Dispatching needs a write or a violation: <see cref="DispatchAsync"/> does it.</summary>
        Async,
    }

    /// <summary>Applies a transition that needs no write; anything else is left untouched for <see cref="DispatchAsync"/>.</summary>
    private DispatchStep TryDispatch(TIn message)
    {
        CompiledState<TIn, TOut> state = _states[Volatile.Read(ref _state)];
        int typeId = _definition.InboundTypes.Find(message.GetType());
        if (typeId < 0 || !state.Inbound[typeId].IsDefined)
            return DispatchStep.Async;

        InboundTransition<TIn, TOut> transition = state.Inbound[typeId];
        if (transition.Action == InboundAction.Respond)
            return DispatchStep.Async;
        if (transition.Next >= 0)
        {
            CompiledState<TIn, TOut> target = _states[transition.Next];
            if (target.OnEnter is not null)
                return DispatchStep.Async;
            lock (_stateLock)
                _state = transition.Next;
            if (target.IsFinal)
                SetStatus(SessionStatus.Closed);
        }

        return transition.Action == InboundAction.Delegate ? DispatchStep.Deliver : DispatchStep.Handled;
    }

    /// <returns>True when the message goes to the user.</returns>
    private async ValueTask<bool> DispatchAsync(TIn message, CancellationToken cancellationToken)
    {
        CompiledState<TIn, TOut> state = _states[Volatile.Read(ref _state)];
        int typeId = _definition.InboundTypes.Find(message.GetType());
        if (typeId < 0 || !state.Inbound[typeId].IsDefined)
        {
            string detail = $"{message.GetType().Name} is not accepted in state '{state.Name}'.";
            if (_definition.ViolationAction == ViolationAction.Skip)
            {
                ReportSkipped(ViolationCode.UnexpectedMessage, detail);
                return false;
            }

            throw await ViolateAsync(ViolationCode.UnexpectedMessage, detail).ConfigureAwait(false);
        }

        InboundTransition<TIn, TOut> transition = state.Inbound[typeId];
        switch (transition.Action)
        {
            case InboundAction.Delegate:
                if (transition.Next >= 0)
                    await EnterStateAsync(transition.Next, cancellationToken).ConfigureAwait(false);
                return true;

            case InboundAction.Respond:
                TOut reply;
                try
                {
                    reply = transition.Responder!(message);
                }
                catch (Exception ex)
                {
                    throw Fault(ex);
                }

                if (!TakeBudget())
                    throw await ViolateAsync(ViolationCode.RateExceeded, "The peer triggered more automatic replies than the budget allows.").ConfigureAwait(false);
                await WriteMessageAsync(reply, WriteMode.Automatic, cancellationToken).ConfigureAwait(false);
                break;

            default:
                break;
        }

        if (transition.Next >= 0)
            await EnterStateAsync(transition.Next, cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async ValueTask EnterStateAsync(int next, CancellationToken cancellationToken)
    {
        CompiledState<TIn, TOut> target = _states[next];
        lock (_stateLock)
            _state = next;

        if (target.IsFinal)
        {
            SetStatus(SessionStatus.Closed);
            return;
        }

        if (target.OnEnter is { } enter)
        {
            if (!TakeBudget())
                throw await ViolateAsync(ViolationCode.RateExceeded, "The peer triggered more automatic messages than the budget allows.").ConfigureAwait(false);
            await WriteMessageAsync(enter(), WriteMode.Automatic, cancellationToken).ConfigureAwait(false);
        }
    }

    private IMessageReader<TIn> CurrentReader()
    {
        int index = _states[Volatile.Read(ref _state)].ReaderIndex;
        return index < 0 ? _codec : _stateReaders[index];
    }

    private bool TakeBudget()
    {
        if (!_limits.HasAutoRespondBudget)
            return true;

        long now = _time.GetTimestamp();
        if (_time.GetElapsedTime(_budgetWindowStart, now) >= BudgetWindow)
        {
            _budgetWindowStart = now;
            _budgetCount = 0;
        }

        return ++_budgetCount <= _limits.AutoRespondBudget;
    }

    // ---- writing -------------------------------------------------------------------------------

    private enum WriteMode : byte
    {
        /// <summary>Must be allowed by the state; applies its transition.</summary>
        User,

        /// <summary>Framework message: no check, no transition.</summary>
        Automatic,

        /// <summary>Closing message: applies a transition when the state describes one.</summary>
        Closing,

        /// <summary>The final message of a switch: no check, no transition, may hand the connection over.</summary>
        Switch,
    }

    private async ValueTask WriteMessageAsync(TOut message, WriteMode mode, CancellationToken cancellationToken)
    {
        await AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteLockedAsync(message, mode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connection.WriteLock.Release();
        }
    }

    private bool TryEnterWriteLock()
    {
        try
        {
            return _connection.WriteLock.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            return false; // the asynchronous path reports it
        }
    }

    private async ValueTask ReleaseWriteLockAfterAsync(ValueTask written)
    {
        try
        {
            await written.ConfigureAwait(false);
        }
        finally
        {
            _connection.WriteLock.Release();
        }
    }

    private async ValueTask AcquireWriteLockAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connection.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new ProtocolStateException("The connection was disposed.", ex);
        }
    }

    private async ValueTask WriteLockedAsync(TOut message, WriteMode mode, CancellationToken cancellationToken)
    {
        ThrowIfNotWritable();

        OutboundTransition<TOut> transition = default;
        if (mode is WriteMode.User or WriteMode.Closing)
        {
            int typeId = _definition.OutboundTypes.Find(message.GetType());
            CompiledState<TIn, TOut> state = _states[Volatile.Read(ref _state)];
            if (typeId >= 0 && state.Outbound[typeId].IsDefined)
                transition = state.Outbound[typeId];
            else if (mode == WriteMode.User)
                throw new ProtocolStateException($"{message.GetType().Name} may not be sent in state '{state.Name}' of '{Protocol}'.");
        }

        WriteResult result = Encode(message);
        if (result.HandsOver && mode != WriteMode.Switch)
            throw new ProtocolStateException($"{message.GetType().Name} hands the connection over to another protocol.") { Guidance = "Send it through SwitchAsync." };
        _output.Write(_writeBuffer.WrittenSpan);
        if (result.Payload is not null)
            await StreamPayloadAsync(result, cancellationToken).ConfigureAwait(false);
        if (!TryDeferFlush(mode, result))
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _lastWrite, _time.GetTimestamp());
        Metrics.Add(Metrics.MessagesSent, Protocol);

        if (transition.IsDefined)
        {
            int next = transition.Resolve(message);
            if (next >= 0)
                await EnterStateLockedAsync(next, cancellationToken).ConfigureAwait(false);
        }

        if (result.CloseAfter)
            SetStatus(SessionStatus.Closed);
    }

    private async ValueTask EnterStateLockedAsync(int next, CancellationToken cancellationToken)
    {
        CompiledState<TIn, TOut> target = _states[next];
        lock (_stateLock)
            _state = next;

        if (target.IsFinal)
            SetStatus(SessionStatus.Closed);
        else if (target.OnEnter is { } enter)
            await WriteLockedAsync(enter(), WriteMode.Automatic, cancellationToken).ConfigureAwait(false);
    }

    // Every message is encoded completely into the session buffer before a byte reaches the connection, so a
    // codec or a message that fails half-way can never leave half a message on the wire.
    private WriteResult Encode(TOut message)
    {
        _writeBuffer.Reset();
        WriteResult result;
        try
        {
            result = _codec.Write(message, _writeBuffer);
        }
        catch (ProtoStreamException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CodecContractException($"The codec {_codec.GetType().Name} threw while writing {message.GetType().Name}.", ex);
        }

        return result;
    }

    private async ValueTask StreamPayloadAsync(WriteResult result, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PayloadCopyBufferSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await result.Payload!.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > result.ExpectedLength)
                    throw new ProtocolStateException($"The payload source yielded more than the {result.ExpectedLength} bytes announced.");
                result.Encoder!.WriteData(buffer.AsSpan(0, read), _output);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (result.ExpectedLength is { } expected && total != expected)
                throw new ProtocolStateException($"The payload source yielded {total} of the {expected} bytes announced.");
            result.Encoder!.WriteEnd(_output);
        }
        catch (Exception ex) when (ex is not TransportException)
        {
            // Part of the payload is on the wire; the peer can no longer find the next message.
            throw Fault(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Under <see cref="FlushPolicy.WhileInputIsBuffered"/>, leaves a user write in the output buffer while the
    /// next input is already buffered and no read is running; the reader flushes before it waits.
    /// </summary>
    private bool TryDeferFlush(WriteMode mode, in WriteResult result)
    {
        if (_definition.FlushPolicy != FlushPolicy.WhileInputIsBuffered || mode != WriteMode.User || result.CloseAfter
            || Volatile.Read(ref _readInFlight) != 0 || !NextInputIsBuffered()
            || !_output.CanGetUnflushedBytes || _output.UnflushedBytes > MaxDeferredFlushBytes)
            return false;

        // Pairs with EnterRead (both full fences): either this write sees a read that started meanwhile and
        // flushes itself, or that read sees the flag and flushes before it waits.
        Interlocked.Exchange(ref _flushDeferred, 1);
        return Volatile.Read(ref _readInFlight) == 0;
    }

    // Bytes of the next message are already here: in the session's buffer, or behind a detached message and its payload.
    private bool NextInputIsBuffered() => _hasBuffer ? !_buffer.IsEmpty : _payload.InputFollows(_inputAfterDetached);

    private ValueTask FlushDeferredAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _flushDeferred) == 0 ? default : FlushDeferredLockedAsync(cancellationToken);

    private async ValueTask FlushDeferredLockedAsync(CancellationToken cancellationToken)
    {
        await AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _flushDeferred) != 0)
                await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connection.WriteLock.Release();
        }
    }

    /// <summary>Flushes everything written so far, held-back output included. Called under the write lock.</summary>
    private ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _flushDeferred, 0);
        long flushing = _output.CanGetUnflushedBytes ? _output.UnflushedBytes : 0;
        ValueTask<FlushResult> pending;
        try
        {
            pending = _output.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return ValueTask.FromException(Fault(new TransportException("Writing to the connection failed.", ex)));
        }

        if (!pending.IsCompletedSuccessfully)
            return AwaitFlushAsync(pending, flushing);
        return PeerStoppedReading(pending.Result) is { } failure ? ValueTask.FromException(failure) : default;
    }

    /// <summary>A flush that waits on the peer, bounded by the minimum write rate for the bytes it carries.</summary>
    private async ValueTask AwaitFlushAsync(ValueTask<FlushResult> pending, long flushing)
    {
        // Allocated only when a flush actually waits; a reader that keeps up never gets here.
        using CancellationTokenSource? slow = _limits.MinWriteRate is { } rate
            ? new CancellationTokenSource(rate.AllowedFor(flushing), _time)
            : null;
        using CancellationTokenRegistration cutOff = slow?.Token.UnsafeRegister(static output => ((PipeWriter)output!).CancelPendingFlush(), _output) ?? default;

        FlushResult flushed;
        try
        {
            flushed = await pending.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw Fault(new TransportException("Writing to the connection failed.", ex));
        }

        if (flushed.IsCanceled && slow is { IsCancellationRequested: true })
            throw Fault(new TransportException($"The peer took output slower than {_limits.MinWriteRate!.BytesPerSecond} bytes per second.", new IOException("The minimum write rate was not met.")));
        if (PeerStoppedReading(flushed) is { } failure)
            throw failure;
    }

    private Exception? PeerStoppedReading(FlushResult flushed)
    {
        if (flushed.IsCompleted)
            return Fault(new TransportException("The peer stopped reading.", new IOException("The reading side of the connection completed.")));

        // Only the connection's disposal cancels a flush: the message may not have left.
        if (flushed.IsCanceled)
            return Fault(new TransportException("The connection was disposed while writing.", new IOException("The flush was cancelled.")));
        return null;
    }

    private void ThrowIfNotWritable()
    {
        switch (Status)
        {
            case SessionStatus.Open:
                return;
            case SessionStatus.Switched:
                throw new ProtocolSwitchedException(Protocol);
            default:
                throw new ProtocolStateException($"The '{Protocol}' session is {Status.ToString().ToLowerInvariant()} and cannot write.");
        }
    }

    private void OnHeartbeat()
    {
        if (Status != SessionStatus.Open || _time.GetElapsedTime(Volatile.Read(ref _lastWrite)) < _definition.HeartbeatInterval)
            return;

        // One heartbeat at a time: behind a peer that stopped reading, every tick would otherwise add a task
        // waiting for the write lock.
        if (Interlocked.Exchange(ref _heartbeatPending, 1) == 0)
            _ = SendHeartbeatAsync();
    }

    private async Task SendHeartbeatAsync()
    {
        try
        {
            await WriteMessageAsync(_definition.HeartbeatMessage!(), WriteMode.Automatic, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed heartbeat means a broken transport; the reading loop sees the fault next.
            if (Status == SessionStatus.Open)
                Fault(ex);
        }
        finally
        {
            Volatile.Write(ref _heartbeatPending, 0);
        }
    }

    // ---- lifecycle -----------------------------------------------------------------------------

    private bool SetStatus(SessionStatus status)
    {
        if (Interlocked.CompareExchange(ref _status, (int)status, (int)SessionStatus.Open) != (int)SessionStatus.Open)
            return false;

        Metrics.AddActive(Protocol, -1);
        _heartbeat?.Dispose();
        if (status == SessionStatus.Closed)
            Notify(static (o, s, _) => o.OnClosed(s), null);
        return true;
    }

    private Exception Fault(Exception exception)
    {
        if (SetStatus(SessionStatus.Faulted))
            Notify(static (o, s, e) => o.OnFault(s, ((ExceptionHolder)e!).Exception), new ExceptionHolder(exception));
        return exception;
    }

    private Violation CreateViolation(ViolationCode code, string detail, int? protocolErrorCode = null)
    {
        var violation = new Violation { Code = code, Detail = detail, Protocol = Protocol, State = State, ProtocolErrorCode = protocolErrorCode };
        Metrics.AddViolation(Protocol, code);
        Notify(static (o, s, v) => o.OnViolation(s, ((ViolationHolder)v!).Violation), new ViolationHolder(violation));
        return violation;
    }

    private Exception ViolationNow(ViolationCode code, string detail, SessionStatus status)
    {
        Violation violation = CreateViolation(code, detail);
        SetStatus(status);
        return new ProtocolViolationException(violation);
    }

    private async ValueTask<Exception> ViolateAsync(ViolationCode code, string detail, int? protocolErrorCode = null)
    {
        Violation violation = CreateViolation(code, detail, protocolErrorCode);
        if (_definition.ViolationReply is { } reply)
        {
            try
            {
                if (reply(violation) is { } message)
                {
                    using var timeout = new CancellationTokenSource(_limits.CloseTimeout, _time);
                    await WriteMessageAsync(message, WriteMode.Automatic, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // The reply is a courtesy; the session closes either way.
            }
        }

        SetStatus(SessionStatus.Faulted);
        return new ProtocolViolationException(violation);
    }

    private void ReportSkipped(ViolationCode code, string detail) => CreateViolation(code, detail);

    private void ReleaseResources()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;

        // Every message still referenced by the application turns stale before its memory goes back to a pool
        // that other connections rent from.
        _parser.Generation.Advance();
        _heartbeat?.Dispose();
        _timer.Dispose();
        _parser.Dispose();
        _writeBuffer.Dispose();
        (_codec as IDisposable)?.Dispose();
        foreach (IMessageReader<TIn> reader in _stateReaders)
            (reader as IDisposable)?.Dispose();
    }

    private void Notify(Action<IProtocolObserver, ISession, object?> callback, object? argument)
    {
        if (_observer is null)
            return;
        try
        {
            callback(_observer, this, argument);
        }
        catch (Exception)
        {
            // An observer must not break the session it observes.
        }
    }

    private static T Create<T>(Func<T> factory, string what) where T : class
    {
        try
        {
            return factory() ?? throw new InvalidOperationException($"The {what} factory returned null.");
        }
        catch (Exception ex) when (ex is not ProtoStreamException)
        {
            throw new CodecContractException($"The {what} factory failed.", ex);
        }
    }

    // ---- payload host --------------------------------------------------------------------------

    void IPayloadHost.ArmPayloadTimeout(TimeSpan due) => _timer.Arm(due);

    ProtocolLimits IPayloadHost.Limits => _limits;

    TimeProvider IPayloadHost.Time => _time;

    void IPayloadHost.DisarmTimeout() => _timer.Disarm();

    bool IPayloadHost.TimedOut => _timer.Fired;

    Exception IPayloadHost.PayloadViolation(ViolationCode code, string detail) => ViolationNow(code, detail, SessionStatus.Faulted);

    async ValueTask IPayloadHost.WritePreambleAsync(ReadOnlyMemory<byte> preamble, CancellationToken cancellationToken)
    {
        await AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotWritable();
            _output.Write(preamble.Span);
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connection.WriteLock.Release();
        }
    }

    ValueTask IPayloadHost.FlushDeferredAsync(CancellationToken cancellationToken) => FlushDeferredAsync(cancellationToken);

    bool IPayloadHost.IsTornDown => Volatile.Read(ref _tornDown) != 0;

    /// <summary>A message the synchronous read path parsed but could not dispatch without waiting.</summary>
    private readonly struct PendingParse
    {
        public PendingParse(ParseOutcome outcome, TIn message)
        {
            Outcome = outcome;
            Message = message;
            IsSet = true;
        }

        public ParseOutcome Outcome { get; }

        public TIn? Message { get; }

        public bool IsSet { get; }
    }

    private sealed class ExceptionHolder(Exception exception)
    {
        public Exception Exception { get; } = exception;
    }

    private sealed class ViolationHolder(Violation violation)
    {
        public Violation Violation { get; } = violation;
    }

    // ---- enumeration ---------------------------------------------------------------------------

    private sealed class MessageEnumerable(Session<TIn, TOut> session) : IAsyncEnumerable<TIn>
    {
        public IAsyncEnumerator<TIn> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(session, cancellationToken);
    }

    private sealed class Enumerator(Session<TIn, TOut> session, CancellationToken cancellationToken) : IAsyncEnumerator<TIn>
    {
        public TIn Current { get; private set; } = null!;

        public ValueTask<bool> MoveNextAsync()
        {
            // A switch made inside the loop body ends the loop instead of failing it.
            if (session.Status == SessionStatus.Switched)
                return new ValueTask<bool>(false);

            ValueTask<ProtocolReadResult<TIn>> read = session.ReadAsync(cancellationToken);
            return read.IsCompletedSuccessfully ? new ValueTask<bool>(Accept(read.Result)) : AwaitAsync(read);
        }

        public ValueTask DisposeAsync() => default;

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<bool> AwaitAsync(ValueTask<ProtocolReadResult<TIn>> read) =>
            Accept(await read.ConfigureAwait(false));

        private bool Accept(ProtocolReadResult<TIn> result)
        {
            Current = result.Message!;
            return !result.IsCompleted;
        }
    }
}
