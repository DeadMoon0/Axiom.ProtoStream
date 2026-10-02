using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using ProtoStream.Errors;
using ProtoStream.Http;

namespace ProtoStream.Tests.Http;

/// <summary>One test per requirement of RFC 9110, RFC 9112 and RFC 3986 the server role implements.</summary>
public sealed partial class Http11Tests
{
    // ---- request targets: RFC 9112 section 3.2, RFC 3986 -------------------------------------

    [Fact]
    public async Task AnOriginFormTargetIsSplitIntoPathAndQuery()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET /a/b%20c?x=1&y=/? HTTP/1.1\r\nHost: example.org:8080\r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        Assert.Equal(RequestTargetForm.Origin, request.TargetForm);
        Assert.Equal("/a/b%20c", request.Path);
        Assert.True(request.HasQuery);
        Assert.Equal("x=1&y=/?", request.Query);
        Assert.Equal("example.org:8080", request.Authority);
    }

    // RFC 9112 section 3.2.2: a server MUST accept absolute-form, and the target's authority overrides Host.
    [Fact]
    public async Task AnAbsoluteFormTargetIsAcceptedAndItsAuthorityWinsOverHost()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET http://example.org:8080/a?b HTTP/1.1\r\nHost: other.example\r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        Assert.Equal(RequestTargetForm.Absolute, request.TargetForm);
        Assert.Equal("example.org:8080", request.Authority);
        Assert.Equal("/a", request.Path);
        Assert.Equal("b", request.Query);
    }

    [Fact]
    public async Task AnAsteriskTargetIsAcceptedForOptions()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("OPTIONS * HTTP/1.1\r\nHost: x\r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        Assert.Equal(RequestTargetForm.Asterisk, request.TargetForm);
        Assert.Equal("", request.Path);
    }

    [Theory]
    [InlineData("GET * HTTP/1.1\r\nHost: x\r\n\r\n")] // asterisk only for OPTIONS
    [InlineData("CONNECT /path HTTP/1.1\r\nHost: x\r\n\r\n")] // CONNECT needs authority-form
    [InlineData("CONNECT example.org HTTP/1.1\r\nHost: x\r\n\r\n")] // authority-form needs a port
    [InlineData("GET /a#fragment HTTP/1.1\r\nHost: x\r\n\r\n")] // no fragment in a request target
    [InlineData("GET /a%zz HTTP/1.1\r\nHost: x\r\n\r\n")] // malformed percent-encoding
    [InlineData("GET /a%2 HTTP/1.1\r\nHost: x\r\n\r\n")] // truncated percent-encoding
    [InlineData("GET /a\"b HTTP/1.1\r\nHost: x\r\n\r\n")] // '"' is not a path character
    [InlineData("GET relative/path HTTP/1.1\r\nHost: x\r\n\r\n")] // neither a path nor a URI
    [InlineData("GET http:///a HTTP/1.1\r\nHost: x\r\n\r\n")] // http URI with an empty host
    [InlineData("GET http:/a HTTP/1.1\r\nHost: x\r\n\r\n")] // http URI without an authority
    [InlineData("GET http://user@example.org/ HTTP/1.1\r\nHost: x\r\n\r\n")] // userinfo in an http URI
    [InlineData("GET http://[zz]/ HTTP/1.1\r\nHost: x\r\n\r\n")] // invalid IP literal
    [InlineData("GET / HTTP/1.1\r\nHost: exa mple\r\n\r\n")] // Host with a space
    [InlineData("GET / HTTP/1.1\r\nHost: example.org:http\r\n\r\n")] // non-numeric port
    [InlineData("GET / HTTP/1.1\r\nHost: [::1\r\n\r\n")] // unterminated IP literal
    [InlineData("GET / HTTP/1.1\r\nHost: a/b\r\n\r\n")] // '/' is not a host character
    public async Task AnInvalidTargetOrHostGets400(string request)
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync(request);

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => http.Session.ReadAsync(Ct).AsTask());

        Assert.Equal(HttpStatus.BadRequest, ex.Violation.ProtocolErrorCode);
    }

    [Theory]
    [InlineData("GET //double/slash HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /?only-query HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: \r\n\r\n")] // empty Host is valid (RFC 9110 section 7.2)
    [InlineData("GET / HTTP/1.1\r\nHost: [::1]:8080\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: 192.0.2.1\r\n\r\n")]
    [InlineData("GET /%C3%A9t%C3%A9 HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET urn:isbn:0451450523 HTTP/1.1\r\nHost: x\r\n\r\n")] // generic URI without authority
    [InlineData("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: ,chunked,\r\n\r\n0\r\n\r\n")] // empty list elements are ignored
    public async Task ValidEdgeCasesAreAccepted(string request)
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync(request);

        Assert.NotNull((await http.Session.ReadAsync(Ct)).Message);
    }

    // RFC 9110 section 9.1: methods are case-sensitive.
    [Fact]
    public async Task MethodsAreCaseSensitive()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("get / HTTP/1.1\r\nHost: x\r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        Assert.Equal(HttpRequestMethod.Other, request.Method);
        Assert.Equal("get", request.MethodName);
    }

    // ---- CONNECT: RFC 9110 section 9.3.6 ---------------------------------------------------------

    [Fact]
    public async Task ASuccessfulConnectHandsTheConnectionOverWithoutFramingFields()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("CONNECT example.org:443 HTTP/1.1\r\nHost: example.org:443\r\n\r\ntunnelled");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;
        Assert.Equal(RequestTargetForm.Authority, request.TargetForm);
        Assert.Equal("example.org:443", request.Authority);

        // A 2xx to CONNECT changes the protocol, so a plain write is refused before anything is sent.
        await Assert.ThrowsAsync<ProtocolStateException>(() => http.Session.WriteAsync(HttpResponse.ConnectionEstablished(), Ct).AsTask());

        Session<RawData, RawData> tunnel = await http.Session.SwitchAsync(HttpResponse.ConnectionEstablished(), Raw.Definition, Ct);

        Assert.Equal("HTTP/1.1 200 OK\r\n" + DateField + "\r\n", await http.ReceiveAsync(text => text.EndsWith("\r\n\r\n", StringComparison.Ordinal)));
        Assert.Equal("tunnelled", System.Text.Encoding.ASCII.GetString((await tunnel.ReadAsync(Ct)).Message!.Data.ToArray()));
    }

    // ---- message body length: RFC 9112 section 6 -------------------------------------------------

    [Fact]
    public async Task ChunkExtensionsAreParsedByGrammarIncludingQuotedValues()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n3 ; a = \"q\\\"x\" ;b\r\nabc\r\n0;last\r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        Assert.Equal("abc", await ReadBodyAsync(request.Body));
    }

    [Theory]
    [InlineData("3;=x\r\nabc\r\n0\r\n\r\n")] // extension without a name
    [InlineData("3;a=\"open\r\nabc\r\n0\r\n\r\n")] // unterminated quoted string
    [InlineData("3x\r\nabc\r\n0\r\n\r\n")] // garbage after the size
    [InlineData("3\r\nabcX\r\n0\r\n\r\n")] // chunk data not followed by CRLF
    [InlineData("3\r\nabc\r\n0\r\nBad Trailer\r\n\r\n")] // malformed trailer field
    public async Task AMalformedChunkedBodyFailsWhileReadingIt(string body)
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n" + body);

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => ReadBodyAsync(request.Body));
        Assert.Equal(ViolationCode.Malformed, ex.Violation.Code);
    }

    // ---- Expect: RFC 9110 section 10.1.1 ---------------------------------------------------------

    [Fact]
    public async Task OneHundredContinueIsSentWhenTheBodyIsFirstRead()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;
        Assert.True(request.ExpectsContinue);
        Assert.False(http.Transport.Client.Input.TryRead(out _)); // nothing sent before the body is wanted

        Task<string> body = ReadBodyAsync(request.Body);
        Assert.Equal("HTTP/1.1 100 Continue\r\n\r\n", await http.ReceiveAsync(text => text.EndsWith("\r\n\r\n", StringComparison.Ordinal)));
        await http.SendAsync("hello");
        Assert.Equal("hello", await body);
    }

    [Fact]
    public async Task AFinalResponseWithoutReadingAnExpectedBodyClosesTheConnection()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n");
        await http.Session.ReadAsync(Ct);

        await http.Session.WriteAsync(HttpResponse.Error(HttpStatus.ContentTooLarge), Ct);

        string response = await http.ReceiveResponseAsync();
        Assert.StartsWith("HTTP/1.1 413 ", response);
        Assert.Contains("Connection: close\r\n", response);
        Assert.DoesNotContain("100 Continue", response);
        Assert.Equal(SessionStatus.Closed, http.Session.Status);
    }

    [Fact]
    public async Task AnUnknownExpectationGets417()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\nExpect: something-else\r\n\r\n");

        var ex = await Assert.ThrowsAsync<ProtocolViolationException>(() => http.Session.ReadAsync(Ct).AsTask());
        Assert.Equal(HttpStatus.ExpectationFailed, ex.Violation.ProtocolErrorCode);
    }

    [Fact]
    public async Task ExpectationsAreIgnoredInHttp10()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("POST / HTTP/1.0\r\nContent-Length: 2\r\nExpect: 100-continue\r\n\r\nhi");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;

        Assert.False(request.ExpectsContinue);
        Assert.Equal("hi", await ReadBodyAsync(request.Body));
        Assert.False(http.Transport.Client.Input.TryRead(out _));
    }

    // ---- responses: RFC 9110 sections 6.6.1, 7.8, 15 ---------------------------------------------

    [Fact]
    public async Task AnApplicationDateIsNotDuplicated()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        await http.Session.ReadAsync(Ct);

        var response = new HttpResponse(HttpStatus.NoContent);
        response.Headers.Add("Date", "Mon, 07 Nov 1994 08:49:37 GMT");
        await http.Session.WriteAsync(response, Ct);

        Assert.Equal("HTTP/1.1 204 No Content\r\nDate: Mon, 07 Nov 1994 08:49:37 GMT\r\n\r\n", await http.ReceiveAsync(text => text.EndsWith("\r\n\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task InterimResponsesPrecedeTheFinalOneWithoutDateOrFraming()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        await http.Session.ReadAsync(Ct);

        var hints = new HttpResponse(HttpStatus.EarlyHints);
        hints.Headers.Add("Link", "</style.css>; rel=preload");
        await http.Session.WriteAsync(hints, Ct);
        Assert.Equal(Http11.AwaitResponse, http.Session.State);
        await http.Session.WriteAsync(HttpResponse.Status(HttpStatus.NoContent), Ct);
        Assert.Equal(Http11.AwaitRequest, http.Session.State);

        Assert.Equal(
            "HTTP/1.1 103 Early Hints\r\nLink: </style.css>; rel=preload\r\n\r\nHTTP/1.1 204 No Content\r\n" + DateField + "\r\n",
            await http.ReceiveAsync(text => text.EndsWith(DateField + "\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task NoInterimResponseIsSentToAnHttp10Client()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.0\r\n\r\n");
        await http.Session.ReadAsync(Ct);

        await Assert.ThrowsAsync<ProtocolStateException>(() => http.Session.WriteAsync(new HttpResponse(HttpStatus.Continue), Ct).AsTask());
    }

    [Theory]
    [InlineData(HttpStatus.MethodNotAllowed, "Allow", "GET")]
    [InlineData(HttpStatus.Unauthorized, "WWW-Authenticate", "Basic realm=\"x\"")]
    [InlineData(HttpStatus.ProxyAuthenticationRequired, "Proxy-Authenticate", "Basic realm=\"x\"")]
    [InlineData(HttpStatus.UpgradeRequired, "Upgrade", "websocket")]
    public async Task AStatusThatRequiresAFieldIsRefusedWithoutIt(int status, string field, string value)
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        await http.Session.ReadAsync(Ct);

        await Assert.ThrowsAsync<ProtocolStateException>(() => http.Session.WriteAsync(HttpResponse.Status(status), Ct).AsTask());

        HttpResponse complete = HttpResponse.Status(status);
        complete.Headers.Add(field, value);
        await http.Session.WriteAsync(complete, Ct);
        Assert.StartsWith($"HTTP/1.1 {status} ", await http.ReceiveResponseAsync());
    }

    [Fact]
    public async Task SwitchingProtocolsIsOnlyForAnOfferedProtocolAndOnlyThroughASwitch()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\nConnection: upgrade\r\nUpgrade: websocket\r\n\r\n");
        await http.Session.ReadAsync(Ct);

        await Assert.ThrowsAsync<ProtocolStateException>(() => http.Session.WriteAsync(HttpResponse.SwitchingProtocols("websocket"), Ct).AsTask());
        await Assert.ThrowsAsync<ProtocolStateException>(() => http.Session.SwitchAsync(HttpResponse.SwitchingProtocols("h2c"), Raw.Definition, Ct).AsTask());
        Assert.False(http.Transport.Client.Input.TryRead(out _));

        await http.Session.SwitchAsync(HttpResponse.SwitchingProtocols("websocket"), Raw.Definition, Ct);
        Assert.Equal(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: upgrade\r\n\r\n",
            await http.ReceiveAsync(text => text.EndsWith("\r\n\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SwitchingProtocolsWithoutAnUpgradeRequestIsRefused()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        await http.Session.ReadAsync(Ct);

        await Assert.ThrowsAsync<ProtocolStateException>(() => http.Session.SwitchAsync(HttpResponse.SwitchingProtocols("websocket"), Raw.Definition, Ct).AsTask());
    }
}
