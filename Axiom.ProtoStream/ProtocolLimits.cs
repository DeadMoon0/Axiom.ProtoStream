using System;
using System.Threading;

namespace Axiom.ProtoStream;

/// <summary>
/// The limits and timeouts protecting a session. Every one has a safe default; turning one off takes a
/// named opt-out on <see cref="LimitsBuilder"/>, which the definition records as a warning.
/// </summary>
public sealed class ProtocolLimits
{
    /// <summary>The defaults.</summary>
    public static ProtocolLimits Default { get; } = new();

    /// <summary>Time allowed for the first message of a session to arrive completely. Default 30 seconds.</summary>
    public TimeSpan FirstMessageTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time allowed for each later message to arrive completely, counted from when the session starts waiting. Default 2 minutes.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Time a graceful close may take, including waiting for the peer's side of a close handshake. Default 5 seconds.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Most bytes a session buffers while one message is incomplete. Default 1 MiB.</summary>
    public int MaxBufferedBytes { get; init; } = 1024 * 1024;

    /// <summary>Most bytes of an unread payload the session discards to reach the next message. Default 1 MiB.</summary>
    public long MaxPayloadDrain { get; init; } = 1024 * 1024;

    /// <summary>Most messages per second the framework sends on its own (replies, greetings). Default 1000.</summary>
    public int AutoRespondBudget { get; init; } = 1000;

    internal bool HasFirstMessageTimeout => FirstMessageTimeout != Timeout.InfiniteTimeSpan;

    internal bool HasIdleTimeout => IdleTimeout != Timeout.InfiniteTimeSpan;

    internal bool HasAutoRespondBudget => AutoRespondBudget > 0;
}

/// <summary>Describes the limits of a protocol. See <see cref="ProtocolLimits"/> for the defaults.</summary>
public sealed class LimitsBuilder
{
    private ProtocolLimits _limits = ProtocolLimits.Default;

    internal ProtocolLimits Limits => _limits;

    internal System.Collections.Generic.List<string> Errors { get; } = [];

    internal System.Collections.Generic.List<string> OptOuts { get; } = [];

    /// <summary>Time allowed for the first message to arrive.</summary>
    public LimitsBuilder FirstMessageTimeout(TimeSpan timeout)
    {
        RequirePositive(timeout, nameof(FirstMessageTimeout), "NoFirstMessageTimeout");
        _limits = With(firstMessageTimeout: timeout);
        return this;
    }

    /// <summary>Time allowed for each later message to arrive.</summary>
    public LimitsBuilder IdleTimeout(TimeSpan timeout)
    {
        RequirePositive(timeout, nameof(IdleTimeout), "NoIdleTimeout");
        _limits = With(idleTimeout: timeout);
        return this;
    }

    /// <summary>Time a graceful close may take.</summary>
    public LimitsBuilder CloseTimeout(TimeSpan timeout)
    {
        RequirePositive(timeout, nameof(CloseTimeout), null);
        _limits = With(closeTimeout: timeout);
        return this;
    }

    /// <summary>Most bytes buffered while one message is incomplete.</summary>
    public LimitsBuilder MaxBufferedBytes(int bytes)
    {
        if (bytes < 1)
            Errors.Add($"MaxBufferedBytes must be at least 1, not {bytes}.");
        _limits = With(maxBufferedBytes: bytes);
        return this;
    }

    /// <summary>Most bytes of an unread payload discarded to reach the next message.</summary>
    public LimitsBuilder MaxPayloadDrain(long bytes)
    {
        if (bytes < 0)
            Errors.Add($"MaxPayloadDrain cannot be negative, not {bytes}.");
        _limits = With(maxPayloadDrain: bytes);
        return this;
    }

    /// <summary>Most messages per second the framework sends on its own.</summary>
    public LimitsBuilder AutoRespondBudget(int messagesPerSecond)
    {
        if (messagesPerSecond < 1)
            Errors.Add($"AutoRespondBudget must be at least 1, not {messagesPerSecond}; use NoAutoRespondBudget() to turn it off.");
        _limits = With(autoRespondBudget: messagesPerSecond);
        return this;
    }

    /// <summary>Opt-out: the first message may take forever. Recorded as a definition warning.</summary>
    public LimitsBuilder NoFirstMessageTimeout()
    {
        OptOuts.Add("The first-message timeout is off: a peer that connects and sends nothing holds the session forever.");
        _limits = With(firstMessageTimeout: Timeout.InfiniteTimeSpan);
        return this;
    }

    /// <summary>Opt-out: later messages may take forever. Recorded as a definition warning.</summary>
    public LimitsBuilder NoIdleTimeout()
    {
        OptOuts.Add("The idle timeout is off: a silent peer holds the session forever.");
        _limits = With(idleTimeout: Timeout.InfiniteTimeSpan);
        return this;
    }

    /// <summary>Opt-out: the framework may send replies without limit. Recorded as a definition warning.</summary>
    public LimitsBuilder NoAutoRespondBudget()
    {
        OptOuts.Add("The auto-respond budget is off: a peer can make the framework send replies without limit.");
        _limits = With(autoRespondBudget: 0);
        return this;
    }

    private void RequirePositive(TimeSpan value, string name, string? optOut)
    {
        if (value <= TimeSpan.Zero)
            Errors.Add(optOut is null
                ? $"{name} must be positive, not {value}."
                : $"{name} must be positive, not {value}; use {optOut}() to turn it off.");
    }

    private ProtocolLimits With(
        TimeSpan? firstMessageTimeout = null, TimeSpan? idleTimeout = null, TimeSpan? closeTimeout = null,
        int? maxBufferedBytes = null, long? maxPayloadDrain = null, int? autoRespondBudget = null) => new()
    {
        FirstMessageTimeout = firstMessageTimeout ?? _limits.FirstMessageTimeout,
        IdleTimeout = idleTimeout ?? _limits.IdleTimeout,
        CloseTimeout = closeTimeout ?? _limits.CloseTimeout,
        MaxBufferedBytes = maxBufferedBytes ?? _limits.MaxBufferedBytes,
        MaxPayloadDrain = maxPayloadDrain ?? _limits.MaxPayloadDrain,
        AutoRespondBudget = autoRespondBudget ?? _limits.AutoRespondBudget,
    };
}
