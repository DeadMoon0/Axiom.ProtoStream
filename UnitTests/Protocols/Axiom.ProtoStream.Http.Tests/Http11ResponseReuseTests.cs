using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Testing;

namespace Axiom.ProtoStream.Http.Tests;

/// <summary>Frozen responses shared across requests, and pipelined responses sent with one flush.</summary>
public sealed partial class Http11Tests
{
    private const string Pipelined3 =
        "GET /a HTTP/1.1\r\nHost: x\r\n\r\nGET /b HTTP/1.1\r\nHost: x\r\n\r\nGET /c HTTP/1.1\r\nHost: x\r\n\r\n";

    private const string HiResponse = "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n" + DateField + "Content-Length: 2\r\n\r\nhi";

    /// <summary>Counts the flushes that reach the transport.</summary>
    private sealed class CountingWriter(PipeWriter inner) : PipeWriter
    {
        private int _flushes;

        public int Flushes => Volatile.Read(ref _flushes);

        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;

        public override long UnflushedBytes => inner.UnflushedBytes;

        public override void Advance(int bytes) => inner.Advance(bytes);

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _flushes);
            return inner.FlushAsync(cancellationToken);
        }
    }

    private sealed class CountingPipe(IDuplexPipe inner) : IDuplexPipe
    {
        public PipeReader Input => inner.Input;

        public CountingWriter Writer { get; } = new(inner.Output);

        public PipeWriter Output => Writer;
    }

    private static async Task<int> AnswerPipelinedAsync(Http11Options options, HttpResponse response)
    {
        TransportPair transport = InMemoryTransport.CreatePair();
        var counting = new CountingPipe(transport.Server);
        await using Connection connection = Connection.FromPipe(counting);
        Session<HttpRequest, HttpResponse> session = await connection.OpenAsync(Http11.Server(options), Ct);
        await transport.Client.Output.WriteAsync(Encoding.Latin1.GetBytes(Pipelined3));

        for (int i = 0; i < 3; i++)
        {
            await session.ReadAsync(Ct);
            await session.WriteAsync(response, Ct);
        }

        // The fourth read finds nothing buffered and waits, which is when held-back responses must go out.
        Task<ProtocolReadResult<HttpRequest>> fourth = session.ReadAsync(Ct).AsTask();
        var received = new StringBuilder();
        while (received.Length < 3 * HiResponse.Length)
        {
            ReadResult read = await transport.Client.Input.ReadAsync(Ct);
            received.Append(Encoding.Latin1.GetString(read.Buffer.ToArray()));
            transport.Client.Input.AdvanceTo(read.Buffer.End);
        }

        Assert.Equal(HiResponse + HiResponse + HiResponse, received.ToString());
        await transport.Client.Output.CompleteAsync();
        Assert.True((await fourth).IsCompleted);
        return counting.Writer.Flushes;
    }

    [Fact]
    public async Task AFrozenResponseIsWrittenIdenticallyForEveryRequest()
    {
        HttpResponse frozen = HttpResponse.Text(HttpStatus.Ok, "hi").Freeze();

        Assert.True(frozen.IsFrozen);
        Assert.Equal(3, await AnswerPipelinedAsync(new Http11Options { TimeProvider = Clock }, frozen));
    }

    [Fact]
    public async Task AFrozenResponseStillGetsItsFramingFromEachRequest()
    {
        HttpResponse frozen = HttpResponse.Text(HttpStatus.Ok, "hi").Freeze();
        await using HttpPair http = await HttpPair.OpenAsync();

        await http.SendAsync("HEAD / HTTP/1.1\r\nHost: x\r\n\r\n");
        await http.Session.ReadAsync(Ct);
        await http.Session.WriteAsync(frozen, Ct);
        Assert.Equal(HiResponse[..^2], await http.ReceiveAsync(text => text.EndsWith("\r\n\r\n", StringComparison.Ordinal)));

        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        await http.Session.ReadAsync(Ct);
        await http.Session.WriteAsync(frozen, Ct);
        Assert.Equal(HiResponse.Replace("Content-Length: 2\r\n", "Content-Length: 2\r\nConnection: close\r\n", StringComparison.Ordinal), await http.ReceiveResponseAsync());
    }

    [Fact]
    public void AFrozenResponseRefusesChangesAndAStreamedResponseCannotBeFrozen()
    {
        HttpResponse frozen = HttpResponse.Text(HttpStatus.Ok, "hi").Freeze();

        Assert.Throws<ProtocolStateException>(() => frozen.Headers.Add("X-Late", "1"));
        Assert.Throws<ProtocolStateException>(() => frozen.Headers.Remove("Content-Type"));
        Assert.Same(frozen, frozen.Freeze());
        Assert.Throws<ProtocolStateException>(() => HttpResponse.FromStream(HttpStatus.Ok, new MemoryStream([1]), 1, "application/octet-stream").Freeze());
    }

    // One frozen instance answering on many connections at once must produce the same bytes on each.
    [Fact]
    public async Task AFrozenResponseCanBeSharedByConcurrentSessions()
    {
        HttpResponse frozen = HttpResponse.Text(HttpStatus.Ok, "hi").Freeze();
        var options = new Http11Options { TimeProvider = Clock };

        int[] flushes = await Task.WhenAll(new Func<Task<int>>[8].Select(_ => Task.Run(() => AnswerPipelinedAsync(options, frozen))));

        Assert.All(flushes, count => Assert.Equal(3, count));
    }

    [Fact]
    public async Task CoalescingSendsTheResponsesToPipelinedRequestsWithOneFlush()
    {
        int flushes = await AnswerPipelinedAsync(new Http11Options { TimeProvider = Clock, CoalescePipelinedResponses = true }, HttpResponse.Text(HttpStatus.Ok, "hi"));

        Assert.Equal(1, flushes);
    }

    // The next request is incomplete, so the server waits for it: the held-back response must reach the client
    // first, or a client that waits for responses before sending more would wait forever.
    [Fact]
    public async Task ACoalescedResponseIsFlushedBeforeTheServerWaitsForMoreInput()
    {
        await using HttpPair http = await HttpPair.OpenAsync(new Http11Options { TimeProvider = Clock, CoalescePipelinedResponses = true });
        await http.SendAsync("GET /a HTTP/1.1\r\nHost: x\r\n\r\nGET /b HTTP/1.1\r\nHo");

        await http.Session.ReadAsync(Ct);
        await http.Session.WriteAsync(HttpResponse.Text(HttpStatus.Ok, "hi"), Ct);
        Task<ProtocolReadResult<HttpRequest>> next = http.Session.ReadAsync(Ct).AsTask();

        Assert.Equal(HiResponse, await http.ReceiveResponseAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await http.SendAsync("st: x\r\n\r\n");
        Assert.Equal("/b", (await next).Message!.Path);
    }
}
