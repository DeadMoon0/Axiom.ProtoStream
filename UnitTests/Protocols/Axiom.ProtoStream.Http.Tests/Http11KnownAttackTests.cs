using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Testing;

namespace Axiom.ProtoStream.Http.Tests;

/// <summary>
/// Inputs behind published vulnerabilities in other HTTP/1.1 servers (CVE or advisory named per case). Each must
/// be refused, or handled without the effect the attack relies on.
/// </summary>
public sealed partial class Http11Tests
{
    [Theory]
    [InlineData("POST / HTTP/1.1\r\n Content-Length: 5\r\nHost: x\r\n\r\nhello")] // obs-fold on the first field, CVE-2022-32215 (Node)
    [InlineData("GET / HTTP/1.1\r\nHost: x\nContent-Length: 5\r\n\r\nhello")] // bare LF as a line end, CVE-2022-32214 (llhttp)
    [InlineData("GET / HTTP/1.1\r\nHost: x\rX: y\r\n\r\n")] // bare CR, CVE-2023-30589 (Node)
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: \r\n\r\n")] // empty Content-Length, CVE-2021-32715 (hyper)
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 0000000000000000005\r\n\r\nhello")] // 19 digits
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 0x5\r\n\r\nhello")]
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5 5\r\n\r\nhello")]
    [InlineData("GET / HTTP/1.1\r\nHost: x\r\n: x\r\n\r\n")] // empty field name, CVE-2023-25725 (HAProxy)
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked;q=1\r\n\r\n0\r\n\r\n")] // parameterised chunked, HTTP Garden
    [InlineData("GET / HTTP/1.1\r\nHost: good.example,evil.example\r\n\r\n")] // a Host that reads as two
    [InlineData("GET /\r\n\r\n")] // HTTP/0.9, CVE-2017-7656 (Jetty)
    [InlineData("GET  / HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET / HTTP/1.1 \r\nHost: x\r\n\r\n")]
    public async Task APublishedRequestSmugglingOrParsingAttackIsRefusedWith400(string request)
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync(request);

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => http.Session.ReadAsync(Ct).AsTask());

        Assert.Equal(HttpStatus.BadRequest, ex.Violation.ProtocolErrorCode);
        Assert.Contains("Connection: close\r\n", await http.ReceiveResponseAsync());
    }

    [Theory]
    [InlineData("f0000000000000003\r\nabc\r\n0\r\n\r\n", ViolationCode.Malformed)] // chunk size overflow, CVE-2017-7657 (Jetty)
    [InlineData("10000000000000000\r\n", ViolationCode.Malformed)] // CVE-2021-32714 (hyper)
    [InlineData("2;\nxx\r\n10\r\n1234567890abcdef\r\n0\r\n\r\n", ViolationCode.Malformed)] // bare LF in a chunk line, CVE-2025-55315 (Kestrel)
    [InlineData("2\nxx\r\n0\r\n\r\n", ViolationCode.Malformed)] // CVE-2025-22871 (Go)
    [InlineData("2;a=b\rxx\r\n0\r\n\r\n", ViolationCode.Malformed)] // bare CR, CVE-2024-52304 (aiohttp)
    [InlineData("2;a=\"x\ny\"\r\nxx\r\n0\r\n\r\n", ViolationCode.Malformed)] // LF in a quoted extension, CVE-2026-2332 (Jetty)
    [InlineData("5 \r\nhello\r\n0\r\n\r\n", ViolationCode.Malformed)] // whitespace after the size without an extension
    [InlineData("3\r\nabcXX0\r\n\r\n", ViolationCode.Malformed)] // data not followed by CRLF, CVE-2025-43859 (h11)
    public async Task AMalformedChunkedBodyIsAViolation(string body, ViolationCode code)
    {
        ProtocolViolationException ex = await ReadChunkedBodyExpectingViolationAsync(body);

        Assert.Equal(code, ex.Violation.Code);
    }

    [Fact]
    public async Task AnOverlongChunkExtensionIsRefused() // CVE-2024-22019 (Node), CVE-2024-21647 (Puma)
    {
        ProtocolViolationException ex = await ReadChunkedBodyExpectingViolationAsync("1;" + new string('a', 300) + "\r\nX\r\n0\r\n\r\n");

        Assert.Equal(ViolationCode.LimitExceeded, ex.Violation.Code);
    }

    [Fact]
    public async Task AnOversizedTrailerSectionIsRefused() // CVE-2023-46589 (Tomcat)
    {
        ProtocolViolationException ex = await ReadChunkedBodyExpectingViolationAsync("0\r\nX: " + new string('a', 9000) + "\r\n\r\n");

        Assert.Equal(ViolationCode.LimitExceeded, ex.Violation.Code);
    }

    // One data byte per chunk behind a long extension: some 270 wire bytes per body byte, past every limit that
    // counts data only (Go CVE-2023-39326). The framing is now bounded relative to the data.
    [Fact]
    public async Task ChunksThatAreMostlyFramingAreRefused()
    {
        string chunk = "1;" + new string('a', 250) + "\r\nX\r\n";
        ProtocolViolationException ex = await ReadChunkedBodyExpectingViolationAsync(string.Concat(Enumerable.Repeat(chunk, 100)) + "0\r\n\r\n");

        Assert.Equal(ViolationCode.LimitExceeded, ex.Violation.Code);
    }

    [Fact]
    public async Task ChunkExtensionsWithinReasonStillWork()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n5;name=value\r\nhello\r\n6 ; a = \"b c\"\r\n world\r\n0\r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        Assert.Equal("hello world", await ReadBodyAsync(request.Body));
    }

    // The same framing attack against the drain: the application does not read the body, so the session
    // discards it, and the discard must stop at the framing bound as well.
    [Fact]
    public async Task AnUnreadBodyThatIsMostlyFramingIsNotDrainedAtLength()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        string chunk = "1;" + new string('a', 250) + "\r\nX\r\n";
        await http.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n" + string.Concat(Enumerable.Repeat(chunk, 100)) + "0\r\n\r\n");
        await http.Session.ReadAsync(Ct);
        await http.Session.WriteAsync(HttpResponse.Status(HttpStatus.NoContent), Ct);

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => http.Session.ReadAsync(Ct).AsTask());

        Assert.Equal(ViolationCode.LimitExceeded, ex.Violation.Code);
    }

    // CL.0: a server that ignores the body of a GET reads the body as the next request (Kettle 2022).
    [Fact]
    public async Task TheBodyOfAGetIsNeverReadAsTheNextRequest()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        const string Smuggled = "GET /admin HTTP/1.1\r\nHost: x\r\n\r\n";
        await http.SendAsync($"GET / HTTP/1.1\r\nHost: x\r\nContent-Length: {Smuggled.Length}\r\n\r\n{Smuggled}GET /next HTTP/1.1\r\nHost: x\r\n\r\n");

        Assert.Equal("/", (await http.Session.ReadAsync(Ct)).Message!.Path);
        await http.Session.WriteAsync(HttpResponse.Status(HttpStatus.NoContent), Ct);

        Assert.Equal("/next", (await http.Session.ReadAsync(Ct)).Message!.Path);
    }

    // A body that turns out malformed while it is discarded must fail the connection, not leave it reusable
    // with the rest of the bytes read as a request (CVE-2022-22720, Apache httpd).
    [Fact]
    public async Task ABodyThatFailsWhileDiscardedEndsTheConnection()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabcXX0\r\n\r\nGET /admin HTTP/1.1\r\nHost: x\r\n\r\n");
        await http.Session.ReadAsync(Ct);
        await http.Session.WriteAsync(HttpResponse.Status(HttpStatus.NoContent), Ct);

        await Assert.ThrowsAsync<ProtocolViolationException>(() => http.Session.ReadAsync(Ct).AsTask());

        Assert.NotEqual(SessionStatus.Open, http.Session.Status);
        Assert.True((await http.Session.ReadAsync(Ct)).IsCompleted);
    }

    // Remembering how far a head was searched must not change what is parsed: trickled byte by byte, with
    // leading empty lines and a pipelined second request.
    [Fact]
    public async Task AHeadTrickledOneByteAtATimeParsesTheSame()
    {
        byte[] wire = Encoding.ASCII.GetBytes("\r\n\r\nGET /a HTTP/1.1\r\nHost: x\r\nX: " + new string('y', 500) + "\r\n\r\nGET /b HTTP/1.1\r\nHost: x\r\n\r\n");
        var transport = new ScriptedTransport(wire.Select(b => (ReadOnlyMemory<byte>)new[] { b }));
        await using Connection connection = Connection.FromPipe(transport);
        Session<HttpRequest, HttpResponse> session = await connection.OpenAsync(Http11.Server(new Http11Options { TimeProvider = Clock }), Ct);

        HttpRequest first = (await session.ReadAsync(Ct)).Message!;
        Assert.Equal("/a", first.Path);
        Assert.True(first.Headers.TryGetValue("X", out string value));
        Assert.Equal(500, value.Length);
        await session.WriteAsync(HttpResponse.Status(HttpStatus.NoContent), Ct);

        Assert.Equal("/b", (await session.ReadAsync(Ct)).Message!.Path);
    }

    [Fact]
    public void AResponseFieldWithCharactersBeyondLatin1IsRefused() // response splitting through U+010D U+010A, CVE-2018-12116 (Node)
    {
        var response = new HttpResponse(HttpStatus.Ok);

        Assert.Throws<ProtocolStateException>(() => response.Headers.Add("X", "ačĊSet-Cookie: x"));
    }

    private static async Task<ProtocolViolationException> ReadChunkedBodyExpectingViolationAsync(string body)
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n" + body);
        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        return await Assert.ThrowsAsync<ProtocolViolationException>(() => ReadBodyAsync(request.Body));
    }
}
