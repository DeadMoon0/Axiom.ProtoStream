using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Axiom.ProtoStream;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Http;
using Axiom.ProtoStream.WebSockets;

namespace WebSocketEcho;

/// <summary>Accepts TCP connections and runs one task per connection: HTTP until a WebSocket upgrade, then echo.</summary>
internal sealed class EchoServer(IPEndPoint endpoint)
{
    /// <summary>Messages up to 16 MiB, the largest the Autobahn conformance suite sends.</summary>
    private const int MaxMessageSize = 16 * 1024 * 1024;

    private static readonly ConnectionOptions Options = new() { Observer = new ConsoleObserver() };

    private static readonly WebSocketAcceptOptions Accept = new()
    {
        WebSocket = new WebSocketOptions { MaxMessageSize = MaxMessageSize },
    };

    private static readonly byte[] Page = Encoding.UTF8.GetBytes(EchoPage.Html);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(endpoint);
        listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket socket = await listener.AcceptSocketAsync(cancellationToken);
                _ = ServeAsync(socket, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ServeAsync(Socket socket, CancellationToken cancellationToken)
    {
        socket.NoDelay = true;
        try
        {
            await using Connection connection = Connection.FromStream(new NetworkStream(socket, ownsSocket: true), Options);
            Session<HttpRequest, HttpResponse> http = await connection.OpenAsync(Http11.Server(), cancellationToken);

            await foreach (HttpRequest request in http.Messages.WithCancellation(cancellationToken))
            {
                if (!WebSocket.IsUpgrade(request))
                {
                    await http.WriteAsync(Route(request), cancellationToken);
                    continue;
                }

                if (!WebSocket.TryAccept(request, Accept, out var accepted, out HttpResponse? rejection))
                {
                    await http.WriteAsync(rejection!, cancellationToken);
                    continue;
                }

                Session<WsMessage, WsMessage> ws = await http.SwitchAsync(accepted!, cancellationToken);
                await foreach (WsMessage message in ws.Messages.WithCancellation(cancellationToken))
                    await ws.WriteAsync(message, cancellationToken);
                return;
            }
        }
        catch (ProtoStreamException)
        {
            // Violations and transport failures are reported by the observer; the connection is gone.
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Shutdown or a peer that vanished.
        }
    }

    private static HttpResponse Route(HttpRequest request)
    {
        if (request.Method is not (HttpRequestMethod.Get or HttpRequestMethod.Head))
        {
            HttpResponse notAllowed = HttpResponse.Status(HttpStatus.MethodNotAllowed);
            notAllowed.Headers.Add("Allow", "GET, HEAD");
            return notAllowed;
        }

        return request.Path == "/"
            ? HttpResponse.Bytes(HttpStatus.Ok, Page, "text/html; charset=utf-8")
            : HttpResponse.Text(HttpStatus.NotFound, "Not found");
    }

    private sealed class ConsoleObserver : IProtocolObserver
    {
        public void OnViolation(ISession session, Violation violation) =>
            Console.WriteLine($"#{session.Connection.Id} {violation.Protocol}: {violation.Code} {violation.ProtocolErrorCode} - {violation.Detail}");

        public void OnFault(ISession session, Exception exception) =>
            Console.WriteLine($"#{session.Connection.Id} {session.Protocol}: fault - {exception.Message}");
    }
}
