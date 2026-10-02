using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Axiom.ProtoStream;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Http;
using Axiom.ProtoStream.WebSockets;

namespace Showcase.Server;

/// <summary>
/// One task per TCP connection. Each connection speaks HTTP/1.1 — pages, a JSON API, streamed uploads — until
/// the browser asks for <c>/ws</c>; then the same connection switches to WebSocket and joins the hub.
/// </summary>
internal sealed class ShowcaseServer(IPEndPoint endpoint, LiveHub hub, Telemetry telemetry)
{
    private const string WebSocketPath = "/ws";
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>Uploads up to 256 MiB, streamed and hashed without ever being held in memory.</summary>
    private const long MaxUploadBytes = 256L * 1024 * 1024;

    /// <summary>A posted chat message is small; anything larger is refused before it is read.</summary>
    private const int MaxJsonBodyBytes = 4 * 1024;

    /// <summary>WebSocket messages are canvas strokes, chat lines and probes: 64 KiB is plenty.</summary>
    private const int MaxWebSocketMessage = 64 * 1024;

    private const double BytesPerMegabyte = 1024 * 1024;

    private readonly ConnectionOptions _connection = new() { Observer = telemetry };
    private readonly ProtocolDefinition<HttpRequest, HttpResponse> _http = Http11.Server(new Http11Options { MaxRequestBodySize = MaxUploadBytes });
    private readonly WebSocketAcceptOptions _accept = new() { WebSocket = new WebSocketOptions { MaxMessageSize = MaxWebSocketMessage } };

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

    private async Task ServeAsync(Socket socket, CancellationToken cancellationToken)
    {
        socket.NoDelay = true;
        try
        {
            await using Connection connection = Connection.FromStream(new NetworkStream(socket, ownsSocket: true), _connection);
            Session<HttpRequest, HttpResponse> http = await connection.OpenAsync(_http, cancellationToken);

            await foreach (HttpRequest request in http.Messages.WithCancellation(cancellationToken))
            {
                telemetry.CountHttpRequest();
                if (request.Path == WebSocketPath && WebSocket.IsUpgrade(request))
                {
                    if (!WebSocket.TryAccept(request, _accept, out var accepted, out HttpResponse? rejection))
                    {
                        await http.WriteAsync(rejection!, cancellationToken);
                        continue;
                    }

                    // Same TCP connection, new protocol: from here on it is a WebSocket session.
                    Session<WsMessage, WsMessage> ws = await http.SwitchAsync(accepted!, cancellationToken);
                    await hub.ServeAsync(ws, cancellationToken);
                    return;
                }

                await http.WriteAsync(await RouteAsync(request, cancellationToken), cancellationToken);
            }
        }
        catch (ProtoStreamException)
        {
            // Violations and faults are reported by the telemetry observer; the connection is gone.
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Shutdown, or a browser that went away.
        }
    }

    private async Task<HttpResponse> RouteAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        HttpRequestMethod method = request.Method;
        switch (request.Path)
        {
            case "/api/stats" when method == HttpRequestMethod.Get:
                return Json(HttpStatus.Ok, Pulse(), ShowcaseJson.Default.PulseEvent);
            case "/api/messages" when method == HttpRequestMethod.Get:
                return Json(HttpStatus.Ok, hub.RecentChat, ShowcaseJson.Default.IReadOnlyListChatEvent);
            case "/api/messages" when method == HttpRequestMethod.Post:
                return await PostMessageAsync(request, cancellationToken);
            case "/api/inspect" when method == HttpRequestMethod.Get:
                return Json(HttpStatus.Ok, Inspect(request), ShowcaseJson.Default.InspectResult);
            case "/api/upload" when method == HttpRequestMethod.Post:
                return await UploadAsync(request, cancellationToken);
            case "/api/stats" or "/api/messages" or "/api/inspect" or "/api/upload":
                return MethodNotAllowed(request.Path is "/api/messages" ? "GET, POST" : request.Path is "/api/upload" ? "POST" : "GET");
        }

        if (method is HttpRequestMethod.Get or HttpRequestMethod.Head && StaticFiles.TryGet(request.Path, out byte[] content, out string contentType))
        {
            HttpResponse page = HttpResponse.Bytes(HttpStatus.Ok, content, contentType);
            page.Headers.Add("Cache-Control", "no-cache");
            return page;
        }

        return Json(HttpStatus.NotFound, new ErrorResult("Nothing here."), ShowcaseJson.Default.ErrorResult);
    }

    /// <summary>POST /api/messages: a JSON body read from the connection, then broadcast to every WebSocket.</summary>
    private async Task<HttpResponse> PostMessageAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxJsonBodyBytes)
            return Json(HttpStatus.ContentTooLarge, new ErrorResult($"A message body is limited to {MaxJsonBodyBytes} bytes."), ShowcaseJson.Default.ErrorResult);

        byte[]? body = await ReadSmallBodyAsync(request.Body, cancellationToken);
        if (body is null)
            return Json(HttpStatus.ContentTooLarge, new ErrorResult($"A message body is limited to {MaxJsonBodyBytes} bytes."), ShowcaseJson.Default.ErrorResult);

        ChatPost? post;
        try
        {
            post = JsonSerializer.Deserialize(body, ShowcaseJson.Default.ChatPost);
        }
        catch (JsonException)
        {
            post = null;
        }

        if (post is null || !Validation.TryChat(post.Name, post.Text, out string name, out string text))
            return Json(HttpStatus.BadRequest, new ErrorResult("Send {\"name\": \"...\", \"text\": \"...\"} with a non-empty text."), ShowcaseJson.Default.ErrorResult);

        await hub.PostChatAsync(name, text);
        return Json(HttpStatus.Created, new ChatEvent("chat", name, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ShowcaseJson.Default.ChatEvent);
    }

    /// <summary>POST /api/upload: the body streams from the socket through SHA-256; it is never held whole.</summary>
    private static async Task<HttpResponse> UploadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        PipeReader body = request.Body;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            ReadResult read = await body.ReadAsync(cancellationToken);
            foreach (ReadOnlyMemory<byte> segment in read.Buffer)
                sha.AppendData(segment.Span);
            bytes += read.Buffer.Length;
            body.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                break;
        }

        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        double throughput = milliseconds > 0 ? bytes / BytesPerMegabyte / (milliseconds / 1000) : 0;
        var result = new UploadResult(bytes, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), Math.Round(milliseconds, 1), Math.Round(throughput, 1));
        return Json(HttpStatus.Ok, result, ShowcaseJson.Default.UploadResult);
    }

    private static InspectResult Inspect(HttpRequest request)
    {
        var headers = new List<HeaderField>(request.Headers.Count);
        for (int i = 0; i < request.Headers.Count; i++)
        {
            HttpHeader field = request.Headers[i];
            headers.Add(new HeaderField(field.Name, IsCredential(field.Name) ? HiddenValue : field.Value));
        }

        return new InspectResult(
            request.MethodName,
            request.Target,
            request.TargetForm.ToString(),
            request.Path,
            request.HasQuery ? request.Query : null,
            request.Authority,
            request.Version == HttpProtocolVersion.Http11 ? "HTTP/1.1" : "HTTP/1.0",
            request.KeepAlive,
            headers);
    }

    private PulseEvent Pulse() => new(
        "pulse", hub.Online, telemetry.HttpRequests, telemetry.MessagesReceived, telemetry.MessagesSent,
        telemetry.Violations, telemetry.ActiveSessions, telemetry.Uptime.TotalSeconds, GC.GetTotalMemory(forceFullCollection: false));

    /// <returns>The body, or null when it is larger than <see cref="MaxJsonBodyBytes"/>.</returns>
    private static async Task<byte[]?> ReadSmallBodyAsync(PipeReader body, CancellationToken cancellationToken)
    {
        var collected = new ArrayBufferWriter<byte>();
        while (true)
        {
            ReadResult read = await body.ReadAsync(cancellationToken);
            if (collected.WrittenCount + read.Buffer.Length > MaxJsonBodyBytes)
                return null; // the rest is drained by the session, up to its limit
            foreach (ReadOnlyMemory<byte> segment in read.Buffer)
                collected.Write(segment.Span);
            body.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                return collected.WrittenSpan.ToArray();
        }
    }

    private static HttpResponse Json<T>(int status, T value, JsonTypeInfo<T> type)
    {
        HttpResponse response = HttpResponse.Bytes(status, JsonSerializer.SerializeToUtf8Bytes(value, type), JsonContentType);
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        return response;
    }

    // The inspector shows how a request was parsed, not the caller's secrets: a page that echoes cookies and
    // credentials hands them to any script that can make the browser call it.
    private static bool IsCredential(string fieldName) =>
        fieldName.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || fieldName.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || fieldName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    private const string HiddenValue = "(hidden)";

    private static HttpResponse MethodNotAllowed(string allowed)
    {
        HttpResponse response = Json(HttpStatus.MethodNotAllowed, new ErrorResult($"Use {allowed}."), ShowcaseJson.Default.ErrorResult);
        response.Headers.Add("Allow", allowed);
        return response;
    }
}
