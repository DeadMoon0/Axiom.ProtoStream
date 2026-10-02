# Axiom.ProtoStream.Http

The HTTP/1.1 server role for [Axiom.ProtoStream](https://www.nuget.org/packages/Axiom.ProtoStream), following RFC 9110,
RFC 9112 and, for request targets and Host, RFC 3986.

```csharp
Session<HttpRequest, HttpResponse> http = await connection.OpenAsync(Http11.Server(), ct);

await foreach (HttpRequest request in http.Messages.WithCancellation(ct))
{
    if (request.Path == "/upload")
    {
        await using Stream body = request.Body.AsStream();   // streamed straight from the connection
        await SaveAsync(body, ct);
    }

    await http.WriteAsync(HttpResponse.Text(HttpStatus.Ok, "done"), ct);
}
```

- Request heads are parsed in a reused buffer; the request object is reused (valid until the next read; `Retain()` keeps a copy).
- Bodies are read straight from the connection: Content-Length or chunked, bounded, drained when unread.
- `Content-Length`, `Transfer-Encoding`, `Connection` and `Date` are derived by the framework, never contradicting the body.
- Keep-alive, pipelining, `Expect: 100-continue`, interim responses, `Upgrade` (101) and `CONNECT` tunnels.
- Anything RFC 9112 calls ambiguous — the basis of request smuggling — is refused with the right status code.
- Status codes are named constants in `HttpStatus`.

HTTP/2 and HTTP/3 are not part of this package.
