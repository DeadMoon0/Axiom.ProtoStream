using System;
using ProtoStream.Codecs;
using ProtoStream.Framing;

namespace ProtoStream;

/// <summary>First stage of a protocol description: how bytes become messages.</summary>
/// <typeparam name="TIn">Base type of the messages the session reads.</typeparam>
/// <typeparam name="TOut">Base type of the messages the session writes.</typeparam>
public interface IDescribeStage<TIn, TOut>
    where TIn : class
    where TOut : class
{
    /// <summary>A codec that frames itself. The factory runs once per session.</summary>
    IStatesStage<TIn, TOut> Codec(Func<ICodec<TIn, TOut>> factory);

    /// <summary>A uniform frame format; the next stage says what is inside a frame.</summary>
    IFrameContentStage<TIn, TOut> Framing(IFramer framer);
}

/// <summary>Second stage for framed protocols: what a frame contains.</summary>
public interface IFrameContentStage<TIn, TOut>
    where TIn : class
    where TOut : class
{
    /// <summary>A hand-written frame codec. The factory runs once per session.</summary>
    IStatesStage<TIn, TOut> FrameCodec(Func<IFrameCodec<TIn, TOut>> factory);

    /// <summary>Frames carry a message id followed by a message that reads and writes itself.</summary>
    IStatesStage<TIn, TOut> Messages(Action<MessageSetBuilder<TIn, TOut>> configure);
}

/// <summary>Stage that describes the state machine.</summary>
public interface IStatesStage<TIn, TOut>
    where TIn : class
    where TOut : class
{
    /// <summary>The states, what each accepts and sends, and where messages lead.</summary>
    IPolicyStage<TIn, TOut> States(Action<IStateMachineBuilder<TIn, TOut>> configure);
}

/// <summary>Last stage: limits and policies, then <see cref="Build()"/>.</summary>
public interface IPolicyStage<TIn, TOut>
    where TIn : class
    where TOut : class
{
    /// <summary>Limits and timeouts. Unset values keep their safe defaults.</summary>
    IPolicyStage<TIn, TOut> Limits(Action<LimitsBuilder> configure);

    /// <summary>What happens on a violation that the reader can skip. The default is <see cref="ViolationAction.Close"/>.</summary>
    IPolicyStage<TIn, TOut> OnViolation(ViolationAction action);

    /// <summary>A message to send before closing on a violation; return null to send nothing.</summary>
    IPolicyStage<TIn, TOut> ReplyToViolations(Func<Violation, TOut?> reply);

    /// <summary>The message sent when the session is closed by the user.</summary>
    IPolicyStage<TIn, TOut> OnClose(Func<TOut> closingMessage);

    /// <summary>
    /// A message the framework sends when nothing was written for <paramref name="interval"/>.
    /// The only work ProtoStream schedules on its own.
    /// </summary>
    IPolicyStage<TIn, TOut> Heartbeat(TimeSpan interval, Func<TOut> message);

    /// <summary>Validates and compiles the description. Throws <see cref="Errors.ProtocolDefinitionException"/> listing every problem.</summary>
    ProtocolDefinition<TIn, TOut> Build();

    /// <summary>Validates and compiles the description with the given options.</summary>
    ProtocolDefinition<TIn, TOut> Build(ProtocolBuildOptions options);
}

/// <summary>Options for <see cref="IPolicyStage{TIn, TOut}.Build(ProtocolBuildOptions)"/>.</summary>
public sealed class ProtocolBuildOptions
{
    /// <summary>Treat every definition warning as an error, for example in CI.</summary>
    public bool WarningsAsErrors { get; init; }
}

/// <summary>What a session does with a violation that its reader can skip.</summary>
public enum ViolationAction
{
    /// <summary>Close the session. Always the outcome for violations that cannot be skipped.</summary>
    Close,

    /// <summary>Discard the offending message and keep reading: for lenient line protocols.</summary>
    Skip,
}

/// <summary>Describes the states of a protocol.</summary>
public interface IStateMachineBuilder<TIn, TOut>
    where TIn : class
    where TOut : class
{
    /// <summary>The state a session starts in.</summary>
    IStateMachineBuilder<TIn, TOut> Start(string state);

    /// <summary>Describes a state. Each state is described once.</summary>
    IStateBuilder<TIn, TOut> In(string state);

    /// <summary>A state that ends the session gracefully when entered.</summary>
    IStateMachineBuilder<TIn, TOut> Final(string state);
}

/// <summary>Describes one state.</summary>
public interface IStateBuilder<TIn, TOut> : IStateMachineBuilder<TIn, TOut>
    where TIn : class
    where TOut : class
{
    /// <summary>A message of type <typeparamref name="T"/> may arrive in this state.</summary>
    IInboundBuilder<TIn, TOut, T> On<T>() where T : class, TIn;

    /// <summary>The user may write a message of type <typeparamref name="T"/> in this state.</summary>
    ISendBuilder<TIn, TOut, T> OnSend<T>() where T : class, TOut;

    /// <summary>The session may switch to another protocol in this state.</summary>
    IStateBuilder<TIn, TOut> Switchable();

    /// <summary>A message the framework sends whenever the session enters this state, including the start state at open.</summary>
    IStateBuilder<TIn, TOut> OnEnter(Func<TOut> message);

    /// <summary>A different reader for this state, such as a dot-terminated body reader. The factory runs once per session.</summary>
    IStateBuilder<TIn, TOut> Reads(Func<IMessageReader<TIn>> factory);
}

/// <summary>Says what happens with an arriving message.</summary>
public interface IInboundBuilder<TIn, TOut, T>
    where TIn : class
    where TOut : class
    where T : class, TIn
{
    /// <summary>Hand the message to the user.</summary>
    ITransitionTail<TIn, TOut> Delegate();

    /// <summary>Send <paramref name="reply"/>'s result and keep reading; the user never sees the message.</summary>
    ITransitionTail<TIn, TOut> Respond(Func<T, TOut> reply);

    /// <summary>Consume the message silently.</summary>
    ITransitionTail<TIn, TOut> Wait();
}

/// <summary>After an inbound action: optionally say which state follows.</summary>
public interface ITransitionTail<TIn, TOut> : IStateBuilder<TIn, TOut>
    where TIn : class
    where TOut : class
{
    /// <summary>The session moves to <paramref name="state"/> after the action.</summary>
    IStateBuilder<TIn, TOut> GoTo(string state);
}

/// <summary>After an outbound declaration: optionally say which state follows the write.</summary>
public interface ISendBuilder<TIn, TOut, T> : IStateBuilder<TIn, TOut>
    where TIn : class
    where TOut : class
    where T : class, TOut
{
    /// <summary>The session moves to <paramref name="state"/> after the message was written.</summary>
    IStateBuilder<TIn, TOut> GoTo(string state);

    /// <summary>The session moves to <paramref name="whenTrue"/> or <paramref name="otherwise"/> depending on the written message.</summary>
    IStateBuilder<TIn, TOut> GoToIf(Func<T, bool> predicate, string whenTrue, string otherwise);
}
