using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Axiom.ProtoStream.Internal;

/// <summary>
/// An async method builder for <see cref="ValueTask{TResult}"/> whose state machine boxes go back to a pool once the
/// result is read. .NET's pooling builder keeps about one box per thread, which a server with a thousand sessions
/// waiting for input outgrows; this pool is striped per core and holds up to <see cref="StateMachineBox{TStateMachine, TResult}.Capacity"/>
/// boxes per stripe. A method that completes before its first await needs no box at all.
/// </summary>
/// <remarks>
/// The usual rule for pooled value tasks applies, and every caller in this assembly keeps it: a returned
/// <see cref="ValueTask{TResult}"/> is awaited once, and never read after that.
/// </remarks>
/// <typeparam name="TResult">The method's result.</typeparam>
internal struct PooledValueTaskMethodBuilder<TResult>
{
    private PooledBox<TResult>? _box;
    private TResult? _result;
    private Exception? _exception;

    public static PooledValueTaskMethodBuilder<TResult> Create() => default;

    public ValueTask<TResult> Task =>
        _box is { } box ? new ValueTask<TResult>(box, box.Version)
        : _exception is { } exception ? ValueTask.FromException<TResult>(exception)
        : new ValueTask<TResult>(_result!);

    /// <summary>The method's task without its result, for <see cref="PooledValueTaskMethodBuilder"/>.</summary>
    public ValueTask VoidTask =>
        _box is { } box ? new ValueTask(box, box.Version)
        : _exception is { } exception ? ValueTask.FromException(exception)
        : default;

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine =>
        AsyncTaskMethodBuilder.Create().Start(ref stateMachine);

    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }

    public void SetResult(TResult result)
    {
        if (_box is { } box)
            box.SetResult(result);
        else
            _result = result;
    }

    public void SetException(Exception exception)
    {
        if (_box is { } box)
            box.SetException(exception);
        else
            _exception = exception;
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        StateMachineBox<TStateMachine, TResult> box = Box(ref stateMachine);
        box.Context = null;
        awaiter.OnCompleted(box.MoveNextAction);
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        StateMachineBox<TStateMachine, TResult> box = Box(ref stateMachine);
        box.Context = ExecutionContext.Capture();
        awaiter.UnsafeOnCompleted(box.MoveNextAction);
    }

    /// <summary>The method's box; on its first await, rented and given a copy of the state machine (whose builder already points at it).</summary>
    private StateMachineBox<TStateMachine, TResult> Box<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        if (_box is StateMachineBox<TStateMachine, TResult> existing)
            return existing;
        StateMachineBox<TStateMachine, TResult> box = StateMachineBox<TStateMachine, TResult>.Rent();
        _box = box;
        box.StateMachine = stateMachine;
        return box;
    }
}

/// <summary>
/// <see cref="PooledValueTaskMethodBuilder{TResult}"/> for methods returning <see cref="ValueTask"/>: the same pool, with a
/// result nobody reads.
/// </summary>
internal struct PooledValueTaskMethodBuilder
{
    private PooledValueTaskMethodBuilder<bool> _inner;

    public static PooledValueTaskMethodBuilder Create() => default;

    public ValueTask Task => _inner.VoidTask;

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine =>
        AsyncTaskMethodBuilder.Create().Start(ref stateMachine);

    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }

    public void SetResult() => _inner.SetResult(true);

    public void SetException(Exception exception) => _inner.SetException(exception);

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        _inner.AwaitOnCompleted(ref awaiter, ref stateMachine);

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        _inner.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
}

/// <summary>The value-task source of a pooled async method: completed once, read once, then back to its pool.</summary>
internal abstract class PooledBox<TResult> : IValueTaskSource<TResult>, IValueTaskSource
{
    private ManualResetValueTaskSourceCore<TResult> _core;

    public short Version => _core.Version;

    public void SetResult(TResult result) => _core.SetResult(result);

    public void SetException(Exception exception) => _core.SetException(exception);

    public TResult GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            _core.Reset();
            Return();
        }
    }

    void IValueTaskSource.GetResult(short token) => GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);

    protected abstract void Return();
}

/// <summary>A box for one async method's state machine, pooled per method in stripes by core.</summary>
internal sealed class StateMachineBox<TStateMachine, TResult> : PooledBox<TResult>
    where TStateMachine : IAsyncStateMachine
{
    public const int Capacity = 128;

    private static readonly ContextCallback RunInContext = static box => ((StateMachineBox<TStateMachine, TResult>)box!).StateMachine!.MoveNext();
    private static readonly Stripe[] Stripes = CreateStripes();

    private StateMachineBox() => MoveNextAction = MoveNext;

    public TStateMachine? StateMachine;

    public ExecutionContext? Context;

    public Action MoveNextAction { get; }

    public static StateMachineBox<TStateMachine, TResult> Rent()
    {
        Stripe stripe = Stripes[(uint)Thread.GetCurrentProcessorId() % (uint)Stripes.Length];
        lock (stripe)
        {
            if (stripe.Count > 0)
            {
                StateMachineBox<TStateMachine, TResult> box = stripe.Items[--stripe.Count]!;
                stripe.Items[stripe.Count] = null;
                return box;
            }
        }

        return new StateMachineBox<TStateMachine, TResult>();
    }

    protected override void Return()
    {
        StateMachine = default;
        Context = null;
        Stripe stripe = Stripes[(uint)Thread.GetCurrentProcessorId() % (uint)Stripes.Length];
        lock (stripe)
        {
            if (stripe.Count < Capacity)
                stripe.Items[stripe.Count++] = this;
        }
    }

    private void MoveNext()
    {
        if (Context is null)
            StateMachine!.MoveNext();
        else
            ExecutionContext.Run(Context, RunInContext, this);
    }

    private static Stripe[] CreateStripes()
    {
        var stripes = new Stripe[Math.Max(1, Environment.ProcessorCount)];
        for (int i = 0; i < stripes.Length; i++)
            stripes[i] = new Stripe();
        return stripes;
    }

    private sealed class Stripe
    {
        public readonly StateMachineBox<TStateMachine, TResult>?[] Items = new StateMachineBox<TStateMachine, TResult>?[Capacity];

        public int Count;
    }
}
