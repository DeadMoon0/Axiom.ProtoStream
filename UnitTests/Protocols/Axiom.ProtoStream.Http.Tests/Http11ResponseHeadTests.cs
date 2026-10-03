using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Axiom.ProtoStream.Errors;

namespace Axiom.ProtoStream.Http.Tests;

/// <summary>Response heads validated and encoded once, and requests read as bytes without allocating strings.</summary>
public sealed partial class Http11Tests
{
    private static readonly KeyValuePair<string, string>[] TextFields = [new("Content-Type", "text/plain; charset=utf-8")];

    [Fact]
    public async Task AResponseFromAHeadIsWrittenLikeOneBuiltFieldByField()
    {
        HttpResponseHead head = HttpResponseHead.Create(HttpStatus.Ok, TextFields);

        Assert.Equal(3, await AnswerPipelinedAsync(new Http11Options { TimeProvider = Clock }, new HttpResponse(head) { Content = "hi"u8.ToArray() }));
    }

    [Fact]
    public async Task FieldsAddedToAResponseFromAHeadFollowTheHeadAndFramingStaysPerRequest()
    {
        HttpResponseHead head = HttpResponseHead.Create(HttpStatus.Ok, TextFields);
        var response = new HttpResponse(head) { Content = "hi"u8.ToArray(), CloseConnection = true };
        response.Headers.Add("X-Extra", "1");
        await using HttpPair http = await HttpPair.OpenAsync();

        await http.SendAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        await http.Session.ReadAsync(Ct);
        await http.Session.WriteAsync(response, Ct);

        Assert.Equal(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nX-Extra: 1\r\n" + DateField + "Content-Length: 2\r\nConnection: close\r\n\r\nhi",
            await http.ReceiveResponseAsync());
    }

    [Fact]
    public void AHeadRefusesWhatHeadersAddRefuses()
    {
        Assert.Throws<ProtocolStateException>(() => HttpResponseHead.Create(HttpStatus.Ok, [new("Bad Name", "x")]));
        Assert.Throws<ProtocolStateException>(() => HttpResponseHead.Create(HttpStatus.Ok, [new("X-Injected", "a\r\nSet-Cookie: b")]));
        Assert.Throws<ProtocolStateException>(() => HttpResponseHead.Create(HttpStatus.Ok, [new("Content-Length", "1")]));
        Assert.Throws<ProtocolStateException>(() => HttpResponseHead.Create(HttpStatus.Ok, [], "bad\r\nreason"));
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpResponseHead.Create(1000, []));
    }

    [Fact]
    public void AResponseFromAHeadTakesItsReasonPhraseFromTheHead()
    {
        HttpResponseHead head = HttpResponseHead.Create(HttpStatus.Ok, [], "Fine");

        Assert.Equal("Fine", new HttpResponse(head).ReasonPhrase);
        Assert.Throws<ProtocolStateException>(() => new HttpResponse(head) { ReasonPhrase = "Other" });
    }

    // RFC 9110 section 15.5.6: the field a status requires may come from the head.
    [Fact]
    public async Task AFieldTheStatusRequiresCanComeFromTheHead()
    {
        HttpResponseHead head = HttpResponseHead.Create(HttpStatus.MethodNotAllowed, [new("Allow", "GET")]);
        await using HttpPair http = await HttpPair.OpenAsync();

        await http.SendAsync("POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n");
        await http.Session.ReadAsync(Ct);
        await http.Session.WriteAsync(new HttpResponse(head), Ct);

        Assert.Equal("HTTP/1.1 405 Method Not Allowed\r\nAllow: GET\r\n" + DateField + "Content-Length: 0\r\n\r\n", await http.ReceiveResponseAsync());
    }

    [Fact]
    public async Task TheAuthorityAndFieldNamesCanBeReadWithoutStrings()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET / HTTP/1.1\r\nHost: Example.org:8080\r\nX-Token: a\r\n\r\nGET http://other.example/ HTTP/1.1\r\nHost: x\r\n\r\n");

        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;
        Assert.Equal("Example.org:8080", Encoding.ASCII.GetString(request.AuthorityBytes.Span));
        Assert.True(request.Headers[1].NameEquals("x-token"));
        Assert.False(request.Headers[1].NameEquals("x-tokens"));
        await http.Session.WriteAsync(HttpResponse.Status(HttpStatus.NoContent), Ct);

        // RFC 9112 section 3.2.2: the absolute target's authority wins over Host.
        request = (await http.Session.ReadAsync(Ct)).Message!;
        Assert.Equal("other.example", Encoding.ASCII.GetString(request.AuthorityBytes.Span));
    }
}
