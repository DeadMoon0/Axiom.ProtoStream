# ProtoStream.WebSockets

RFC 6455 WebSockets for [ProtoStream](https://www.nuget.org/packages/ProtoStream), and the HTTP/1.1 upgrade that
switches a `ProtoStream.Http` session into a WebSocket session.

```csharp
if (WebSocket.IsUpgrade(request))
{
    if (!WebSocket.TryAccept(request, new WebSocketAcceptOptions(), out var accept, out HttpResponse? rejection))
    {
        await http.WriteAsync(rejection!, ct);   // 400, or 426 with the supported version
        continue;
    }

    Session<WsMessage, WsMessage> ws = await http.SwitchAsync(accept!, ct);
    await foreach (WsMessage message in ws.Messages.WithCancellation(ct))
    {
        if (message is WsText text)
            await ws.WriteAsync(WsMessage.CreateText(text.Text.ToUpperInvariant()), ct);
    }
}
```

- Server and client roles: masking checked on receive, fresh random keys on send.
- Fragmentation with interleaved control frames, UTF-8 validation of text, close-code validation.
- Pings are answered, closes are echoed, the close handshake is bounded by a timeout.
- Messages above the size limit are refused from their header, before their bytes arrive.
- Optional keep-alive pings; received messages are pooled and decoded without heap copies.
- Close codes are named constants in `WsCloseCode`.

Extensions such as permessage-deflate are not negotiated.
