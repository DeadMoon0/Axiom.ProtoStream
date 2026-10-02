using System;
using System.Threading.Tasks;
using Axiom.ProtoStream.Errors;

namespace Axiom.ProtoStream.Http.Tests;

/// <summary>
/// A request's memory goes back to pools other connections rent from. Every way to reach it after its lifetime
/// must throw instead of showing whatever the memory holds by then.
/// </summary>
public sealed partial class Http11Tests
{
    [Fact]
    public async Task ARequestTurnsStaleWhenItsSessionSwitchesProtocols()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET /chat HTTP/1.1\r\nHost: x\r\nConnection: upgrade\r\nUpgrade: websocket\r\nCookie: a=1\r\n\r\n");
        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;
        HttpHeader cookie = request.Headers[3];

        await http.Session.SwitchAsync(HttpResponse.SwitchingProtocols("websocket"), Raw.Definition, Ct);

        Assert.Throws<StaleMessageException>(() => request.Path);
        Assert.Throws<StaleMessageException>(() => request.Headers);
        Assert.Throws<StaleMessageException>(() => cookie.ValueBytes);
    }

    [Fact]
    public async Task ARequestTurnsStaleWhenItsConnectionIsDisposed()
    {
        HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET /a HTTP/1.1\r\nHost: x\r\n\r\n");
        HttpRequest request = (await http.Session.ReadAsync(Ct)).Message!;
        await http.Transport.Client.Output.CompleteAsync();

        await http.DisposeAsync();

        Assert.Throws<StaleMessageException>(() => request.Path);
    }

    // The header struct is a copy the application can keep; its byte views must not outlive the request.
    [Fact]
    public async Task AHeaderKeptPastTheNextRequestThrows()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync("GET /a HTTP/1.1\r\nHost: x\r\n\r\nGET /b HTTP/1.1\r\nHost: y\r\n\r\n");
        HttpHeader host = (await http.Session.ReadAsync(Ct)).Message!.Headers[0];
        Assert.Equal("x", host.Value);
        await http.Session.WriteAsync(HttpResponse.Status(HttpStatus.NoContent), Ct);

        await http.Session.ReadAsync(Ct);

        Assert.Throws<StaleMessageException>(() => host.Value);
        Assert.Throws<StaleMessageException>(() => host.NameBytes);
    }

    // Behind a proxy that reuses upstream connections, the next request may belong to another user: a body
    // reader kept from the previous request must not read it.
    [Fact]
    public async Task ABodyReaderKeptPastTheNextRequestThrowsInsteadOfReadingTheNextBody()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        await http.SendAsync(
            "POST /a HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\n\r\none" +
            "POST /b HTTP/1.1\r\nHost: x\r\nContent-Length: 6\r\n\r\nsecret");
        HttpRequest first = (await http.Session.ReadAsync(Ct)).Message!;
        System.IO.Pipelines.PipeReader firstBody = first.Body;
        Assert.Equal("one", await ReadBodyAsync(firstBody));
        await http.Session.WriteAsync(HttpResponse.Status(HttpStatus.NoContent), Ct);

        HttpRequest second = (await http.Session.ReadAsync(Ct)).Message!;

        await Assert.ThrowsAsync<StaleMessageException>(() => firstBody.ReadAsync().AsTask());
        Assert.Equal("secret", await ReadBodyAsync(second.Body));
    }
}
