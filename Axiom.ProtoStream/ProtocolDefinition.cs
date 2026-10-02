using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Framing;

namespace Axiom.ProtoStream;

/// <summary>Entry point for describing protocols.</summary>
public static class Protocol
{
    /// <summary>Starts describing one role of a protocol.</summary>
    /// <typeparam name="TIn">Base type of the messages this role reads.</typeparam>
    /// <typeparam name="TOut">Base type of the messages this role writes.</typeparam>
    /// <param name="name">Name used in diagnostics, errors and metrics.</param>
    public static IDescribeStage<TIn, TOut> Describe<TIn, TOut>(string name)
        where TIn : class
        where TOut : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Definition.ProtocolBuilder<TIn, TOut>(name);
    }
}

/// <summary>
/// A compiled, validated, immutable protocol description for one role. Build it once (it is thread-safe)
/// and open any number of sessions from it.
/// </summary>
/// <typeparam name="TIn">Base type of the messages read.</typeparam>
/// <typeparam name="TOut">Base type of the messages written.</typeparam>
public sealed class ProtocolDefinition<TIn, TOut>
    where TIn : class
    where TOut : class
{
    internal ProtocolDefinition(
        string name,
        IReadOnlyList<string> warnings,
        ProtocolLimits limits,
        Func<ICodec<TIn, TOut>> codecFactory,
        bool readerCanResync,
        IReadOnlyList<Func<IMessageReader<TIn>>> stateReaders,
        Definition.CompiledState<TIn, TOut>[] states,
        int startState,
        Definition.TypeMap inboundTypes,
        Definition.TypeMap outboundTypes,
        ViolationAction violationAction,
        Func<Violation, TOut?>? violationReply,
        Func<TOut>? onClose,
        TimeSpan heartbeatInterval,
        Func<TOut>? heartbeat)
    {
        Name = name;
        Warnings = warnings;
        Limits = limits;
        CodecFactory = codecFactory;
        ReaderCanResync = readerCanResync;
        StateReaders = stateReaders;
        CompiledStates = states;
        StartState = startState;
        InboundTypes = inboundTypes;
        OutboundTypes = outboundTypes;
        ViolationAction = violationAction;
        ViolationReply = violationReply;
        OnCloseMessage = onClose;
        HeartbeatInterval = heartbeatInterval;
        HeartbeatMessage = heartbeat;

        var names = new string[states.Length];
        for (int i = 0; i < states.Length; i++)
            names[i] = states[i].Name;
        States = names;
    }

    /// <summary>Name of the protocol.</summary>
    public string Name { get; }

    /// <summary>
    /// Findings of the definition linter: opted-out limits and suspicious state machines. Empty for a clean
    /// definition; <see cref="ProtocolBuildOptions.WarningsAsErrors"/> turns them into build errors.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The limits every session of this protocol runs with.</summary>
    public ProtocolLimits Limits { get; }

    /// <summary>Names of the states, in the order they were described.</summary>
    public IReadOnlyList<string> States { get; }

    /// <inheritdoc />
    public override string ToString() => Name;

    internal Func<ICodec<TIn, TOut>> CodecFactory { get; }

    internal bool ReaderCanResync { get; }

    internal IReadOnlyList<Func<IMessageReader<TIn>>> StateReaders { get; }

    internal Definition.CompiledState<TIn, TOut>[] CompiledStates { get; }

    internal int StartState { get; }

    internal Definition.TypeMap InboundTypes { get; }

    internal Definition.TypeMap OutboundTypes { get; }

    internal ViolationAction ViolationAction { get; }

    internal Func<Violation, TOut?>? ViolationReply { get; }

    internal Func<TOut>? OnCloseMessage { get; }

    internal TimeSpan HeartbeatInterval { get; }

    internal Func<TOut>? HeartbeatMessage { get; }
}
