# Axiom.ProtoStream — Architecture (arc42)

## 1. Introduction and goals

ProtoStream lets a protocol author **describe a protocol once** — framing, messages, state machine,
limits, lifecycle — and run that description over any `Stream` or `IDuplexPipe`. It is part of the
[Axiom](https://github.com/DeadMoon0/Axiom) family; packages and namespaces are `Axiom.ProtoStream*`.

| Goal | Meaning |
|---|---|
| The framework manages, the user decides | The user sees only the messages the protocol hands over. Automatic replies, state tracking, body draining, timeouts, limits and framing fields are framework work. |
| Validate early | What the description can prove is checked at `Build()`, everything else at the wire. |
| Fast and allocation-free | No per-message allocation on the read path in steady state; measured by Release-only tests and benchmarks. |
| Safe by default | The peer is hostile: every length is bounded, every wait has a deadline, ambiguity is refused. |
| Spec-compliant | Every MUST of the implemented RFCs has a test ([COMPLIANCE.md](COMPLIANCE.md)); WebSockets pass the Autobahn suite. |
| A good public package family | Binary-compatible growth, trimming/AOT-safe, documented, one entry point per package. |

## 2. Constraints

- .NET 8 and .NET 10, `System.IO.Pipelines` as the I/O model, no other dependency.
- No reflection on hot paths, no runtime code generation.
- The framework never schedules work on its own: the task that awaits a read drives the session. The only
  exception is the opt-in heartbeat timer, which sends a framework message under the write lock.
- Public API: no optional parameters, no positional records without `[ClosedShape]` (convention tests in every
  test project). No bare numbers: protocol constants are named (`HttpStatus`, `WsCloseCode`, ...).

## 3. Context and scope

ProtoStream ends at "typed message in, typed message out, state and lifecycle handled". Routing, dependency
injection, authentication and request contexts belong to the host.

Out of scope: datagram transports, TLS itself (use `SslStream`, also as a transport switch), multiplexed
protocols (HTTP/2, HTTP/3), WebSocket extensions.

## 4. Solution strategy

Honest layers at the bottom, one dishonest object at the top:

| Layer | Honest? | Types |
|---|---|---|
| Framing | yes | `IFramer`, `LengthPrefix`, `Framers` |
| Codecs | yes | `IMessageReader<T>`, `IMessageWriter<T>`, `ICodec<TIn,TOut>`, `IFrameCodec`, `Wire*`, message sets |
| State machine | yes | compiled `ProtocolDefinition<TIn,TOut>` |
| Policies | yes | `ProtocolLimits`, violation action and reply |
| Session | no | `Connection`, `Session<TIn,TOut>` — pump the pipe through the layers |

The honest layers are tested with byte sequences split at every position, mutation fuzzing and no sockets
(`Axiom.ProtoStream.Testing`); sessions with in-memory pipes and a fake clock.

## 5. Building blocks

```
Axiom.ProtoStream/              core: describer, compiler, framing, Wire, connection, session, Raw
Axiom.ProtoStream.Testing/      CodecHarness, MessageReuseContract, DefinitionAssert, transports
Protocols/Axiom.ProtoStream.Http/        HTTP/1.1 server role
Protocols/Axiom.ProtoStream.WebSockets/  RFC 6455, server and client role, HTTP upgrade
UnitTests/                      one test project per project, same layout; UnitTests/Shared for fixtures
```

### Definition

```csharp
var chat = Protocol.Describe<ChatMessage, ChatMessage>("chat/1")
    .Framing(Framers.LengthPrefixed(LengthPrefix.UInt32BigEndian, maxFrameSize: 64 * 1024))
    .Messages(m => m.Add<Say>(1).Add<Ping>(2).Add<Pong>(3))
    .States(s => s
        .Start("Open")
        .In("Open")
            .On<Say>().Delegate()
            .On<Ping>().Respond(ping => new Pong())
            .OnSend<Say>())
    .Build();
```

The builder is staged: `Build()` is reachable only after a codec and the states were described. `Build()`
compiles type ids, per-state transition arrays, per-state readers and limits, throws a
`ProtocolDefinitionException` listing **all** problems, and records lint findings in `Warnings`.

| Inbound action | Effect |
|---|---|
| `Delegate()` | Yielded to the user's `await foreach`. |
| `Respond(f)` | The framework writes `f(message)` and keeps reading; budgeted. |
| `Wait()` | Consumed silently. |
| `GoTo(state)` | Moves on after the action; entering a final state ends the session gracefully. |

Outbound: `OnSend<T>()` declares what the user may write in a state; `GoTo`/`GoToIf` move on after the write.

### Runtime

- `Connection` owns the transport and the single write lock; disposing it closes the session gracefully,
  completes the pipes and disposes the stream(s).
- `Session<TIn,TOut>`: read from one loop (`Messages`), write from anywhere. `SwitchAsync` writes a final
  message (or none), ends the session and opens the next protocol on the same pipe; bytes the peer already sent
  go to the new session. A transport switch wraps the stream (STARTTLS) and refuses bytes sent too early.
- A message whose encoding hands the connection over (HTTP 101, 2xx to CONNECT) may only be written by
  `SwitchAsync` (`WriteResult.ThenHandOver()`).
- `Raw` is a passthrough protocol for tunnels.

## 6. Runtime view: one read

```
ReadAsync
 ├ synchronous path, while the bytes of the last pipe read hold another message and no payload is pending:
 │    bump the read generation → parse → transition without a write (Delegate: return, Wait: next)
 │    anything that needs to wait (a reply, OnEnter, a violation, more input) hands over to:
 ├ release the previous message: drain an unread payload (bounded), bump the read generation
 ├ loop: parse the buffered bytes; when they are used up:
 │    flush output held back by FlushPolicy.WhileInputIsBuffered, arm the message timeout, pipe.ReadAsync
 │    NeedMore → advance what the reader took, check MaxBufferedBytes, wait
 │    Invalid  → skip when the policy and the reader allow it, else reply (optional) and fault
 │    Done     → transition by message type: Wait / Respond / Delegate (return)
 └ disarm the timeout
```

A read of a message that is already buffered completes synchronously and allocates nothing; a write that finds
the write lock free and flushes synchronously does too.

## 7. Cross-cutting concepts

### Memory

- Messages may reference pipe memory until the next read on the session (delayed advance): zero-copy.
- A codec that needs a body first **detaches** the head into session memory; `DoneWithPayload` requires the
  `DetachedHead` token, so a payload can never be claimed while the head still points into the pipe.
- Payloads (`PipeReader Body`) are read straight from the connection through an `IPayloadDecoder`; an
  `IPayloadPreamble` sends bytes before the first body read (HTTP `100 Continue`).
- Pooled messages carry a `MessageStamp` and rotate two instances, so a message kept past the next read throws
  `StaleMessageException`; `Retain()` returns an owned copy.
- Every outgoing message is encoded completely before a byte reaches the wire.
- Releasing a session (switch, disposal) advances its read generation before its buffers go back to the pools;
  disposal stops running reads and writes first. See [SECURITY.md](SECURITY.md#isolation-between-connections).
- `FlushPolicy.WhileInputIsBuffered` keeps a user write in the output buffer while the next input is already
  there; the session flushes before it waits for input, closes or switches, so held output never waits on the peer.
- WebSocket frame payloads are consumed as they arrive, so a frame never has to sit whole in the pipe.

### Errors

See [ERROR-HANDLING.md](ERROR-HANDLING.md): definition (author), codec contract (codec), violation (peer),
state (caller), transport.

### Safety

See [SECURITY.md](SECURITY.md).

### Diagnostics

`IProtocolObserver` (opened, violation, switch, close, fault) and the `Axiom.ProtoStream` meter. No logging
dependency.

## 8. Decisions

| Decision | Reason |
|---|---|
| Pull-based sessions consumed with `await foreach` | Natural backpressure, no hidden tasks, the caller owns scheduling. |
| Exceptions for violations | One loud, typed failure per connection; violations are rare. |
| Type-based dispatch compiled to arrays | AOT-safe, allocation-free, no user-written discriminator for in-process messages. |
| Codecs per session from a factory | Codecs may keep state (fragments, the last request) without a "must be stateless" rule. |
| Detach token for payloads | Encodes "a payload-carrying message must not reference pipe memory". |
| Framework-owned framing fields | `Content-Length`, `Transfer-Encoding`, `Connection`, `Date` cannot contradict the body. |
| Fail closed where an RFC allows a choice | Ambiguity between parsers is what smuggling exploits. |
| Axiom family naming | One product family on NuGet; package id = assembly = root namespace. |
