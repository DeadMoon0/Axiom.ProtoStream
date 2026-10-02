using System;
using System.Buffers;

namespace ProtoStream;

/// <summary>
/// How a <see cref="Connection"/> uses memory, time and diagnostics. Share one instance across all
/// connections of a host so they share one memory pool.
/// </summary>
public sealed class ConnectionOptions
{
    /// <summary>The defaults: shared memory pool, 4 KiB reads, system clock, no observer.</summary>
    public static ConnectionOptions Default { get; } = new();

    /// <summary>Pool the connection buffers are rented from. Null uses <see cref="MemoryPool{T}.Shared"/>.</summary>
    public MemoryPool<byte>? Pool { get; init; }

    /// <summary>Smallest buffer a read from a stream asks the pool for. Default 4096.</summary>
    public int MinimumReadSize { get; init; } = 4096;

    /// <summary>Leave the stream(s) open when the connection is disposed. Default false.</summary>
    public bool LeaveOpen { get; init; }

    /// <summary>Clock for timeouts and heartbeats. Replace it in tests to control time.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Receives violations, switches, closes and faults of every session on the connection.</summary>
    public IProtocolObserver? Observer { get; init; }
}

/// <summary>Callbacks about sessions, for logging and diagnostics. Every method has an empty default.</summary>
/// <remarks>
/// Called synchronously on the session's own flow; keep implementations fast and non-throwing. There is
/// deliberately no per-message callback: the steady state stays free of extra calls.
/// </remarks>
public interface IProtocolObserver
{
    /// <summary>A session was opened, by <c>OpenAsync</c> or a switch.</summary>
    void OnOpened(ISession session) { }

    /// <summary>The peer violated the protocol or a limit was hit.</summary>
    void OnViolation(ISession session, Violation violation) { }

    /// <summary>A session switched to another protocol.</summary>
    void OnSwitched(ISession from, ISession to) { }

    /// <summary>A session ended gracefully.</summary>
    void OnClosed(ISession session) { }

    /// <summary>A session failed: a codec or callback threw, or the transport broke.</summary>
    void OnFault(ISession session, Exception exception) { }
}

/// <summary>What every session exposes regardless of its message types.</summary>
public interface ISession
{
    /// <summary>The connection the session runs on.</summary>
    Connection Connection { get; }

    /// <summary>Name of the protocol.</summary>
    string Protocol { get; }

    /// <summary>Name of the current state.</summary>
    string State { get; }

    /// <summary>Lifecycle status.</summary>
    SessionStatus Status { get; }
}

/// <summary>Lifecycle of a session.</summary>
public enum SessionStatus
{
    /// <summary>Reading and writing.</summary>
    Open,

    /// <summary>Ended gracefully: a final state, a close, or the peer finished at a message boundary.</summary>
    Closed,

    /// <summary>Handed over to another protocol by <c>SwitchAsync</c>.</summary>
    Switched,

    /// <summary>Ended by a violation, a codec bug or a transport failure.</summary>
    Faulted,
}

/// <summary>Result of <c>Session.ReadAsync</c>.</summary>
/// <typeparam name="TIn">Base type of the messages read.</typeparam>
public readonly struct ProtocolReadResult<TIn>
    where TIn : class
{
    internal ProtocolReadResult(TIn? message) => Message = message;

    /// <summary>The message, or null when the session is complete.</summary>
    public TIn? Message { get; }

    /// <summary>True when the session ended and no more messages will come.</summary>
    public bool IsCompleted => Message is null;
}

/// <summary>Everything a session needs to hand over to another protocol. Create it with <see cref="ProtocolSwitch"/>.</summary>
public sealed class ProtocolSwitch<TOut, TIn2, TOut2>
    where TOut : class
    where TIn2 : class
    where TOut2 : class
{
    internal ProtocolSwitch(TOut finalMessage, ProtocolDefinition<TIn2, TOut2> target, Func<System.IO.Stream, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<System.IO.Stream>>? transport)
    {
        FinalMessage = finalMessage;
        Target = target;
        Transport = transport;
    }

    /// <summary>The last message of the current protocol, such as <c>101 Switching Protocols</c>.</summary>
    public TOut FinalMessage { get; }

    /// <summary>The protocol the connection continues with.</summary>
    public ProtocolDefinition<TIn2, TOut2> Target { get; }

    internal Func<System.IO.Stream, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<System.IO.Stream>>? Transport { get; }

    /// <summary>
    /// The same switch, and the transport is wrapped after the final message, for example in an
    /// <c>SslStream</c> for STARTTLS. Any byte the peer sent after the switch point is a violation.
    /// </summary>
    public ProtocolSwitch<TOut, TIn2, TOut2> WithTransport(Func<System.IO.Stream, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<System.IO.Stream>> wrap)
    {
        ArgumentNullException.ThrowIfNull(wrap);
        return new ProtocolSwitch<TOut, TIn2, TOut2>(FinalMessage, Target, wrap);
    }
}

/// <summary>Creates <see cref="ProtocolSwitch{TOut, TIn2, TOut2}"/> values with inferred types.</summary>
public static class ProtocolSwitch
{
    /// <summary>Switch to <paramref name="target"/> after writing <paramref name="finalMessage"/>.</summary>
    public static ProtocolSwitch<TOut, TIn2, TOut2> Create<TOut, TIn2, TOut2>(TOut finalMessage, ProtocolDefinition<TIn2, TOut2> target)
        where TOut : class
        where TIn2 : class
        where TOut2 : class
    {
        ArgumentNullException.ThrowIfNull(finalMessage);
        ArgumentNullException.ThrowIfNull(target);
        return new ProtocolSwitch<TOut, TIn2, TOut2>(finalMessage, target, null);
    }
}
