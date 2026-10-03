![Icon](https://raw.githubusercontent.com/DeadMoon0/Axiom/refs/heads/main/Assets/Icon.svg)

# Axiom.ProtoStream.WebSockets

RFC 6455 WebSockets for [Axiom.ProtoStream](https://www.nuget.org/packages/Axiom.ProtoStream), and the HTTP/1.1 upgrade that
switches an `Axiom.ProtoStream.Http` session into a WebSocket session.

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
- Pings are answered within a budget (a flood closes with 1008), closes are echoed, the close handshake is
  bounded by a timeout; a message's deadline holds while control frames are answered.
- Messages above the size limit are refused from their header, before their bytes arrive.
- Optional keep-alive pings; received messages are pooled and decoded without heap copies.
- Close codes are named constants in `WsCloseCode`.

Extensions such as permessage-deflate are not negotiated.
