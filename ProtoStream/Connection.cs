using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using ProtoStream.Errors;

namespace ProtoStream;

/// <summary>
/// Owns one transport: its pipes, the single write lock every writer shares, and the clock. Sessions are
/// typed views over a connection; the first is opened with <see cref="OpenAsync"/>, later ones by switching.
/// </summary>
/// <remarks>
/// Dispose the connection when the session loop ends. Disposing closes the current session gracefully
/// (bounded by its close timeout), completes the pipes and disposes the stream(s) unless
/// <see cref="ConnectionOptions.LeaveOpen"/> is set.
/// </remarks>
public sealed class Connection : IAsyncDisposable
{
    private static long s_nextId;

    private readonly Stream? _input;
    private readonly Stream? _output;
    private Stream? _wrapped;
    private int _opened;
    private int _disposed;

    private Connection(IDuplexPipe pipe, Stream? input, Stream? output, ConnectionOptions options)
    {
        Id = Interlocked.Increment(ref s_nextId);
        Options = options;
        Input = pipe.Input;
        Output = pipe.Output;
        _input = input;
        _output = output;
    }

    /// <summary>Process-unique id of the connection, for log correlation.</summary>
    public long Id { get; }

    /// <summary>The options the connection was created with.</summary>
    public ConnectionOptions Options { get; }

    /// <summary>The session currently using the connection, or null before <see cref="OpenAsync"/>.</summary>
    public ISession? Current { get; private set; }

    internal PipeReader Input { get; private set; }

    internal PipeWriter Output { get; private set; }

    internal SemaphoreSlim WriteLock { get; } = new(1, 1);

    /// <summary>A connection over a duplex stream such as a <c>NetworkStream</c> or <c>SslStream</c>.</summary>
    public static Connection FromStream(Stream stream) => FromStream(stream, ConnectionOptions.Default);

    /// <summary>A connection over a duplex stream with the given options.</summary>
    public static Connection FromStream(Stream stream, ConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        return new Connection(CreatePipe(stream, stream, options), stream, stream, options);
    }

    /// <summary>A connection over separate input and output streams, such as a child process's stdout and stdin.</summary>
    public static Connection FromStreams(Stream input, Stream output) => FromStreams(input, output, ConnectionOptions.Default);

    /// <summary>A connection over separate input and output streams with the given options.</summary>
    public static Connection FromStreams(Stream input, Stream output, ConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        return new Connection(CreatePipe(input, output, options), input, output, options);
    }

    /// <summary>A connection over pipes a host already has, such as a socket transport.</summary>
    /// <remarks>
    /// A message is buffered until it is complete, so the input pipe's pause threshold must be larger than the
    /// protocol's <see cref="ProtocolLimits.MaxBufferedBytes"/>. A smaller threshold deadlocks on large messages: the
    /// transport stops writing until bytes are consumed, and the session cannot consume an incomplete message.
    /// </remarks>
    public static Connection FromPipe(IDuplexPipe pipe) => FromPipe(pipe, ConnectionOptions.Default);

    /// <summary>A connection over pipes a host already has, with the given options.</summary>
    public static Connection FromPipe(IDuplexPipe pipe, ConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(options);
        return new Connection(pipe, null, null, options);
    }

    /// <summary>Opens the first session. A connection opens one protocol; later protocols come from switching.</summary>
    public ValueTask<Session<TIn, TOut>> OpenAsync<TIn, TOut>(ProtocolDefinition<TIn, TOut> definition, CancellationToken cancellationToken)
        where TIn : class
        where TOut : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _opened, 1) != 0)
            throw new ProtocolStateException("The connection already has a session.") { Guidance = "Continue with another protocol through Session.SwitchAsync." };
        return OpenSessionAsync(definition, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (Current is IClosableSession { Status: SessionStatus.Open } session)
        {
            try
            {
                await session.CloseForDisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Disposal must release the transport whatever the protocol's close did.
            }
        }

        Input.CancelPendingRead();
        await CompletePipesAsync().ConfigureAwait(false);
        (Current as IClosableSession)?.ReleaseResources();
        if (!Options.LeaveOpen)
            await DisposeStreamsAsync().ConfigureAwait(false);
        WriteLock.Dispose();
    }

    internal async ValueTask<Session<TIn, TOut>> OpenSessionAsync<TIn, TOut>(ProtocolDefinition<TIn, TOut> definition, CancellationToken cancellationToken)
        where TIn : class
        where TOut : class
    {
        var session = new Session<TIn, TOut>(this, definition);
        Current = session;
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    /// <summary>Replaces the transport by a wrapper around it; the old pipes must hold no unread bytes.</summary>
    internal async ValueTask WrapTransportAsync(Func<Stream, CancellationToken, ValueTask<Stream>> wrap, CancellationToken cancellationToken)
    {
        if (_input is null || !ReferenceEquals(_input, _output) || _wrapped is not null)
            throw new ProtocolStateException("Only a connection created from one duplex stream can wrap its transport, and only once.");

        await CompletePipesAsync().ConfigureAwait(false);
        Stream wrapped = await wrap(_input, cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolStateException("The transport wrapper returned no stream.");
        _wrapped = wrapped;
        IDuplexPipe pipe = CreatePipe(wrapped, wrapped, Options);
        Input = pipe.Input;
        Output = pipe.Output;
    }

    private async ValueTask CompletePipesAsync()
    {
        try
        {
            await Output.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The peer may already be gone; completing is best effort.
        }

        try
        {
            await Input.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // As above.
        }
    }

    private async ValueTask DisposeStreamsAsync()
    {
        if (_wrapped is not null)
            await _wrapped.DisposeAsync().ConfigureAwait(false);
        if (_input is not null)
            await _input.DisposeAsync().ConfigureAwait(false);
        if (_output is not null && !ReferenceEquals(_output, _input))
            await _output.DisposeAsync().ConfigureAwait(false);
    }

    // The pipes never own the streams: the connection disposes them, after the pipes are complete.
    private static IDuplexPipe CreatePipe(Stream input, Stream output, ConnectionOptions options)
    {
        MemoryPool<byte> pool = options.Pool ?? MemoryPool<byte>.Shared;
        var reader = PipeReader.Create(input, new StreamPipeReaderOptions(pool: pool, bufferSize: options.MinimumReadSize, leaveOpen: true, useZeroByteReads: true));
        var writer = PipeWriter.Create(output, new StreamPipeWriterOptions(pool, minimumBufferSize: options.MinimumReadSize, leaveOpen: true));
        return new DuplexPipe(reader, writer);
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}

/// <summary>Lets the connection close whatever session is current without knowing its message types.</summary>
internal interface IClosableSession : ISession
{
    ValueTask CloseForDisposeAsync();

    void ReleaseResources();
}
