using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Axiom.ProtoStream.Internal;
using Axiom.ProtoStream.Tests.Shared;

namespace Axiom.ProtoStream.Tests;

/// <summary>
/// The connection's reader over a stream: it keeps the <see cref="PipeReader"/> contract (examined input is not
/// returned again, unexamined input is, cancellation ends a read without throwing), holds no buffer while idle, and
/// reuses its buffers and segments.
/// </summary>
public sealed class StreamInputReaderTests
{
    [Fact]
    public async Task Input_ArrivesInOrder_AndTheEndOfTheStream_Completes()
    {
        var pipe = new Pipe();
        var reader = new StreamInputReader(pipe.Reader.AsStream(), MemoryPool<byte>.Shared, 16);

        await pipe.Writer.WriteAsync("hello"u8.ToArray());
        ReadResult first = await reader.ReadAsync();
        Assert.Equal("hello"u8.ToArray(), first.Buffer.ToArray());
        Assert.False(first.IsCompleted);
        reader.AdvanceTo(first.Buffer.GetPosition(2), first.Buffer.End);

        await pipe.Writer.WriteAsync(" world"u8.ToArray());
        await pipe.Writer.CompleteAsync();
        ReadResult second = await reader.ReadAsync();
        Assert.Equal("llo world"u8.ToArray(), second.Buffer.ToArray());
        reader.AdvanceTo(second.Buffer.End);

        ReadResult end = await reader.ReadAsync();
        Assert.True(end.IsCompleted);
        Assert.True(end.Buffer.IsEmpty);
    }

    [Fact]
    public async Task UnexaminedInput_IsReturnedAgain_WithoutWaitingForTheStream()
    {
        var pipe = new Pipe();
        var reader = new StreamInputReader(pipe.Reader.AsStream(), MemoryPool<byte>.Shared, 16);
        await pipe.Writer.WriteAsync("abcdef"u8.ToArray());

        ReadResult first = await reader.ReadAsync();
        reader.AdvanceTo(first.Buffer.Start, first.Buffer.GetPosition(3));

        ValueTask<ReadResult> pending = reader.ReadAsync();
        Assert.True(pending.IsCompletedSuccessfully);
        ReadResult again = await pending;
        Assert.Equal("abcdef"u8.ToArray(), again.Buffer.ToArray());
        reader.AdvanceTo(again.Buffer.Start, again.Buffer.End);

        ValueTask<ReadResult> waiting = reader.ReadAsync();
        Assert.False(waiting.IsCompleted);
        await pipe.Writer.WriteAsync("g"u8.ToArray());
        Assert.Equal("abcdefg"u8.ToArray(), (await waiting).Buffer.ToArray());
    }

    [Fact]
    public async Task InputLargerThanABuffer_SpansSegments_AndStaysWhole()
    {
        var pipe = new Pipe();
        var reader = new StreamInputReader(pipe.Reader.AsStream(), MemoryPool<byte>.Shared, 64);
        byte[] sent = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();

        Task writing = Task.Run(async () =>
        {
            for (int i = 0; i < sent.Length; i += 100)
                await pipe.Writer.WriteAsync(sent.AsMemory(i, 100));
            await pipe.Writer.CompleteAsync();
        });

        ReadResult result;
        while (true)
        {
            result = await reader.ReadAsync();
            if (result.IsCompleted)
                break;
            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }

        await writing;
        Assert.False(result.Buffer.IsSingleSegment);
        Assert.Equal(sent, result.Buffer.ToArray());
    }

    [Fact]
    public async Task CancelPendingRead_EndsAWaitingRead_AndTheNextReadStillWorks()
    {
        var pipe = new Pipe();
        var reader = new StreamInputReader(pipe.Reader.AsStream(), MemoryPool<byte>.Shared, 16);

        ValueTask<ReadResult> waiting = reader.ReadAsync();
        reader.CancelPendingRead();
        ReadResult canceled = await waiting;
        Assert.True(canceled.IsCanceled);
        reader.AdvanceTo(canceled.Buffer.Start);

        reader.CancelPendingRead();
        Assert.True((await reader.ReadAsync()).IsCanceled);

        await pipe.Writer.WriteAsync("x"u8.ToArray());
        ReadResult read = await reader.ReadAsync();
        Assert.False(read.IsCanceled);
        Assert.Equal("x"u8.ToArray(), read.Buffer.ToArray());
    }

    [Fact]
    public async Task TheCallersToken_Throws()
    {
        var pipe = new Pipe();
        var reader = new StreamInputReader(pipe.Reader.AsStream(), MemoryPool<byte>.Shared, 16);
        using var source = new CancellationTokenSource();

        ValueTask<ReadResult> waiting = reader.ReadAsync(source.Token);
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
    }

    [Fact]
    public async Task AnIdleReader_HoldsNoBuffer_AndCompleteGivesEverythingBack()
    {
        var pool = new CountingPool();
        var pipe = new Pipe();
        var reader = new StreamInputReader(pipe.Reader.AsStream(), pool, 16);

        ValueTask<ReadResult> waiting = reader.ReadAsync();
        await Task.Delay(20);
        Assert.Equal(0, pool.Outstanding);

        await pipe.Writer.WriteAsync("abc"u8.ToArray());
        ReadResult read = await waiting;
        Assert.Equal(1, pool.Outstanding);
        reader.AdvanceTo(read.Buffer.End);
        Assert.Equal(0, pool.Outstanding);

        await pipe.Writer.WriteAsync("def"u8.ToArray());
        read = await reader.ReadAsync();
        reader.AdvanceTo(read.Buffer.Start);
        Assert.Equal(1, pool.Outstanding);

        reader.Complete();
        Assert.Equal(0, pool.Outstanding);
        Assert.Throws<InvalidOperationException>(() => reader.ReadAsync());
    }

    [ReleaseFact]
    public void SteadyState_AllocatesNothing()
    {
        var reader = new StreamInputReader(new RepeatingStream("GET / HTTP/1.1\r\n\r\n"u8.ToArray()), MemoryPool<byte>.Shared, 4096);
        for (int i = 0; i < 100; i++)
            Consume(reader);

        const int Reads = 10_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Reads; i++)
            Consume(reader);

        // Less than a byte per read: nothing per read (the runtime may allocate once, e.g. when it recompiles the loop).
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < Reads, $"{allocated} bytes for {Reads} reads.");

        static void Consume(StreamInputReader reader)
        {
            ValueTask<ReadResult> pending = reader.ReadAsync();
            ReadResult result = pending.Result;
            reader.AdvanceTo(result.Buffer.End);
        }
    }

    /// <summary>Answers every read at once with the same bytes.</summary>
    private sealed class RepeatingStream(byte[] chunk) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = Math.Min(buffer.Length, chunk.Length);
            chunk.AsSpan(0, count).CopyTo(buffer.Span);
            return new ValueTask<int>(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Counts the buffers rented and not yet given back.</summary>
    private sealed class CountingPool : MemoryPool<byte>
    {
        private int _outstanding;

        public int Outstanding => Volatile.Read(ref _outstanding);

        public override int MaxBufferSize => int.MaxValue;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            Interlocked.Increment(ref _outstanding);
            return new Owner(this, new byte[Math.Max(minBufferSize, 1)]);
        }

        protected override void Dispose(bool disposing)
        {
        }

        private sealed class Owner(CountingPool pool, byte[] array) : IMemoryOwner<byte>
        {
            private int _disposed;

            public Memory<byte> Memory => array;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Decrement(ref pool._outstanding);
            }
        }
    }
}
