using System;
using System.Collections.Generic;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Framing;

namespace Axiom.ProtoStream.Definition;

/// <summary>Validates a description, lints it and compiles it into lookup tables.</summary>
internal static class DefinitionCompiler
{
    /// <summary>Room for a frame header on top of the largest frame content: more than any built-in framer needs.</summary>
    private const int FrameHeaderAllowance = 64;

    public static ProtocolDefinition<TIn, TOut> Compile<TIn, TOut>(ProtocolBuilder<TIn, TOut> builder, ProtocolBuildOptions options)
        where TIn : class
        where TOut : class
    {
        var errors = new List<string>(builder.Errors);
        var warnings = new List<string>();
        StateMachineBuilder<TIn, TOut> machine = builder.StateMachine ?? throw new InvalidOperationException("States were not described.");
        errors.AddRange(machine.Errors);
        errors.AddRange(builder.LimitsDescription.Errors);
        warnings.AddRange(builder.LimitsDescription.OptOuts);
        ProtocolLimits limits = builder.LimitsDescription.Limits;

        Func<ICodec<TIn, TOut>>? codecFactory = CompileCodec(builder, errors);
        bool canResync = ProbeCodec(codecFactory, errors);

        // States and their names.
        if (machine.States.Count == 0)
            errors.Add("No states are described.");
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (StateDescription<TIn, TOut> state in machine.States)
            ids[state.Name] = ids.Count;

        int start = -1;
        if (machine.StartState is null)
            errors.Add("No start state is described; use Start(...).");
        else if (!ids.TryGetValue(machine.StartState, out start))
            errors.Add($"The start state '{machine.StartState}' is not described.");

        // Message types.
        var inboundTypes = new List<Type>();
        var outboundTypes = new List<Type>();
        foreach (StateDescription<TIn, TOut> state in machine.States)
        {
            foreach (InboundDescription<TIn, TOut> inbound in state.Inbound)
                inboundTypes.Add(inbound.Type);
            foreach (OutboundDescription<TOut> outbound in state.Outbound)
                outboundTypes.Add(outbound.Type);
        }

        if (builder.MessageSet is not null)
        {
            RequireRegistered(inboundTypes, builder.MessageSet.InboundTypes, "arrives", errors);
            RequireRegistered(outboundTypes, builder.MessageSet.OutboundTypes, "is sent", errors);
        }

        var inboundMap = new TypeMap(inboundTypes);
        var outboundMap = new TypeMap(outboundTypes);

        // Compile every state.
        var readers = new List<Func<IMessageReader<TIn>>>();
        var states = new CompiledState<TIn, TOut>[machine.States.Count];
        for (int i = 0; i < machine.States.Count; i++)
            states[i] = CompileState(machine.States[i], i, ids, inboundMap, outboundMap, readers, errors, warnings);

        if (start >= 0)
            RequireReachable(machine.States, start, ids, errors);

        // Policies and limits.
        if (builder.ViolationAction == ViolationAction.Skip && !canResync)
            warnings.Add("OnViolation(Skip) has no effect: the codec cannot resume after an invalid message, so violations still close the session.");
        if (builder.HeartbeatMessage is not null)
        {
            if (builder.HeartbeatInterval <= TimeSpan.Zero)
                errors.Add($"The heartbeat interval must be positive, not {builder.HeartbeatInterval}.");
            else if (builder.HeartbeatInterval < TimeSpan.FromSeconds(1))
                warnings.Add($"A heartbeat every {builder.HeartbeatInterval.TotalMilliseconds} ms floods the peer.");
        }

        if (builder.Framer is { } framer && limits.MaxBufferedBytes < (long)framer.MaxFrameSize + FrameHeaderAllowance)
            errors.Add($"MaxBufferedBytes ({limits.MaxBufferedBytes}) cannot hold a frame of the maximum size ({framer.MaxFrameSize}) plus its header.");

        if (options.WarningsAsErrors)
            errors.AddRange(warnings);
        if (errors.Count > 0)
            throw new ProtocolDefinitionException(builder.Name, errors);

        return new ProtocolDefinition<TIn, TOut>(
            builder.Name, warnings, limits, codecFactory!, canResync, readers, states, start, inboundMap, outboundMap,
            builder.ViolationAction, builder.ViolationReply, builder.CloseMessage, builder.HeartbeatInterval, builder.HeartbeatMessage);
    }

    private static Func<ICodec<TIn, TOut>>? CompileCodec<TIn, TOut>(ProtocolBuilder<TIn, TOut> builder, List<string> errors)
        where TIn : class
        where TOut : class
    {
        if (builder.CodecFactory is { } codec)
            return codec;
        if (builder.Framer is not { } framer)
        {
            errors.Add("No codec is described; use Codec(...) or Framing(...).");
            return null;
        }

        if (builder.FrameCodecFactory is { } frameCodec)
            return () => new FramedCodec<TIn, TOut>(framer, frameCodec());

        if (builder.MessageSet is { } set)
        {
            MessageSetCodec<TIn, TOut>? compiled = set.Compile();
            errors.AddRange(set.Errors);
            return compiled is null ? null : () => new FramedCodec<TIn, TOut>(framer, compiled);
        }

        errors.Add("The frame content is not described; use FrameCodec(...) or Messages(...).");
        return null;
    }

    // Creating one codec at build time proves the factory works and tells the linter what the reader can do.
    private static bool ProbeCodec<TIn, TOut>(Func<ICodec<TIn, TOut>>? factory, List<string> errors)
    {
        if (factory is null)
            return false;

        try
        {
            ICodec<TIn, TOut> codec = factory() ?? throw new InvalidOperationException("The codec factory returned null.");
            bool canResync = codec.CanResync;
            (codec as IDisposable)?.Dispose();
            return canResync;
        }
        catch (Exception ex)
        {
            errors.Add($"The codec factory failed: {ex.Message}");
            return false;
        }
    }

    private static void RequireRegistered(List<Type> used, IReadOnlyCollection<Type> registered, string verb, List<string> errors)
    {
        foreach (Type type in used)
        {
            bool found = false;
            foreach (Type candidate in registered)
                found |= type.IsAssignableFrom(candidate);
            if (!found)
                errors.Add($"{type.Name} {verb} in a state but no registered message is a {type.Name}.");
        }
    }

    private static CompiledState<TIn, TOut> CompileState<TIn, TOut>(
        StateDescription<TIn, TOut> state, int id, Dictionary<string, int> ids, TypeMap inboundMap, TypeMap outboundMap,
        List<Func<IMessageReader<TIn>>> readers, List<string> errors, List<string> warnings)
    {
        if (state.IsFinal && (state.DescribedByIn || state.Inbound.Count > 0 || state.Outbound.Count > 0 || state.OnEnter is not null || state.Reader is not null))
            errors.Add($"Final state '{state.Name}' cannot have transitions, OnEnter or Reads: entering it ends the session.");
        if (!state.IsFinal && state.Inbound.Count == 0 && state.Outbound.Count == 0)
            warnings.Add($"State '{state.Name}' accepts and sends nothing: a session there can only wait for a timeout.");

        var inbound = new InboundTransition<TIn, TOut>[inboundMap.Count];
        foreach (InboundDescription<TIn, TOut> description in state.Inbound)
        {
            int typeId = inboundMap.Find(description.Type);
            if (inbound[typeId].IsDefined)
                errors.Add($"State '{state.Name}' describes {description.Type.Name} twice.");
            inbound[typeId] = new InboundTransition<TIn, TOut>
            {
                IsDefined = true,
                Action = description.Action,
                Next = Resolve(description.Next, state.Name, ids, errors),
                Responder = description.Responder,
            };
        }

        var outbound = new OutboundTransition<TOut>[outboundMap.Count];
        foreach (OutboundDescription<TOut> description in state.Outbound)
        {
            int typeId = outboundMap.Find(description.Type);
            if (outbound[typeId].IsDefined)
                errors.Add($"State '{state.Name}' describes sending {description.Type.Name} twice.");
            outbound[typeId] = new OutboundTransition<TOut>
            {
                IsDefined = true,
                Next = Resolve(description.Next, state.Name, ids, errors),
                NextIfFalse = Resolve(description.NextIfFalse, state.Name, ids, errors),
                Predicate = description.Predicate,
            };
        }

        int readerIndex = -1;
        if (state.Reader is not null)
        {
            readerIndex = readers.Count;
            readers.Add(state.Reader);
        }

        return new CompiledState<TIn, TOut>
        {
            Name = state.Name,
            Id = id,
            IsFinal = state.IsFinal,
            IsSwitchable = state.IsSwitchable,
            ReaderIndex = readerIndex,
            OnEnter = state.OnEnter,
            Inbound = inbound,
            Outbound = outbound,
        };
    }

    private static int Resolve(string? target, string from, Dictionary<string, int> ids, List<string> errors)
    {
        if (target is null)
            return -1;
        if (ids.TryGetValue(target, out int id))
            return id;
        errors.Add($"State '{from}' goes to '{target}', which is not described.");
        return -1;
    }

    private static void RequireReachable<TIn, TOut>(List<StateDescription<TIn, TOut>> states, int start, Dictionary<string, int> ids, List<string> errors)
    {
        var reached = new bool[states.Count];
        var pending = new Stack<int>();
        pending.Push(start);
        reached[start] = true;
        while (pending.Count > 0)
        {
            StateDescription<TIn, TOut> state = states[pending.Pop()];
            foreach (InboundDescription<TIn, TOut> inbound in state.Inbound)
                Visit(inbound.Next);
            foreach (OutboundDescription<TOut> outbound in state.Outbound)
            {
                Visit(outbound.Next);
                Visit(outbound.NextIfFalse);
            }
        }

        for (int i = 0; i < states.Count; i++)
        {
            if (!reached[i])
                errors.Add($"State '{states[i].Name}' cannot be reached from the start state.");
        }

        void Visit(string? target)
        {
            if (target is not null && ids.TryGetValue(target, out int id) && !reached[id])
            {
                reached[id] = true;
                pending.Push(id);
            }
        }
    }
}
