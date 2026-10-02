# ProtoStream

Describe a network protocol once — framing, messages, state machine, limits, lifecycle — and run it
over any `Stream` or `IDuplexPipe`. The framework drives the connection; your code sees only the messages
the protocol hands to it.

ProtoStream is part of the [Axiom](https://github.com/DeadMoon0/Axiom) family; its packages and namespaces
are `Axiom.ProtoStream*`.

```
Axiom.ProtoStream/            core
Axiom.ProtoStream.Testing/    test harnesses for protocol authors
Protocols/                    protocol implementations (Http, WebSockets)
UnitTests/                    one test project per project, same layout
Benchmarks/, Samples/
```

| Package | What it is |
|---|---|
| `Axiom.ProtoStream` | Core: describer, framing, `Wire` serialization, connections, sessions, protocol switching. |
| `Axiom.ProtoStream.Http` | HTTP/1.1 server role (RFC 9110, 9112): zero-copy request heads, streamed bodies, keep-alive, pipelining, upgrades, CONNECT. |
| `Axiom.ProtoStream.WebSockets` | WebSockets (RFC 6455) and the HTTP upgrade into them. |
| `Axiom.ProtoStream.Testing` | Harnesses for protocol authors: round-trips at every split, fuzzing, lint assertions, in-memory transports. |

Targets .NET 8 and .NET 10. Trimming and Native AOT compatible. No dependencies beyond `System.IO.Pipelines`.

## Quickstart: a binary protocol

```csharp
public abstract class ChatMessage;

public sealed class Say : ChatMessage, IWireMessage<Say>
{
    public required string Text { get; init; }

    public static bool TryRead(ref WireReader reader, out Say message)
    {
        message = new Say { Text = reader.String(LengthPrefix.UInt16BigEndian, maxBytes: 1024) };
        return reader.Ok;
    }

    public void Write(ref WireWriter writer) => writer.String(LengthPrefix.UInt16BigEndian, Text, maxBytes: 1024);
}

public sealed class Ping : ChatMessage, IWireMessage<Ping> { /* ... */ }
public sealed class Pong : ChatMessage, IWireMessage<Pong> { /* ... */ }

ProtocolDefinition<ChatMessage, ChatMessage> chat = Protocol.Describe<ChatMessage, ChatMessage>("chat/1")
    .Framing(Framers.LengthPrefixed(LengthPrefix.UInt32BigEndian, maxFrameSize: 64 * 1024))
    .Messages(m => m.Add<Say>(1).Add<Ping>(2).Add<Pong>(3))
    .States(s => s
        .Start("Open")
        .In("Open")
            .On<Say>().Delegate()                          // handed to your loop
            .On<Ping>().Respond(ping => new Pong())        // answered by the framework
            .OnSend<Say>())
    .Limits(l => l.IdleTimeout(TimeSpan.FromMinutes(5)))
    .Build();                                              // throws listing every problem in the description

await using Connection connection = Connection.FromStream(networkStream);
Session<ChatMessage, ChatMessage> session = await connection.OpenAsync(chat, cancellationToken);

await foreach (ChatMessage message in session.Messages.WithCancellation(cancellationToken))
    await session.WriteAsync(message, cancellationToken);  // echo
```

## Quickstart: HTTP that upgrades to WebSocket

```csharp
await using Connection connection = Connection.FromStream(new NetworkStream(socket, ownsSocket: true));
Session<HttpRequest, HttpResponse> http = await connection.OpenAsync(Http11.Server(), ct);

await foreach (HttpRequest request in http.Messages.WithCancellation(ct))
{
    if (WebSocket.IsUpgrade(request) && WebSocket.TryAccept(request, new WebSocketAcceptOptions(), out var accept, out _))
    {
        Session<WsMessage, WsMessage> ws = await http.SwitchAsync(accept!, ct);
        await foreach (WsMessage message in ws.Messages.WithCancellation(ct))
            await ws.WriteAsync(message, ct);
        return;
    }

    await http.WriteAsync(HttpResponse.Text(HttpStatus.Ok, "hello"), ct);
}
```

Two complete servers:

- [Samples/Showcase](Samples/Showcase): **ProtoStream Live**, a web app with pages, a JSON API (GET and POST),
  streamed uploads, a shared canvas over binary WebSocket frames, chat, live telemetry and latency probes.
- [Samples/WebSocketEcho](Samples/WebSocketEcho): the minimal HTTP-to-WebSocket echo used for the Autobahn
  conformance run.

## Documentation

- [Architecture](Documentation/Arc42.md): layers, runtime, memory model, decisions.
- [Spec compliance](Documentation/COMPLIANCE.md): every RFC requirement and the test that proves it.
- [Security](Documentation/SECURITY.md): threat model, defaults, what the host still has to do.
- [Errors](Documentation/ERROR-HANDLING.md): what each exception means and what to do.

## Status

Preview (0.x). The public API may still change between minor versions.
