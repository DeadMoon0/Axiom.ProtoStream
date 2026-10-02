# Errors

Every exception ProtoStream throws (apart from argument checks) derives from `ProtoStreamException`. Its
`Guidance` property, when set, says what to do. The type says who is at fault.

| Exception | Fault of | When | State afterwards | What to do |
|---|---|---|---|---|
| `ProtocolDefinitionException` | protocol author | `Build()` found problems. `Errors` lists all of them. | No definition. | Fix the description. |
| `ProtocolViolationException` | peer | Malformed input, a limit, a timeout. `Violation` holds the code, the state and the protocol's own error code (HTTP status, WebSocket close code). | Session `Faulted`; the violation reply (if the protocol has one) was sent. | Log it and dispose the connection. |
| `ProtocolStateException` | calling code | A write the state does not allow, an invalid response, a second concurrent read, writing after close. | Unchanged; nothing was sent. | Fix the calling code. |
| `ProtocolSwitchedException` | calling code | Using a session after `SwitchAsync`. | `Switched`. | Use the session `SwitchAsync` returned. |
| `StaleMessageException` | calling code | Using a pooled message after the next read. | Unchanged. | Finish with a message before reading on, or `Retain()` it. |
| `CodecContractException` | codec author | A codec threw, consumed nothing, reported positions outside its input, or two outcomes. | `Faulted`. | Fix the codec; `ProtoStream.Testing` finds most of these. |
| `TransportException` | transport | The stream failed or the peer stopped reading. Inner exception is the stream's error. | `Faulted`. | Dispose the connection. |

`OperationCanceledException` from a cancelled read leaves the session usable: nothing was consumed. A
cancelled write that was streaming a payload faults the session, because part of it may be on the wire.

## Observing errors

`ConnectionOptions.Observer` receives violations (including skipped ones), faults, switches and closes for
every session of a connection. The `ProtoStream` meter counts sessions, messages and violations by code.
