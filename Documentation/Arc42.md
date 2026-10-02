# ProtoStream — Architecture (arc42)

## 1. Introduction and goals

ProtoStream lets a protocol author **describe a protocol once** — framing, messages, state machine,
limits, lifecycle — and run that description over any `Stream` or `IDuplexPipe`.

| Goal | Meaning |
|---|---|
| The framework manages, the user decides | The user sees only the messages the protocol hands to them. Auto-replies, state tracking, body draining, timeouts and limits are framework work. |
| Validate early | Everything that can be checked from the description is checked at `Build()`; combinations only known per connection at `OpenAsync`; the rest at the wire. |
| Fast and allocation-free | Zero allocations per message on the read path in steady state; overhead over a raw `PipeReader` loop in the low single-digit percent. |
| Safe by default | The peer is hostile. Every length is bounded, every wait has a deadline, ambiguity is rejected. |
| A good public package | Binary-compatible growth, trimming/AOT-safe, documented, one entry point per package. |

## 2. Constraints

- .NET 8 and .NET 10, `System.IO.Pipelines` as the I/O model.
- No reflection on hot paths, no runtime code generation: trimmable and Native-AOT compatible.
- The framework never schedules work on its own (no `Task.Run`, no background pumps). The task that
  awaits a read drives the session. The single exception is the opt-in heartbeat timer, which only
  sends a framework-defined message under the connection's write lock.
- Public API: no optional parameters, no positional records without `[ClosedShape]`
  (enforced by convention tests).

## 3. Context and scope

ProtoStream ends at "typed message in, typed message out, state and lifecycle handled".
Routing, dependency injection, authentication, request contexts and logging scopes belong to the host
(for example a web server) that wraps each delivered message in its own context.

Out of scope: datagram transports, TLS itself (use `SslStream` through a transport switch),
multiplexed protocols (HTTP/2, QUIC) in v1.

## 4. Solution strategy

Honest layers at the bottom, one dishonest object at the top:

| Layer | Honest? | Types |
|---|---|---|
| Framing | yes | `IFramer`, `LengthPrefix`, built-in framers |
| Codec | yes | `IMessageReader<TIn>`, `IMessageWriter<TOut>`, `ICodec<TIn,TOut>`, `Wire`, `MessageSet` |
| State machine | yes | compiled `StateTable` from `Protocol.Describe(...)...Build()` |
| Policies | yes | limits, timeouts, violation policy — plain data |
| Session | no | `Connection`, `Session<TIn,TOut>` — pumps the pipe through the stack |

Honest layers are tested with plain byte sequences and no sockets. The session is tested with an
in-memory duplex pipe and a fake `TimeProvider`.

## 5. Building blocks

### Definition

```csharp
var echo = Protocol.Describe<EchoMessage, EchoMessage>("echo/1")
    .Framing(f => f.LengthPrefixed(LengthPrefix.UInt16BigEndian, maxFrameSize: 4096))
    .Messages(m => m.Discriminator(Discriminator.UInt8)
        .Add<Hello>(1).Add<Data>(2).Add<Ping>(3).Add<Pong>(4).Add<Bye>(5))
    .States(s => s
        .Start("AwaitHello")
        .In("AwaitHello").On<Hello>().Delegate().GoTo("Open")
        .In("Open")
            .On<Data>().Delegate()
            .On<Ping>().Respond(p => new Pong { Id = p.Id })
            .On<Bye>().Respond(_ => new Bye()).GoTo("Closed")
            .OnSend<Data>()
        .Final("Closed"))
    .Limits(l => l.IdleTimeout(TimeSpan.FromSeconds(30)))
    .Build();
```

The builder is staged: `Build()` is only reachable after a codec and the states were described.
`Build()` compiles the description into an immutable `ProtocolDefinition<TIn,TOut>`:
type ids, per-state transition arrays, per-state readers, limits. It throws a
`ProtocolDefinitionException` listing **all** problems, and records lint findings in
`definition.Warnings`.

### Inbound actions

| Action | Effect |
|---|---|
| `Delegate()` | The message is yielded to the user's `await foreach`. |
| `Respond(f)` | The framework writes `f(message)` and keeps reading; the user never sees it. |
| `Wait()` | The message is consumed silently. |
| `GoTo(state)` | After the action, the session moves to `state`. Entering a `Final` state ends the session gracefully. |

### Runtime

```csharp
await using var conn = Connection.FromStream(stream);
var session = await conn.OpenAsync(echo, ct);

await foreach (var msg in session.Messages.WithCancellation(ct))
    await session.WriteAsync(msg, ct);
```

- `Connection` owns the transport, the single write lock and the time provider.
- `Session<TIn,TOut>` is a typed view over the connection for one protocol.
- `SwitchAsync` writes a final message, ends the current session and opens the next protocol over the
  same pipe. Bytes the peer already sent after the switch point are delivered to the new session.

## 6. Runtime view: one read

```
ReadAsync
 ├ release the previous message: advance the pipe, drain an unread payload (bounded)
 ├ bump the read generation (pooled messages from the previous read become stale)
 ├ arm the message timeout
 ├ loop: pipe.ReadAsync → reader(state).TryParse
 │    NeedMore → check MaxBufferedBytes, wait for more
 │    Invalid  → violation policy (close / close with reply / skip when the reader can resync)
 │    Done     → look up transition by message type
 │               Wait → continue · Respond → write reply, continue · Delegate → return message
 └ disarm the timeout
```

## 7. Cross-cutting concepts

### Memory

- Messages may reference pipe memory until the next read on the session (delayed advance).
- Codecs that need a payload (HTTP bodies) first **detach** the message head into a session-owned
  buffer; only then may they claim the payload. The token returned by `Detach` is required by
  `DoneWithPayload`, so a codec cannot claim a payload while still referencing pipe memory.
- Pooled message objects carry a `MessageStamp`; using one after the next read throws
  `StaleMessageException`. `Retain()` returns an owned copy.

### Errors

| Exception | At fault |
|---|---|
| `ProtocolDefinitionException` | protocol author, at `Build()` |
| `CodecContractException` | codec author, at runtime (connection aborted) |
| `ProtocolViolationException` | the peer (connection closed by policy) |
| `ProtocolStateException` and derived | the calling code (nothing was sent) |

### Safety

See [SECURITY.md](SECURITY.md) for the threat model and defaults.

## 8. Decisions

| Decision | Reason |
|---|---|
| Pull-based sessions consumed with `await foreach` | Natural backpressure, no hidden tasks, the caller owns scheduling. |
| Exceptions for violations | One loud, typed failure per connection; violations are rare. |
| Type-based dispatch compiled to arrays | AOT-safe, allocation-free, no user-written discriminator for in-process messages. |
| Codecs per session from a factory | Removes "codec must be stateless"; codecs may track negotiated state. |
| Detach token for payloads | Encodes "a payload-carrying message must not reference pipe memory". |
