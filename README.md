# ProtoStream

Describe a network protocol once — framing, messages, state machine, limits, lifecycle — and run it
over any `Stream` or `IDuplexPipe`.

| Package | What it is |
|---|---|
| `ProtoStream` | Core: describer, framing, codecs, `Wire` serialization, connections and sessions. |
| `ProtoStream.Http` | HTTP/1.1 server codec: zero-copy heads, streamed bodies, keep-alive, pipelining, upgrade. |
| `ProtoStream.WebSockets` | RFC 6455 codec and the HTTP/1.1 upgrade into a WebSocket session. |
| `ProtoStream.Testing` | Harnesses for protocol authors: round-trips, fuzzing, lint assertions, in-memory sessions. |

Status: early preview. See [Documentation/Arc42.md](Documentation/Arc42.md) for the design.
