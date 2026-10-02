# Security model

## Threat model

- The **peer is hostile**: it sends malformed, ambiguous, oversized, slow or endless input, and tries to make
  the server buffer, allocate, wait or reply without limit.
- The **application is trusted but fallible**: it may build invalid responses, forget a limit, or keep a message
  too long.
- The **host is internet-facing**.

## Rules the framework applies everywhere

| Rule | How |
|---|---|
| Validate a length before waiting for it or allocating it. | Framers, `WireReader`, HTTP and WebSocket check declared lengths against their maximum and the bytes present. |
| Fail closed on ambiguity. | Whatever an RFC allows to "reject or interpret", ProtoStream rejects (see [COMPLIANCE.md](COMPLIANCE.md)). |
| The wire never names a type. | Message ids map to a table fixed at `Build()`; an unknown id is a violation. |
| Outbound data is validated too. | Header values with CR/LF, delimiters inside delimited frames, oversized strings and invalid close codes throw before a byte is sent. |
| No half message on the wire. | Every message is encoded completely before it is written; a payload that breaks its announced length faults the session. |
| Every wait has a deadline. | First-message, idle, payload-read and close timeouts. |
| Automatic replies are budgeted. | A peer cannot make the framework send more than `AutoRespondBudget` messages per second. |
| Unread payloads are drained only up to a limit. | `MaxPayloadDrain`. |
| Turning a protection off is visible. | Named opt-outs only, reported in `ProtocolDefinition.Warnings`; `WarningsAsErrors` for CI. |
| A buggy codec cannot hang or corrupt a session. | Contract checks: a message consumes at least one byte, positions stay in the input, one outcome per parse. |
| Pooled messages refuse use after reuse. | Generation stamps plus two instances in rotation, one rotation per message kind. |
| Nothing outlives its connection's memory. | See [Isolation between connections](#isolation-between-connections). |

## Isolation between connections

Connections share memory pools (`MemoryPool<byte>.Shared` for pipe segments, `ArrayPool<byte>.Shared` for
session buffers). A buffer one connection returns may be rented by the next, so every way back into returned
memory is closed:

| Way back into returned memory | Guard |
|---|---|
| A pooled message (HTTP request, WebSocket message) used after the next read | `StaleMessageException` on every accessor. |
| A message used after `SwitchAsync` or after the connection was disposed | Releasing a session advances its read generation first, so its messages turn stale before their memory is returned. |
| A header (`HttpHeader`) copied out of a request and kept | The header carries its request's stamp; its byte views throw once the request is stale. |
| A body reader (`HttpRequest.Body`) kept past the next request | Each payload gets its own reader bound to the message's stamp; the next request's body cannot be read through it. |
| `Connection.DisposeAsync` while another task reads or writes | Disposal first stops the session: reads end, stuck flushes are cancelled and fail with `TransportException`, the write lock is taken. Only then are buffers returned. If something still runs after the close timeout, the buffers are left to the garbage collector instead of the pools. |
| A frozen `HttpResponse` shared by many connections | `Freeze()` makes it immutable; its head is encoded once and only read afterwards. |

What the framework shares across connections is immutable: definitions, compiled message sets, framers and
payload encoders. Codecs are created per session. Violation details never contain peer bytes, and error replies
(HTTP 4xx, WebSocket close) carry no detail at all.

**Still the application's responsibility**

- **Byte views are views.** `ReadOnlyMemory<byte>` taken from a message (`PathBytes`, `ValueBytes`, `WsBinary.Data`,
  `WsText.Utf8`, `RawData.Data`) is not stamped once it is taken: copy it, or call `Retain()`, to keep it past
  the next read.
- **Relaying a received message to other sessions** (a broadcast) is safe when every write is awaited before
  the next read, as the showcase does. A fire-and-forget relay can be encoded after the memory is reused.
- **Shared callbacks** (an `IProtocolObserver`, an `IMessageSerializer`, `OnEnter` and responder delegates) run on
  many connections at once and must be thread-safe.
- **Pools are not cleared.** Returned memory keeps its old bytes. That is why the guards above exist; code that
  reads pooled memory without them (a custom codec reading past what it wrote) can see another connection's data.

## Defaults

| Limit | Core | HTTP/1.1 | WebSocket |
|---|---|---|---|
| First message | 30 s | 30 s (request head) | 2 min |
| Idle | 2 min | 130 s (keep-alive) | 2 min, keep-alive ping every 30 s |
| Close | 5 s | 5 s | 5 s |
| Buffered message | 1 MiB | 32 KiB head (+4 KiB) | 1 MiB message |
| Unread payload drain | 1 MiB | 1 MiB | — |
| Automatic replies | 1000/s | 1000/s | 1000/s |
| Other | — | request line 8 KiB, 100 fields, body 30 MB, trailers 8 KiB | — |

## What the host still has to do

- **TLS**: wrap the network stream in an `SslStream`.
- **Connection admission**: limit concurrent connections and accept rates per client; ProtoStream protects one
  connection at a time.
- **Pipe-based transports**: give the input pipe a pause threshold above the protocol's `MaxBufferedBytes`
  (see `Connection.FromPipe`).
- **Application rules**: authentication, authorisation, origin checks for WebSockets, path normalisation.
  Request paths are delivered as sent (percent-encoded); decode and normalise them before using them as file paths.

## Reporting a vulnerability

See [SECURITY.md](../SECURITY.md) in the repository root.
