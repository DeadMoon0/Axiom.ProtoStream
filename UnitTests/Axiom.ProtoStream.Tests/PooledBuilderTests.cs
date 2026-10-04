using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Axiom.ProtoStream.Internal;

namespace Axiom.ProtoStream.Tests;

/// <summary>
/// The pooled async builder behind the session's read path: results and exceptions arrive as with any async
/// method, the execution context flows in and never back out, and a box reused from the pool never mixes two calls.
/// </summary>
public sealed class PooledBuilderTests
{
    private static readonly AsyncLocal<string?> Ambient = new();

    [AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder<>))]
    private static async ValueTask<int> AddAsync(int value, Task gate)
    {
        await gate;
        return value + 1;
    }

    [AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder<>))]
    private static async ValueTask<int> ThrowAsync(Task gate, bool beforeAwait)
    {
        if (beforeAwait)
            throw new InvalidOperationException("before");
        await gate;
        throw new InvalidOperationException("after");
    }

    [AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder))]
    private static async ValueTask CountAsync(StrongBox<int> counter, Task gate, bool fail)
    {
        await gate;
        if (fail)
            throw new InvalidOperationException("failed");
        counter.Value++;
    }

    [AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder<>))]
    private static async ValueTask<string?> SeeAndSetAmbientAsync(Task gate)
    {
        await gate;
        string? seen = Ambient.Value;
        Ambient.Value = "set inside";
        await Task.Yield();
        return seen;
    }

    [Fact]
    public async Task AMethodThatNeverWaits_AndOneThatDoes_BothAnswer()
    {
        Assert.Equal(2, await AddAsync(1, Task.CompletedTask));

        var gate = new TaskCompletionSource();
        ValueTask<int> pending = AddAsync(41, gate.Task);
        Assert.False(pending.IsCompleted);
        gate.SetResult();
        Assert.Equal(42, await pending);
    }

    [Fact]
    public async Task Exceptions_ArriveBeforeAndAfterTheFirstAwait()
    {
        Assert.Equal("before", (await Assert.ThrowsAsync<InvalidOperationException>(() => ThrowAsync(Task.CompletedTask, beforeAwait: true).AsTask())).Message);
        Assert.Equal("after", (await Assert.ThrowsAsync<InvalidOperationException>(() => ThrowAsync(Task.Delay(1), beforeAwait: false).AsTask())).Message);
    }

    [Fact]
    public async Task TheExecutionContext_FlowsIn_AndNotBackOut()
    {
        Ambient.Value = "caller";

        string? seen = await SeeAndSetAmbientAsync(Task.Delay(1));

        Assert.Equal("caller", seen);
        Assert.Equal("caller", Ambient.Value);
    }

    [Fact]
    public async Task ThousandsOfCallsInFlight_EachGetTheirOwnResult()
    {
        for (int round = 0; round < 5; round++)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask<int>[] pending = Enumerable.Range(0, 2000).Select(i => AddAsync(i, gate.Task)).ToArray();
            gate.SetResult();

            for (int i = 0; i < pending.Length; i++)
                Assert.Equal(i + 1, await pending[i]);
        }
    }

    [Fact]
    public async Task AMethodWithoutResult_CompletesAndFails_LikeAnyAsyncMethod()
    {
        var counter = new StrongBox<int>();
        await CountAsync(counter, Task.CompletedTask, fail: false);
        for (int i = 0; i < 300; i++)
            await CountAsync(counter, Task.Run(static () => { }), fail: false);

        Assert.Equal(301, counter.Value);
        Assert.Equal("failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => CountAsync(counter, Task.Delay(1), fail: true).AsTask())).Message);
    }
}
