using System;
using System.Net;
using System.Threading;
using Showcase.Server;

// ProtoStream Live: a web app served entirely by Axiom.ProtoStream — HTTP pages and APIs (GET and POST),
// streamed uploads, and a WebSocket hub for a shared canvas, chat, live telemetry and latency tests.
//   dotnet run -c Release --project Samples/Showcase            http://localhost:8080/
//   dotnet run -c Release --project Samples/Showcase -- 5000    another port

const int DefaultPort = 8080;
int port = args.Length > 0 && int.TryParse(args[0], out int requested) ? requested : DefaultPort;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

using var telemetry = new Telemetry();
var hub = new LiveHub(telemetry);
var server = new ShowcaseServer(new IPEndPoint(IPAddress.Loopback, port), hub, telemetry);

Console.WriteLine($"ProtoStream Live on http://localhost:{port}/ (Ctrl+C to stop)");
await System.Threading.Tasks.Task.WhenAll(server.RunAsync(stop.Token), hub.RunPulseAsync(stop.Token));
