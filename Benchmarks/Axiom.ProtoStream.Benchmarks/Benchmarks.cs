using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Framing;
using Axiom.ProtoStream.Http;
using Axiom.ProtoStream.WebSockets;

namespace Axiom.ProtoStream.Benchmarks;

/// <summary>A pooled message over a frame: what a zero-copy codec hands out.</summary>
public sealed class Chunk
{
    public ReadOnlyMemory<byte> Memory { get; private set; }

    internal void Set(ReadOnlyMemory<byte> memory) => Memory = memory;
}

internal sealed class ChunkCodec : IFrameCodec<Chunk, Chunk>
{
    private readonly Chunk _pooled = new();

    public DecodeResult Decode(in Frame frame, out Chunk message)
    {
        _pooled.Set(frame.Memory);
        message = _pooled;
        return DecodeResult.Ok;
    }

    public void Encode(Chunk message, IBufferWriter<byte> output) => output.Write(message.Memory.Span);
}

/// <summary>
/// The session's overhead over a hand-written pipe loop: both read the same length-prefixed frames from an
/// in-memory pipe. The difference is the framework (state lookup, contract checks, timer, stamps).
/// </summary>
[MemoryDiagnoser]
public class FramedReadBenchmarks
{
    private const int Frames = 1_000;
    private const int PayloadSize = 32;

    private static readonly IFramer Framer = Framers.LengthPrefixed(LengthPrefix.UInt16BigEndian, maxFrameSize: 4096);

    private static readonly ProtocolDefinition<Chunk, Chunk> Definition = Protocol.Describe<Chunk, Chunk>("bench")
        .Framing(Framer)
        .FrameCodec(() => new ChunkCodec())
        .States(s => s.Start("Open").In("Open").On<Chunk>().Delegate())
        .Build();

    private byte[] _input = [];

    [GlobalSetup]
    public void Setup()
    {
        var writer = new ArrayBufferWriter<byte>();
        byte[] payload = new byte[PayloadSize];
        for (int i = 0; i < Frames; i++)
            Framer.WriteFrame(payload, writer);
        _input = writer.WrittenSpan.ToArray();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Frames)]
    public async Task<long> RawPipeLoop()
    {
        Pipe pipe = await FilledPipeAsync();
        long total = 0;
        while (true)
        {
            ReadResult read = await pipe.Reader.ReadAsync();
            ReadOnlySequence<byte> buffer = read.Buffer;
            while (Framer.TryReadFrame(buffer, out ReadOnlySequence<byte> frame, out SequencePosition consumed) == FrameStatus.Complete)
            {
                total += frame.Length;
                buffer = buffer.Slice(consumed);
            }

            pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
            if (read.IsCompleted)
                return total;
        }
    }

    [Benchmark(OperationsPerInvoke = Frames)]
    public async Task<long> Session()
    {
        Pipe pipe = await FilledPipeAsync();
        await using Connection connection = Connection.FromPipe(new Duplex(pipe.Reader, new Pipe().Writer));
        Session<Chunk, Chunk> session = await connection.OpenAsync(Definition, CancellationToken.None);
        long total = 0;
        await foreach (Chunk chunk in session.Messages)
            total += chunk.Memory.Length;
        return total;
    }

    private async Task<Pipe> FilledPipeAsync()
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        await pipe.Writer.WriteAsync(_input);
        await pipe.Writer.CompleteAsync();
        return pipe;
    }

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}

/// <summary>Parsing alone: a typical browser request head, and a masked WebSocket text frame.</summary>
[MemoryDiagnoser]
public class ParseBenchmarks
{
    private static readonly byte[] BrowserRequest = Encoding.ASCII.GetBytes(
        "GET /index.html?lang=en HTTP/1.1\r\nHost: example.org\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\n" +
        "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8\r\nAccept-Language: en-US,en;q=0.5\r\n" +
        "Accept-Encoding: gzip, deflate, br\r\nConnection: keep-alive\r\nUpgrade-Insecure-Requests: 1\r\n\r\n");

    private static readonly byte[] MaskedText = [0x81, 0x85, 0x37, 0xFA, 0x21, 0x3D, 0x7F, 0x9F, 0x4D, 0x51, 0x58];

    private CodecRunner<HttpRequest, HttpResponse> _http = null!;
    private CodecRunner<WsMessage, WsMessage> _ws = null!;

    [GlobalSetup]
    public void Setup()
    {
        _http = new CodecRunner<HttpRequest, HttpResponse>(Http11.Server());
        _ws = new CodecRunner<WsMessage, WsMessage>(WebSocket.Server());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _http.Dispose();
        _ws.Dispose();
    }

    [Benchmark]
    public DecodeStatus HttpRequestHead() => _http.Decode(new ReadOnlySequence<byte>(BrowserRequest), isCompleted: false).Status;

    [Benchmark]
    public DecodeStatus WebSocketMaskedTextFrame() => _ws.Decode(new ReadOnlySequence<byte>(MaskedText), isCompleted: false).Status;
}
