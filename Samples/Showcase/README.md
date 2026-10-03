# ProtoStream Live

A web app served entirely by Axiom.ProtoStream — no ASP.NET, no Kestrel. One TCP connection per browser
connection, one task per connection: HTTP/1.1 for the page and the API, then the same connection switches to
WebSocket for everything live.

```bash
dotnet run -c Release --project Samples/Showcase
```

Open http://localhost:8080/ in two windows side by side.

| Card | What it shows | Framework feature |
|---|---|---|
| Shared canvas | Strokes appear in every open tab. | Binary WebSocket frames (10 bytes each), relayed from the pooled received message without copying. |
| Server pulse | Sockets, HTTP requests, messages in and out, violations, heap. | JSON pushed every second; counters read from ProtoStream's own `Axiom.ProtoStream` meter. |
| Chat | Messages sent over the socket or as `POST /api/messages`. | A JSON body read from the connection, validated, broadcast to every socket. |
| Round trip | 200 probes echoed one after another; min, avg, p95, max, histogram. | Text frames echoed by writing the received message back. |
| Request inspector | `GET /api/inspect?...` returns how the request was parsed. | RFC 9112 target forms, RFC 3986 path and query, effective authority, header fields. |
| Streamed upload | Drop a file: bytes, SHA-256, throughput. | `POST /api/upload` hashed straight from the socket; the body is never held in memory. |

## API

| Request | Answer |
|---|---|
| `GET /` , `/styles.css`, `/app.js` | The app, embedded in the executable. |
| `GET /api/stats` | The current pulse as JSON. |
| `GET /api/messages` | The last 50 chat messages. |
| `POST /api/messages` | `{"name": "...", "text": "..."}`; 201 with the stored message, 400 or 413 otherwise. |
| `GET /api/inspect` | The parsed request head. |
| `POST /api/upload` | Any body up to 256 MiB; bytes, SHA-256, time, throughput. |
| `GET /ws` | WebSocket upgrade. |

Wrong methods get 405 with an `Allow` field; unknown paths 404.

## Notes

- Browsers open spare connections they may never use. ProtoStream closes them after the 30-second
  first-message timeout; the pulse counts them under "violations & timeouts".
- The app uses source-generated JSON and embedded files, so it is set up for Native AOT
  (`dotnet publish Samples/Showcase -c Release -r win-x64`; on Windows this needs Visual Studio's C++ build tools).
