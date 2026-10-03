# Spec compliance

Every MUST-level requirement of the specifications below that applies to the implemented roles, how
ProtoStream meets it, and the test that proves it. Tests live in the matching project under `UnitTests/`.

Scope: the HTTP/1.1 **server** role (RFC 9110, RFC 9112), request targets and Host fields against the URI
grammar (RFC 3986), and WebSockets (RFC 6455) in server and client role. Where an RFC allows a choice
("MAY reject or ..."), ProtoStream takes the stricter one; that is noted.

## HTTP/1.1 message syntax — RFC 9112

| Section | Requirement | Behaviour | Test |
|---|---|---|---|
| 2.2 | Recipients parse octets as a superset of US-ASCII; bare CR is invalid. | Bytes are parsed as octets; a CR outside CRLF is a control character and refused. | `AnAmbiguousOrMalformedRequestIsRefused…` |
| 2.2 | Ignore at least one empty line before the request line. | Any number of leading CRLFs is skipped (bounded by the head limit). | `TheRequestParserSurvivesMutationFuzzing` (regression from a fuzz finding) |
| 2.3 | HTTP-version is case-sensitive `HTTP/` DIGIT `.` DIGIT. | `HTTP/1.1` and `HTTP/1.0` served; other well-formed versions get 505, malformed ones 400. | `…Refused…("GET / HTTP/2.0…", 505)`, `("GET / HTTPX/1.1…", 400)` |
| 3 | request-line = method SP request-target SP HTTP-version; invalid line gets 400. | Exactly one SP between elements; anything else 400. | `…Refused…("GET /a b HTTP/1.1…")` |
| 3.1 | Methods are case-sensitive tokens. | Token check; `get` is not GET. | `MethodsAreCaseSensitive` |
| 3.2 | A server MUST respond 400 to an HTTP/1.1 request without Host, with more than one, or with an invalid one. | Enforced; Host checked as `uri-host [":" port]`. | `…Refused…(Host cases)`, `AnInvalidTargetOrHostGets400` |
| 3.2.1 | origin-form = absolute-path [ "?" query ]. | Path and query checked against RFC 3986. | `AnOriginFormTargetIsSplitIntoPathAndQuery` |
| 3.2.2 | A server MUST accept absolute-form; the target's authority overrides Host. | Accepted; `Authority` comes from the target. | `AnAbsoluteFormTargetIsAccepted…` |
| 3.2.3 | authority-form only for CONNECT. | CONNECT requires `host:port`; other methods cannot use it. | `AnInvalidTargetOrHostGets400` |
| 3.2.4 | asterisk-form only for OPTIONS. | Enforced. | `AnAsteriskTargetIsAcceptedForOptions`, `AnInvalidTargetOrHostGets400` |
| 3.3 | No fragment in a request target. | `#` is not a target character: 400. | `AnInvalidTargetOrHostGets400` |
| 4 | status-line = HTTP-version SP status-code SP [ reason-phrase ]. | Always `HTTP/1.1`, three digits, standard or given reason. | `ARequestHeadIsParsedAndTheResponseFramedByTheFramework` |
| 5.1 | No whitespace between field name and colon: MUST reject with 400. | Enforced. | `…Refused…("Host : x")` |
| 5.2 | obs-fold in a request: reject with 400 or replace. | Rejected (stricter choice). | `…Refused…(folded field)` |
| 6.1 | Transfer codings in HTTP/1.0 make framing faulty. | 400. | `…Refused…("POST / HTTP/1.0…Transfer-Encoding")` |
| 6.1 | chunked MUST NOT be applied more than once. | Twice chunked: 400. | `…Refused…(two chunked fields)` |
| 6.1 | A transfer coding the server does not understand: 501. | Anything but a single chunked: 501. | `…Refused…("gzip, chunked", 501)` |
| 6.3 | Transfer-Encoding with chunked not last in a request: MUST 400 and close. | Enforced. | `…Refused…("chunked, gzip", 400)` |
| 6.3 | Transfer-Encoding with Content-Length: reject, or use Transfer-Encoding and close. | Rejected (stricter choice). | `…Refused…(both fields)` |
| 6.3 | Invalid Content-Length without Transfer-Encoding is an unrecoverable error: 400 and close. | Digits only, identical duplicates only, no lists, no sign. | `…Refused…("+3")`, `("3, 3")`, `(3 and 4)` |
| 7.1 | Recipients MUST parse and decode chunked. | Decoded zero-copy from the connection. | `AChunkedBodyIsDecoded…`, `…OneByteAtATime…` |
| 7.1.1 | chunk-ext grammar; unrecognised extensions are ignored. | Parsed by grammar (token / quoted-string), then ignored. | `ChunkExtensionsAreParsedByGrammar…`, `AMalformedChunkedBodyFails…` |
| 7.1.2 | Trailer fields have field-line syntax. | Validated, then discarded. | `AMalformedChunkedBodyFails…(trailer)` |
| 9.3 | Persistent by default in 1.1; `close` option ends the connection after the response. | Enforced; HTTP/1.0 keep-alive honoured. | `AnHttp10Request…`, `PipelinedRequestsAreAnsweredInOrder` |
| 9.3.2 | Pipelined requests MUST be answered in order. | The state machine allows one response per request, in order. | `PipelinedRequestsAreAnsweredInOrder` |

## HTTP semantics — RFC 9110

| Section | Requirement | Behaviour | Test |
|---|---|---|---|
| 4.2.1 | An http(s) URI with an empty host MUST be rejected. | 400. | `AnInvalidTargetOrHostGets400("http:///a")` |
| 4.2.4 | userinfo in an http(s) URI SHOULD be treated as an error. | 400. | `AnInvalidTargetOrHostGets400(userinfo)` |
| 5.5 | Field values with CR, LF or NUL are invalid: reject or replace. | Rejected, as is every other control except HTAB. | `…Refused…(control in value)` |
| 5.6.1 | Recipients MUST ignore a reasonable number of empty list elements. | Ignored in every list field. | `ValidEdgeCasesAreAccepted(",chunked,")` |
| 6.6.1 | An origin server with a clock MUST send Date in 2xx, 3xx and 4xx. | Sent in every final response (cached per second, IMF-fixdate); an application Date is kept. | `ARequestHeadIs…`, `AnApplicationDateIsNotDuplicated` |
| 7.8 | A server MUST NOT switch to a protocol the client did not offer; 101 MUST carry Upgrade. | Checked against the request's Upgrade field; 101 only via `SwitchAsync`. | `SwitchingProtocolsIsOnlyForAnOfferedProtocol…`, `…WithoutAnUpgradeRequestIsRefused` |
| 8.6 | No Content-Length in 1xx or 204 responses. | Framing fields are framework-owned and omitted there. | `ANoContentResponseHasNoLength…`, `InterimResponses…` |
| 9.3.2 | HEAD responses carry no content. | Content-Length announced, content not sent. | `AHeadResponseAnnouncesTheLengthButSendsNoBody` |
| 9.3.6 | CONNECT uses authority-form; a 2xx response MUST NOT carry Content-Length or Transfer-Encoding. | Enforced; the 2xx hands the connection over (tunnel). | `ASuccessfulConnectHandsTheConnectionOver…` |
| 10.1.1 | 100-continue in HTTP/1.0 MUST be ignored; other expectations MAY get 417. | Ignored in 1.0; unknown expectations 417; 100 Continue sent when the body is first read; a final response while the client waits closes the connection. | `OneHundredContinue…`, `AFinalResponseWithoutReading…`, `AnUnknownExpectationGets417`, `ExpectationsAreIgnoredInHttp10` |
| 15.2 | A server MUST NOT send a 1xx response to an HTTP/1.0 client. | Refused before anything is sent. | `NoInterimResponseIsSentToAnHttp10Client` |
| 15.5.2, .6, .8, .22 | 401 needs WWW-Authenticate, 405 Allow, 407 Proxy-Authenticate, 426 Upgrade. | A response without the field is refused before anything is sent. | `AStatusThatRequiresAFieldIsRefusedWithoutIt` |

## URI syntax — RFC 3986 (request targets and Host)

| Section | Rule | Test |
|---|---|---|
| 2.1 | Percent-encoding is `%` HEXDIG HEXDIG. | `AnInvalidTargetOrHostGets400("%zz", "%2")` |
| 3.1 | scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ). | `AnInvalidTargetOrHostGets400("relative/path")` |
| 3.2.2 | host = IP-literal / IPv4address / reg-name; IPv6 checked; zone identifiers refused. | `ValidEdgeCasesAreAccepted([::1]:8080)`, `AnInvalidTargetOrHostGets400([zz])` |
| 3.2.3 | port = *DIGIT. | `AnInvalidTargetOrHostGets400(":http")` |
| 3.3, 3.4 | path and query character sets. | `AnInvalidTargetOrHostGets400('"')`, `ValidEdgeCasesAreAccepted` |

## WebSockets — RFC 6455

Verified by unit tests and by the [Autobahn testsuite](https://github.com/crossbario/autobahn-testsuite)
(fuzzingclient against `Samples/WebSocketEcho`, cases 1–10; 12–13 cover permessage-deflate, which is not
negotiated). See [the sample](../Samples/WebSocketEcho/README.md) to reproduce.

Latest Autobahn result (testsuite 25.10.1, 2026-10-03, one uninterrupted run): **301 cases — 298 OK, 3 informational, 0 non-strict,
0 failed.** The informational cases (7.1.6, 7.13.1, 7.13.2) exercise behaviour RFC 6455 leaves undefined.

| Section | Requirement | Behaviour | Test |
|---|---|---|---|
| 4.2.1 | GET, HTTP/1.1, Upgrade websocket, Connection upgrade, one 16-byte key, version 13. | Validated; 400 otherwise. | `AnInvalidHandshakeGetsTheResponseTheRfcAsksFor` |
| 4.2.2 | Version mismatch: 426 with Sec-WebSocket-Version. | Enforced. | same |
| 4.2.2 | Accept = base64(SHA-1(key + GUID)); a selected subprotocol must be one the client offered. | Enforced. | `TheAcceptKeyMatchesTheRfcExample`, `ASubprotocolTheClientDidNotOfferIsRefused` |
| 5.1 | Servers fail unmasked client frames; clients fail masked server frames; clients mask every frame. | Enforced; fresh key per frame from a CSPRNG. | `AProtocolErrorFails…(unmasked)`, `AClientMasksEveryFrame…` |
| 5.2 | RSV bits without an extension, reserved opcodes: fail. Minimal length encoding; 64-bit length MSB 0. | Fail with 1002. | `AProtocolErrorFails…` |
| 5.4 | Fragmentation rules; control frames may interleave. | Enforced; pings answered mid-message. | `APingBetweenFragments…`, `AProtocolErrorFails…(orphan, interleaved)` |
| 5.5 | Control frames ≤ 125 bytes, never fragmented. | Fail with 1002. | `AProtocolErrorFails…(long ping, fragmented ping)` |
| 5.5.1 | A received Close MUST be answered with Close; no data after sending Close. | Echoed; writes refused in Closing. | `APeerCloseIsEchoed…`, `ClosingRunsTheHandshake…` |
| 5.5.2 | A Ping MUST be answered with a Pong carrying the same data. | Framework reply. | `APingBetweenFragments…` |
| 5.7 | Example frames. | Decoded. | `TheMaskedTextExampleOfTheRfcDecodes`, `TheUnmaskedExamples…` |
| 7.4 | Close codes: reserved ones are never sent and fail when received; close payload of one byte fails. | Enforced in both directions. | `OnlySendableCloseCodesCanBeCreated`, `AProtocolErrorFails…(close cases)` |
| 8.1 | Invalid UTF-8 in text fails the connection (1007) as soon as it is seen. | Validated incrementally across fragments. | `InvalidUtf8FailsAtTheFragmentThatContainsIt…`, `ACodePointSplitAcrossFragmentsIsValid` |
| 10.4 | Implementations SHOULD limit frame and message sizes. | Limit applied to the declared length; 1009. | `AnOversizedMessageIsRefusedFromItsHeaderAlone` |

## Out of scope

- HTTP/1.1 **client** role, HTTP/2, HTTP/3.
- Content codings (gzip, br) for bodies; transfer codings other than chunked (501).
- WebSocket extensions (permessage-deflate): never negotiated, so never in effect.
- TLS: use `SslStream` under the connection, or switch to it with a transport switch.
