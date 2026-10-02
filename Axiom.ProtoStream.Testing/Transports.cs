using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Axiom.ProtoStream.Testing;

/// <summary>Two ends of an in-memory, full-duplex transport.</summary>
public sealed class TransportPair
{
    internal TransportPair(IDuplexPipe client, IDuplexPipe server)
    {
        Client = client;
        Server = server;
    }

    /// <summary>The client end.</summary>
    public IDuplexPipe Client { get; }

    /// <summary>The server end.</summary>
    public IDuplexPipe Server { get; }
}

/// <summary>In-memory transports for session tests.</summary>
public static class InMemoryTransport
{
    /// <summary>Creates two cross-wired pipes: what the client writes, the server reads, and the other way round.</summary>
    /// <remarks>
    /// The pipes pause their writer at <see cref="DefaultPauseThreshold"/>, above the largest message the built-in
    /// protocols buffer by default. A pause threshold below a protocol's buffer limit deadlocks: the writer waits
    /// for the reader to consume, the reader waits for the rest of the message.
    /// </remarks>
    public static TransportPair CreatePair() => CreatePair(new PipeOptions(
        pauseWriterThreshold: DefaultPauseThreshold, resumeWriterThreshold: DefaultResumeThreshold, useSynchronizationContext: false));

    /// <summary>Unconsumed bytes at which the default pipes pause their writer: 16 MiB.</summary>
    public const long DefaultPauseThreshold = 16 * 1024 * 1024;

    /// <summary>Unconsumed bytes at which a paused writer of the default pipes resumes: 8 MiB.</summary>
    public const long DefaultResumeThreshold = 8 * 1024 * 1024;

    /// <summary>Creates two cross-wired pipes with the given options, for example to test backpressure.</summary>
    public static TransportPair CreatePair(PipeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var toServer = new Pipe(options);
        var toClient = new Pipe(options);
        return new TransportPair(new DuplexPipe(toClient.Reader, toServer.Writer), new DuplexPipe(toServer.Reader, toClient.Writer));
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}

/// <summary>
/// A transport whose input arrives in the given chunks, each as its own buffer segment, and whose output is
/// captured. A new chunk arrives only once the reader examined everything before it, like a network read.
/// </summary>
public sealed class ScriptedTransport : IDuplexPipe
{
    private readonly Pipe _written = new(new PipeOptions(pauseWriterThreshold: long.MaxValue, resumeWriterThreshold: long.MaxValue, useSynchronizationContext: false));
    private readonly List<byte> _captured = [];

    /// <summary>Creates a transport delivering <paramref name="chunks"/> in order, then the end of input.</summary>
    public ScriptedTransport(IEnumerable<ReadOnlyMemory<byte>> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        Input = new ChunkedPipeReader(chunks);
    }

    /// <inheritdoc />
    public PipeReader Input { get; }

    /// <inheritdoc />
    public PipeWriter Output => _written.Writer;

    /// <summary>Everything written to the transport so far.</summary>
    public byte[] Written()
    {
        while (_written.Reader.TryRead(out ReadResult result))
        {
            foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                _captured.AddRange(segment.Span.ToArray());
            _written.Reader.AdvanceTo(result.Buffer.End);
            if (result.Buffer.IsEmpty)
                break;
        }

        return [.. _captured];
    }
}

/// <summary>A pipe reader that hands out predetermined chunks as separate segments.</summary>
public sealed class ChunkedPipeReader : PipeReader
{
    private readonly Queue<ReadOnlyMemory<byte>> _upcoming;
    private readonly List<ReadOnlyMemory<byte>> _buffered = [];
    private ReadOnlySequence<byte> _last;
    private bool _examinedAll = true;
    private volatile bool _cancelPending;

    /// <summary>Creates a reader over <paramref name="chunks"/>.</summary>
    public ChunkedPipeReader(IEnumerable<ReadOnlyMemory<byte>> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        _upcoming = new Queue<ReadOnlyMemory<byte>>(chunks);
    }

    /// <inheritdoc />
    public override bool TryRead(out ReadResult result)
    {
        if (_cancelPending)
        {
            _cancelPending = false;
            result = new ReadResult(Current(), isCanceled: true, isCompleted: false);
            return true;
        }

        if (_examinedAll && _upcoming.Count > 0)
            _buffered.Add(_upcoming.Dequeue());

        result = new ReadResult(Current(), isCanceled: false, isCompleted: _upcoming.Count == 0);
        return true;
    }

    /// <inheritdoc />
    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryRead(out ReadResult result);
        return new ValueTask<ReadResult>(result);
    }

    /// <inheritdoc />
    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    /// <inheritdoc />
    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        long consumedBytes = _last.Slice(_last.Start, consumed).Length;
        _examinedAll = _last.Slice(_last.Start, examined).Length == _last.Length;

        while (consumedBytes > 0 && _buffered.Count > 0)
        {
            ReadOnlyMemory<byte> first = _buffered[0];
            if (consumedBytes >= first.Length)
            {
                consumedBytes -= first.Length;
                _buffered.RemoveAt(0);
            }
            else
            {
                _buffered[0] = first[(int)consumedBytes..];
                consumedBytes = 0;
            }
        }
    }

    /// <inheritdoc />
    public override void CancelPendingRead() => _cancelPending = true;

    /// <inheritdoc />
    public override void Complete(Exception? exception = null)
    {
    }

    private ReadOnlySequence<byte> Current()
    {
        var parts = new List<ReadOnlyMemory<byte>>();
        foreach (ReadOnlyMemory<byte> part in _buffered)
            if (!part.IsEmpty)
                parts.Add(part);
        _last = Segments.Join(parts);
        return _last;
    }
}
