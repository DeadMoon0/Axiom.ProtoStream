using System;
using System.Net;
using System.Threading;
using WebSocketEcho;

// An HTTP/1.1 server that serves a test page and echoes WebSocket messages, built only on ProtoStream.
//   dotnet run --project Samples/WebSocketEcho            listens on http://localhost:9001/
//   dotnet run --project Samples/WebSocketEcho -- 8080    listens on another port

const int DefaultPort = 9001;
int port = args.Length > 0 && int.TryParse(args[0], out int requested) ? requested : DefaultPort;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

var server = new EchoServer(new IPEndPoint(IPAddress.Loopback, port));
Console.WriteLine($"Listening on http://localhost:{port}/ (Ctrl+C to stop)");
await server.RunAsync(stop.Token);
