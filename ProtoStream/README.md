# ProtoStream

Describe a network protocol once — framing, messages, state machine, limits, lifecycle — and run it over any
`Stream` or `IDuplexPipe`.

```csharp
ProtocolDefinition<ChatMessage, ChatMessage> chat = Protocol.Describe<ChatMessage, ChatMessage>("chat/1")
    .Framing(Framers.LengthPrefixed(LengthPrefix.UInt32BigEndian, maxFrameSize: 64 * 1024))
    .Messages(m => m.Add<Say>(1).Add<Ping>(2).Add<Pong>(3))
    .States(s => s
        .Start("Open")
        .In("Open")
            .On<Say>().Delegate()
            .On<Ping>().Respond(ping => new Pong())
            .OnSend<Say>())
    .Build();

await using Connection connection = Connection.FromStream(stream);
Session<ChatMessage, ChatMessage> session = await connection.OpenAsync(chat, ct);
await foreach (ChatMessage message in session.Messages.WithCancellation(ct))
    await session.WriteAsync(message, ct);
```

## Core concepts

- **Definition**: built once, validated at `Build()` (every problem listed at once, plus lint warnings), thread-safe.
- **Connection**: owns the transport and the single write lock. Dispose it when the loop ends.
- **Session**: one protocol on a connection. Read from one loop, write from anywhere. `SwitchAsync` hands the
  connection to the next protocol, including bytes the peer already sent.
- **Framing**: length-prefixed (fixed, varint, QUIC varint, hex), delimited, fixed-size, header-declared length,
  Content-Length headers. Every framer has a required maximum.
- **Wire**: `WireReader` / `WireWriter` for binary fields; bounded, never throwing on input.
- **Limits**: timeouts, buffer and drain limits, a budget for automatic replies. Turning one off is a named
  opt-out recorded as a definition warning.

The steady state reads without allocating, and the framework never schedules work on its own.

See the [repository](https://github.com/DeadMoon0/ProtoStream) for the architecture, security and error docs.
