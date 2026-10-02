using System;
using System.Collections.Generic;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Framing;

namespace Axiom.ProtoStream.Definition;

/// <summary>Collects a protocol description; every stage interface is implemented here.</summary>
internal sealed class ProtocolBuilder<TIn, TOut>(string name)
    : IDescribeStage<TIn, TOut>, IFrameContentStage<TIn, TOut>, IStatesStage<TIn, TOut>, IPolicyStage<TIn, TOut>
    where TIn : class
    where TOut : class
{
    public string Name { get; } = name;

    public IFramer? Framer { get; private set; }

    public Func<ICodec<TIn, TOut>>? CodecFactory { get; private set; }

    public Func<IFrameCodec<TIn, TOut>>? FrameCodecFactory { get; private set; }

    public MessageSetBuilder<TIn, TOut>? MessageSet { get; private set; }

    public StateMachineBuilder<TIn, TOut>? StateMachine { get; private set; }

    public LimitsBuilder LimitsDescription { get; } = new();

    public ViolationAction ViolationAction { get; private set; } = ViolationAction.Close;

    public Func<Violation, TOut?>? ViolationReply { get; private set; }

    public Func<TOut>? CloseMessage { get; private set; }

    public TimeSpan HeartbeatInterval { get; private set; }

    public Func<TOut>? HeartbeatMessage { get; private set; }

    public List<string> Errors { get; } = [];

    public IStatesStage<TIn, TOut> Codec(Func<ICodec<TIn, TOut>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        CodecFactory = factory;
        return this;
    }

    public IFrameContentStage<TIn, TOut> Framing(IFramer framer)
    {
        ArgumentNullException.ThrowIfNull(framer);
        Framer = framer;
        return this;
    }

    public IStatesStage<TIn, TOut> FrameCodec(Func<IFrameCodec<TIn, TOut>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        FrameCodecFactory = factory;
        return this;
    }

    public IStatesStage<TIn, TOut> Messages(Action<MessageSetBuilder<TIn, TOut>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        MessageSet = new MessageSetBuilder<TIn, TOut>();
        configure(MessageSet);
        return this;
    }

    public IPolicyStage<TIn, TOut> States(Action<IStateMachineBuilder<TIn, TOut>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        StateMachine = new StateMachineBuilder<TIn, TOut>();
        configure(StateMachine);
        return this;
    }

    public IPolicyStage<TIn, TOut> Limits(Action<LimitsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(LimitsDescription);
        return this;
    }

    public IPolicyStage<TIn, TOut> OnViolation(ViolationAction action)
    {
        ViolationAction = action;
        return this;
    }

    public IPolicyStage<TIn, TOut> ReplyToViolations(Func<Violation, TOut?> reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (ViolationReply is not null)
            Errors.Add("ReplyToViolations is described twice.");
        ViolationReply = reply;
        return this;
    }

    public IPolicyStage<TIn, TOut> OnClose(Func<TOut> closingMessage)
    {
        ArgumentNullException.ThrowIfNull(closingMessage);
        if (CloseMessage is not null)
            Errors.Add("OnClose is described twice.");
        CloseMessage = closingMessage;
        return this;
    }

    public IPolicyStage<TIn, TOut> Heartbeat(TimeSpan interval, Func<TOut> message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (HeartbeatMessage is not null)
            Errors.Add("Heartbeat is described twice.");
        HeartbeatInterval = interval;
        HeartbeatMessage = message;
        return this;
    }

    public ProtocolDefinition<TIn, TOut> Build() => Build(new ProtocolBuildOptions());

    public ProtocolDefinition<TIn, TOut> Build(ProtocolBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return DefinitionCompiler.Compile(this, options);
    }
}

internal sealed class StateDescription<TIn, TOut>(string name)
{
    public string Name { get; } = name;

    public bool DescribedByIn { get; set; }

    public bool IsFinal { get; set; }

    public bool IsSwitchable { get; set; }

    public Func<TOut>? OnEnter { get; set; }

    public Func<IMessageReader<TIn>>? Reader { get; set; }

    public List<InboundDescription<TIn, TOut>> Inbound { get; } = [];

    public List<OutboundDescription<TOut>> Outbound { get; } = [];
}

internal sealed class InboundDescription<TIn, TOut>(Type type, InboundAction action, Func<TIn, TOut>? responder)
{
    public Type Type { get; } = type;

    public InboundAction Action { get; } = action;

    public Func<TIn, TOut>? Responder { get; } = responder;

    public string? Next { get; set; }
}

internal sealed class OutboundDescription<TOut>(Type type)
{
    public Type Type { get; } = type;

    public string? Next { get; set; }

    public string? NextIfFalse { get; set; }

    public Func<TOut, bool>? Predicate { get; set; }
}

internal sealed class StateMachineBuilder<TIn, TOut> : ITransitionTail<TIn, TOut>
    where TIn : class
    where TOut : class
{
    private readonly Dictionary<string, StateDescription<TIn, TOut>> _byName = new(StringComparer.Ordinal);
    private StateDescription<TIn, TOut>? _current;
    private InboundDescription<TIn, TOut>? _lastInbound;

    public List<StateDescription<TIn, TOut>> States { get; } = [];

    public string? StartState { get; private set; }

    public List<string> Errors { get; } = [];

    public IStateMachineBuilder<TIn, TOut> Start(string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        if (StartState is not null)
            Errors.Add($"Start is described twice ('{StartState}' and '{state}').");
        StartState = state;
        return this;
    }

    public IStateBuilder<TIn, TOut> In(string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        StateDescription<TIn, TOut> description = GetOrAdd(state);
        if (description.DescribedByIn)
            Errors.Add($"State '{state}' is described twice.");
        description.DescribedByIn = true;
        _current = description;
        _lastInbound = null;
        return this;
    }

    public IStateMachineBuilder<TIn, TOut> Final(string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        StateDescription<TIn, TOut> description = GetOrAdd(state);
        if (description.IsFinal)
            Errors.Add($"Final state '{state}' is described twice.");
        description.IsFinal = true;
        _current = null;
        _lastInbound = null;
        return this;
    }

    public IInboundBuilder<TIn, TOut, T> On<T>() where T : class, TIn => new InboundBuilder<T>(this);

    public ISendBuilder<TIn, TOut, T> OnSend<T>() where T : class, TOut
    {
        var outbound = new OutboundDescription<TOut>(typeof(T));
        Current.Outbound.Add(outbound);
        _lastInbound = null;
        return new SendBuilder<T>(this, outbound);
    }

    public IStateBuilder<TIn, TOut> Switchable()
    {
        Current.IsSwitchable = true;
        return this;
    }

    public IStateBuilder<TIn, TOut> OnEnter(Func<TOut> message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Current.OnEnter is not null)
            Errors.Add($"OnEnter of state '{Current.Name}' is described twice.");
        Current.OnEnter = message;
        return this;
    }

    public IStateBuilder<TIn, TOut> Reads(Func<IMessageReader<TIn>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (Current.Reader is not null)
            Errors.Add($"Reads of state '{Current.Name}' is described twice.");
        Current.Reader = factory;
        return this;
    }

    public IStateBuilder<TIn, TOut> GoTo(string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        if (_lastInbound is null)
            Errors.Add($"GoTo('{state}') in state '{Current.Name}' does not follow an inbound action.");
        else
            _lastInbound.Next = state;
        _lastInbound = null;
        return this;
    }

    private StateDescription<TIn, TOut> Current =>
        _current ?? throw new InvalidOperationException("Describe a state with In(...) first.");

    private ITransitionTail<TIn, TOut> AddInbound(Type type, InboundAction action, Func<TIn, TOut>? responder)
    {
        _lastInbound = new InboundDescription<TIn, TOut>(type, action, responder);
        Current.Inbound.Add(_lastInbound);
        return this;
    }

    private StateDescription<TIn, TOut> GetOrAdd(string state)
    {
        if (!_byName.TryGetValue(state, out StateDescription<TIn, TOut>? description))
        {
            description = new StateDescription<TIn, TOut>(state);
            _byName.Add(state, description);
            States.Add(description);
        }

        return description;
    }

    private sealed class InboundBuilder<T>(StateMachineBuilder<TIn, TOut> parent) : IInboundBuilder<TIn, TOut, T>
        where T : class, TIn
    {
        public ITransitionTail<TIn, TOut> Delegate() => parent.AddInbound(typeof(T), InboundAction.Delegate, null);

        public ITransitionTail<TIn, TOut> Respond(Func<T, TOut> reply)
        {
            ArgumentNullException.ThrowIfNull(reply);
            return parent.AddInbound(typeof(T), InboundAction.Respond, message => reply((T)message));
        }

        public ITransitionTail<TIn, TOut> Wait() => parent.AddInbound(typeof(T), InboundAction.Wait, null);
    }

    private sealed class SendBuilder<T>(StateMachineBuilder<TIn, TOut> parent, OutboundDescription<TOut> outbound) : ISendBuilder<TIn, TOut, T>
        where T : class, TOut
    {
        public IStateBuilder<TIn, TOut> GoTo(string state)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(state);
            outbound.Next = state;
            return parent;
        }

        public IStateBuilder<TIn, TOut> GoToIf(Func<T, bool> predicate, string whenTrue, string otherwise)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentException.ThrowIfNullOrWhiteSpace(whenTrue);
            ArgumentException.ThrowIfNullOrWhiteSpace(otherwise);
            outbound.Predicate = message => predicate((T)message);
            outbound.Next = whenTrue;
            outbound.NextIfFalse = otherwise;
            return parent;
        }

        public IInboundBuilder<TIn, TOut, TMessage> On<TMessage>() where TMessage : class, TIn => parent.On<TMessage>();

        public ISendBuilder<TIn, TOut, TMessage> OnSend<TMessage>() where TMessage : class, TOut => parent.OnSend<TMessage>();

        public IStateBuilder<TIn, TOut> Switchable() => parent.Switchable();

        public IStateBuilder<TIn, TOut> OnEnter(Func<TOut> message) => parent.OnEnter(message);

        public IStateBuilder<TIn, TOut> Reads(Func<IMessageReader<TIn>> factory) => parent.Reads(factory);

        public IStateMachineBuilder<TIn, TOut> Start(string state) => parent.Start(state);

        public IStateBuilder<TIn, TOut> In(string state) => parent.In(state);

        public IStateMachineBuilder<TIn, TOut> Final(string state) => parent.Final(state);
    }
}
