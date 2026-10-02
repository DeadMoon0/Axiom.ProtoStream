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
| Pooled messages refuse use after reuse. | Generation stamps plus two instances in rotation. |

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
