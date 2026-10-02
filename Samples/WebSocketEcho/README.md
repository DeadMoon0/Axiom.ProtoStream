# WebSocketEcho

An HTTP/1.1 server built only on ProtoStream: it serves a test page at `/` and echoes every WebSocket message.
One task per connection; HTTP until the upgrade, then WebSocket on the same connection.

```bash
dotnet run -c Release --project Samples/WebSocketEcho
```

Open http://localhost:9001/ and type a message.

## Native AOT

```bash
dotnet publish Samples/WebSocketEcho -c Release -r win-x64
```

## WebSocket conformance (Autobahn)

With the sample running and Docker available:

```bash
docker run --rm -v "${PWD}/Samples/WebSocketEcho/autobahn:/config" -v "${PWD}/Samples/WebSocketEcho/autobahn/reports:/reports" crossbario/autobahn-testsuite wstest -m fuzzingclient -s /config/fuzzingclient.json
```

The report is written to `autobahn/reports/index.html`. Cases 12 and 13 (permessage-deflate) are excluded:
the extension is not negotiated.
