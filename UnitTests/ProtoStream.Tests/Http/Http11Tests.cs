using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ProtoStream.Errors;
using ProtoStream.Http;
using ProtoStream.Testing;

namespace ProtoStream.Tests.Http;

public sealed partial class Http11Tests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>The date of RFC 9110's own IMF-fixdate example, so responses are byte-exact.</summary>
    internal static readonly FakeTimeProvider Clock = new(new DateTimeOffset(1994, 11, 6, 8, 49, 37, TimeSpan.Zero));

    internal const string DateField = "Date: Sun, 06 Nov 1994 08:49:37 GMT\r\n";

    private sealed class HttpPair : IAsyncDisposable
    {
        private readonly StringBuilder _received = new();

        private HttpPair(TransportPair transport, Connection connection, Session<HttpRequest, HttpResponse> session)
        {
            Transport = transport;
            Connection = connection;
            Session = session;
        }

        public TransportPair Transport { get; }

        public Connection Connection { get; }

        public Session<HttpRequest, HttpResponse> Session { get; }

        public static async Task<HttpPair> OpenAsync(Http11Options? options = null)
        {
            TransportPair transport = InMemoryTransport.CreatePair();
            Connection connection = Connection.FromPipe(transport.Server);
            Session<HttpRequest, HttpResponse> session = await connection.OpenAsync(Http11.Server(options ?? new Http11Options { TimeProvider = Clock }), Ct);
            return new HttpPair(transport, connection, session);
        }

        public async Task SendAsync(string text) => await Transport.Client.Output.WriteAsync(Encoding.Latin1.GetBytes(text));

        /// <summary>Reads what the server sent until <paramref name="done"/> holds for everything received so far.</summary>
        public async Task<string> ReceiveAsync(Func<string, bool> done)
        {
            while (!done(_received.ToString()))
            {
                ReadResult read = await Transport.Client.Input.ReadAsync();
                _received.Append(Encoding.Latin1.GetString(read.Buffer.ToArray()));
                Transport.Client.Input.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted)
                    break;
            }

            string all = _received.ToString();
            _received.Clear();
            return all;
        }

        public Task<string> ReceiveResponseAsync() => ReceiveAsync(text => text.Contains("\r\n\r\n", StringComparison.Ordinal) && BodyComplete(text));

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

        private static bool BodyComplete(string text)
        {
            int headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string head = text[..headEnd];
            int lengthAt = head.IndexOf("Content-Length: ", StringComparison.Ordinal);
            if (lengthAt < 0)
                return !head.Contains("chunked", StringComparison.Ordinal) || text.EndsWith("0\r\n\r\n", StringComparison.Ordinal);
            int length = int.Parse(head[(lengthAt + 16)..].Split("\r\n")[0]);
            return text.Length - headEnd - 4 >= length;
        }
    }

    private static async Task<string> ReadBodyAsync(PipeReader body)
    {
        var all = new List<byte>();
        while (true)
        {
            ReadResult read = await body.ReadAsync();
            all.AddRange(read.Buffer.ToArray());
            body.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                return Encoding.ASCII.GetString([.. all]);
        }
    }

    [Fact]
    public async Task ARequestHeadIsParsedAndTheResponseFramedByTheFramework()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET /items?id=7 HTTP/1.1\r\nHost: example.org\r\nX-Trace:  abc \r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;
        Assert.Equal(HttpRequestMethod.Get, request.Method);
        Assert.Equal("/items?id=7", request.Target);
        Assert.Equal(HttpProtocolVersion.Http11, request.Version);
        Assert.True(request.Headers.TryGetValue("x-trace", out string trace));
        Assert.Equal("abc", trace);
        Assert.True(request.KeepAlive);
        Assert.Equal("", await ReadBodyAsync(request.Body));

        await http.Session.WriteAsync(HttpResponse.Text(200, "hi"), Ct);
        Assert.Equal(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n" + DateField + "Content-Length: 2\r\n\r\nhi",
            await http.ReceiveResponseAsync());
    }

    [Fact]
    public async Task AContentLengthBodyIsReadFromTheConnection()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("POST /upload HTTP/1.1\r\nHost: x\r\nContent-Length: 11\r\n\r\nhello world");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        Assert.Equal(11, request.ContentLength);
        Assert.Equal("hello world", await ReadBodyAsync(request.Body));
    }

    private const string ChunkedPost =
        "POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n5;name=value\r\nhello\r\n6\r\n world\r\n0\r\nChecksum: 1\r\n\r\n";

    [Fact]
    public async Task AChunkedBodyIsDecodedWithExtensionsAndTrailersAndTheNextRequestFollows()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync(ChunkedPost + "GET /next HTTP/1.1\r\nHost: x\r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;
        Assert.True(request.IsChunked);
        Assert.Equal("hello world", await ReadBodyAsync(request.Body));

        await http.Session.WriteAsync(HttpResponse.Status(204), Ct);
        Assert.Equal("/next", (await http.Session.ReadAsync(Ct)).Message!.Target);
    }

    [Fact]
    public async Task AChunkedBodyArrivingOneByteAtATimeIsComplete()
    {
        byte[] wire = Encoding.ASCII.GetBytes(ChunkedPost);
        var transport = new ScriptedTransport(wire.Select(b => (ReadOnlyMemory<byte>)new[] { b }));
        await using Connection connection = Connection.FromPipe(transport);
        Session<HttpRequest, HttpResponse> session = await connection.OpenAsync(Http11.Server(), Ct);

        HttpRequest request = (await session.ReadAsync(Ct)).Message!;

        Assert.Equal("hello world", await ReadBodyAsync(request.Body));
    }

    [Fact]
    public async Task PipelinedRequestsAreAnsweredInOrder()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET /1 HTTP/1.1\r\nHost: x\r\n\r\nGET /2 HTTP/1.1\r\nHost: x\r\n\r\n");

        foreach (string expected in new[] { "/1", "/2" })
        {
            HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;
            Assert.Equal(expected, request.Target);
            await http.Session.WriteAsync(HttpResponse.Text(200, request.Target), Ct);
            Assert.EndsWith(expected, await http.ReceiveResponseAsync());
        }
    }

    [Fact]
    public async Task AnHttp10RequestClosesTheConnectionAfterTheResponse()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.0\r\n\r\n");

        await http.Session.ReadAsync(Ct);
        await http.Session.WriteAsync(HttpResponse.Text(200, "bye"), Ct);

        Assert.Contains("Connection: close\r\n", await http.ReceiveResponseAsync());
        Assert.Equal(SessionStatus.Closed, http.Session.Status);
        Assert.True((await http.Session.ReadAsync(Ct)).IsCompleted);
    }

    [Fact]
    public async Task AnHttp10RequestAskingForKeepAliveIsKeptAlive()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.0\r\nConnection: Keep-Alive\r\n\r\n");

        await http.Session.ReadAsync(Ct);
        await http.Session.WriteAsync(HttpResponse.Status(204), Ct);

        Assert.Contains("Connection: keep-alive\r\n", await http.ReceiveResponseAsync());
        Assert.Equal(SessionStatus.Open, http.Session.Status);
    }

    // Every case here is something two HTTP parsers could read differently: the basis of request smuggling.
    [Theory]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\nTransfer-Encoding: chunked\r\n\r\n", 400)]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\nContent-Length: 4\r\n\r\n", 400)]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: +3\r\n\r\n", 400)]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 3, 3\r\n\r\n", 400)]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: gzip, chunked\r\n\r\n", 501)]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n\r\n", 400)]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked, gzip\r\n\r\n", 400)]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: gzip\r\nTransfer-Encoding: chunked\r\n\r\n", 501)]
    [InlineData("POST / HTTP/1.0\r\nTransfer-Encoding: chunked\r\n\r\n", 400)]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\nX-Folded: a\r\n b\r\n\r\n", 400)]
    [InlineData("GET / HTTP/1.1\r\nHost : x\r\n\r\n", 400)]
    [InlineData("GET / HTTP/1.1\r\nX: y\r\n\r\n", 400)]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n", 400)]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\nX: a\u0001b\r\n\r\n", 400)]
    [InlineData("GET /a b HTTP/1.1\r\nHost: x\r\n\r\n", 400)]
    [InlineData("GET /é HTTP/1.1\r\nHost: x\r\n\r\n", 400)]
    [InlineData("G(T / HTTP/1.1\r\nHost: x\r\n\r\n", 400)]
    [InlineData("GET / HTTX/1.1\r\nHost: x\r\n\r\n", 400)]
    [InlineData("GET / HTTP/2.0\r\nHost: x\r\n\r\n", 505)]
    public async Task AnAmbiguousOrMalformedRequestIsRefusedAndTheConnectionClosed(string request, int status)
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync(request);

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => http.Session.ReadAsync(Ct).AsTask());

        Assert.Equal(status, ex.Violation.ProtocolErrorCode);
        string response = await http.ReceiveResponseAsync();
        Assert.StartsWith($"HTTP/1.1 {status} ", response);
        Assert.Contains("Connection: close\r\n", response);
    }

    [Fact]
    public async Task TooManyHeaderFieldsGet431()
    {
        await using HttpPair http = await HttpPair.OpenAsync(new Http11Options { MaxHeaderCount = 3 });
        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\nA: 1\r\nB: 2\r\nC: 3\r\n\r\n");

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => http.Session.ReadAsync(Ct).AsTask());
        Assert.Equal(431, ex.Violation.ProtocolErrorCode);
    }

    [Fact]
    public async Task AHeadLargerThanTheLimitGets431BeforeItEnds()
    {
        await using HttpPair http = await HttpPair.OpenAsync(new Http11Options { MaxRequestHeadBytes = 1024 });
        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\nX: " + new string('a', 2000));

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => http.Session.ReadAsync(Ct).AsTask());
        Assert.Equal(431, ex.Violation.ProtocolErrorCode);
    }

    [Fact]
    public async Task ADeclaredBodyAboveTheLimitGets413WithoutReadingIt()
    {
        await using HttpPair http = await HttpPair.OpenAsync(new Http11Options { MaxRequestBodySize = 10 });
        await http.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 11\r\n\r\n");

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => http.Session.ReadAsync(Ct).AsTask());
        Assert.Equal(413, ex.Violation.ProtocolErrorCode);
    }

    [Fact]
    public async Task ARequestLineAboveTheLimitGets414()
    {
        await using HttpPair http = await HttpPair.OpenAsync(new Http11Options { MaxRequestLineBytes = 32 });
        await http.SendAsync("GET /" + new string('a', 40) + " HTTP/1.1\r\nHost: x\r\n\r\n");

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => http.Session.ReadAsync(Ct).AsTask());
        Assert.Equal(414, ex.Violation.ProtocolErrorCode);
    }

    [Fact]
    public async Task AChunkedBodyAboveTheLimitFailsWhileReadingIt()
    {
        await using HttpPair http = await HttpPair.OpenAsync(new Http11Options { MaxRequestBodySize = 8 });
        await http.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n5\r\nworld\r\n0\r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ReadBodyAsync(request.Body));
        Assert.Equal(ViolationCode.LimitExceeded, ex.Violation.Code);
    }

    [Fact]
    public async Task AHeadResponseAnnouncesTheLengthButSendsNoBody()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("HEAD / HTTP/1.1\r\nHost: x\r\n\r\n");
        await http.Session.ReadAsync(Ct);

        await http.Session.WriteAsync(HttpResponse.Text(200, "hello"), Ct);

        Assert.EndsWith("Content-Length: 5\r\n\r\n", await http.ReceiveAsync(text => text.EndsWith("\r\n\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AStreamOfUnknownLengthIsChunked()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        await http.Session.ReadAsync(Ct);

        await http.Session.WriteAsync(HttpResponse.FromStream(200, new MemoryStream("hello"u8.ToArray()), null, "text/plain"), Ct);

        Assert.Equal(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n" + DateField + "Transfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n",
            await http.ReceiveResponseAsync());
    }

    [Fact]
    public async Task ANoContentResponseHasNoLengthAndRefusesABody()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        await http.Session.ReadAsync(Ct);

        await Assert.ThrowsAsync<ProtocolStateException>(() => http.Session.WriteAsync(HttpResponse.Text(204, "x"), Ct).AsTask());
        await http.Session.WriteAsync(HttpResponse.Status(204), Ct);

        Assert.Equal("HTTP/1.1 204 No Content\r\n" + DateField + "\r\n", await http.ReceiveAsync(text => text.EndsWith("\r\n\r\n", StringComparison.Ordinal)));
    }

    // A CR or LF in a header value would let the application inject a second response.
    [Fact]
    public void HeaderInjectionAndFramingFieldsAreRefusedWhenAdded()
    {
        var response = new HttpResponse(200);

        Assert.Throws<ProtocolStateException>(() => response.Headers.Add("X", "a\r\nSet-Cookie: evil"));
        Assert.Throws<ProtocolStateException>(() => response.Headers.Add("Bad Name", "v"));
        Assert.Throws<ProtocolStateException>(() => response.Headers.Add("Content-Length", "5"));
        Assert.Throws<ProtocolStateException>(() => new HttpResponse(200) { ReasonPhrase = "OK\r\nX: y" });
        Assert.Equal(0, response.Headers.Count);
    }

    [Fact]
    public async Task UpgradeRequestsAreRecognised()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET /chat HTTP/1.1\r\nHost: x\r\nConnection: keep-alive, Upgrade\r\nUpgrade: websocket\r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        Assert.True(request.IsUpgradeRequest);
        Assert.True(request.Headers.ContainsToken("upgrade", "WebSocket"));
    }

    [Fact]
    public async Task ARequestUsedAfterTheNextReadThrowsAndARetainedCopySurvives()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET /first HTTP/1.1\r\nHost: x\r\n\r\nGET /second HTTP/1.1\r\nHost: x\r\n\r\n");

        HttpRequest first = (await http.Session.ReadAsync(Ct)).Message!;
        HttpRequest kept = first.Retain();
        await http.Session.WriteAsync(HttpResponse.Status(204), Ct);
        await http.Session.ReadAsync(Ct);

        Assert.Throws<StaleMessageException>(() => first.Target);
        Assert.Equal("/first", kept.Target);
        Assert.True(kept.Headers.TryGetValue("Host", out string host) && host == "x");
    }

    // ---- harness checks ------------------------------------------------------------------------

    private static readonly byte[][] Samples =
    [
        Encoding.ASCII.GetBytes("GET /a HTTP/1.1\r\nHost: x\r\nAccept: */*\r\n\r\n"),
        Encoding.ASCII.GetBytes("POST /b HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhello"),
        Encoding.ASCII.GetBytes(ChunkedPost),
        Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nConnection: keep-alive\r\n\r\n"),
    ];

    [Fact]
    public void TheRequestParserSurvivesMutationFuzzing()
    {
        FuzzReport report = CodecHarness.Fuzz(Http11.Server(), Samples, seed: 42, iterations: 20_000);

        Assert.True(report.Messages > 0 && report.Invalid > 0);
    }

    [Fact]
    public void AReusedRequestCarriesNothingOverFromThePreviousOne()
    {
        static string Describe(HttpRequest r) =>
            $"{r.MethodName} {r.Target} {r.Version} {r.ContentLength} {r.IsChunked} {r.KeepAlive} " +
            string.Join("|", Enumerable.Range(0, r.Headers.Count).Select(i => r.Headers[i].Name + "=" + r.Headers[i].Value));

        MessageReuseContract.Verify(Http11.Server(), Samples[2], Samples[3], Describe);
        MessageReuseContract.Verify(Http11.Server(), Samples[1], Samples[0], Describe);
    }

    [Fact]
    public void TheServerDefinitionHasNoWarnings() => DefinitionAssert.NoWarnings(Http11.Server());

    // ---- allocations ---------------------------------------------------------------------------

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }

    // Reading a request and writing a response allocates nothing per request beyond what the pipes
    // themselves do: the head is parsed in a reused buffer, the request object is reused, the response is
    // the application's (reused here). Measured against a bare pipe loop moving the same bytes.
    [ReleaseFact]
    public async Task ServingPipelinedRequestsAllocatesNothingPerRequest()
    {
        const int Requests = 5_000;
        byte[] request = Encoding.ASCII.GetBytes("GET /index.html HTTP/1.1\r\nHost: example.org\r\nUser-Agent: test\r\nAccept: */*\r\n\r\n");
        var unbounded = new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false);

        Pipe rawIn = new(unbounded), rawOut = new(unbounded), input = new(unbounded), output = new(unbounded);
        for (int i = 0; i < Requests; i++)
        {
            rawIn.Writer.Write(request);
            input.Writer.Write(request);
        }

        await rawIn.Writer.FlushAsync();
        await input.Writer.FlushAsync();

        HttpResponse response = HttpResponse.Text(200, "hello");
        byte[] responseBytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n" + DateField + "Content-Length: 5\r\n\r\nhello");
        await using Connection connection = Connection.FromPipe(new Duplex(input.Reader, output.Writer));
        Session<HttpRequest, HttpResponse> session = await connection.OpenAsync(Http11.Server(), Ct);

        async Task ServeAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await session.ReadAsync(Ct);
                await session.WriteAsync(response, Ct);
                if (output.Reader.TryRead(out ReadResult sent))
                    output.Reader.AdvanceTo(sent.Buffer.End);
            }
        }

        async Task RawAsync(int count)
        {
            for (int i = 0; i < count; i++)
            {
                ReadResult read = await rawIn.Reader.ReadAsync();
                rawIn.Reader.AdvanceTo(read.Buffer.GetPosition(request.Length));
                rawOut.Writer.Write(responseBytes);
                await rawOut.Writer.FlushAsync();
                if (rawOut.Reader.TryRead(out ReadResult sent))
                    rawOut.Reader.AdvanceTo(sent.Buffer.End);
            }
        }

        await ServeAsync(100);
        await RawAsync(100);

        long before = GC.GetAllocatedBytesForCurrentThread();
        await RawAsync(Requests - 100);
        long pipes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        await ServeAsync(Requests - 100);
        long served = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(served - pipes < 2048, $"Serving allocated {served} bytes where the bare pipes allocated {pipes}.");
    }
}
