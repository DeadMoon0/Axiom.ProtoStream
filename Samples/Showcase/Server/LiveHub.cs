using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Axiom.ProtoStream;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.WebSockets;

namespace Showcase.Server;

/// <summary>
/// Every open WebSocket. Fans messages out to the others: canvas strokes as binary frames, chat and pulse as
/// JSON text frames. Sessions accept writes from any task, so broadcasting needs no locks of its own.
/// </summary>
public sealed class LiveHub(Telemetry telemetry)
{
    /// <summary>Chat messages kept for clients that join later.</summary>
    private const int ChatHistory = 50;

    /// <summary>A client that cannot take a broadcast within this time is dropped.</summary>
    private static readonly TimeSpan BroadcastTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PulseInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<long, Session<WsMessage, WsMessage>> _clients = new();
    private readonly ConcurrentQueue<ChatEvent> _chat = new();

    public int Online => _clients.Count;

    public IReadOnlyList<ChatEvent> RecentChat => [.. _chat];

    /// <summary>Runs one client until it leaves.</summary>
    public async Task ServeAsync(Session<WsMessage, WsMessage> session, CancellationToken cancellationToken)
    {
        long id = session.Connection.Id;
        _clients[id] = session;
        await BroadcastAsync(Text(new PresenceEvent("presence", Online), ShowcaseJson.Default.PresenceEvent), except: null);
        try
        {
            await foreach (WsMessage message in session.Messages.WithCancellation(cancellationToken))
            {
                switch (message)
                {
                    // Canvas strokes: relayed as they are. The pooled message stays valid until this loop reads
                    // again, and every broadcast completes before that — no copy needed.
                    case WsBinary stroke:
                        await BroadcastAsync(stroke, except: id);
                        break;
                    case WsText text:
                        await OnTextAsync(session, text);
                        break;
                }
            }
        }
        catch (ProtoStreamException)
        {
            // The telemetry observer reports violations; the client is gone either way.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            await BroadcastAsync(Text(new PresenceEvent("presence", Online), ShowcaseJson.Default.PresenceEvent), except: null);
        }
    }

    /// <summary>Posts a chat message from any source (WebSocket or HTTP POST) to everyone.</summary>
    public async Task PostChatAsync(string name, string text)
    {
        var chat = new ChatEvent("chat", name, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _chat.Enqueue(chat);
        while (_chat.Count > ChatHistory && _chat.TryDequeue(out _))
        {
        }

        await BroadcastAsync(Text(chat, ShowcaseJson.Default.ChatEvent), except: null);
    }

    /// <summary>Pushes the server's pulse to every client once per second.</summary>
    public async Task RunPulseAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PulseInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var pulse = new PulseEvent(
                    "pulse", Online, telemetry.HttpRequests, telemetry.MessagesReceived, telemetry.MessagesSent,
                    telemetry.Violations, telemetry.ActiveSessions, telemetry.Uptime.TotalSeconds, GC.GetTotalMemory(forceFullCollection: false));
                await BroadcastAsync(Text(pulse, ShowcaseJson.Default.PulseEvent), except: null);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private async Task OnTextAsync(Session<WsMessage, WsMessage> session, WsText text)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(text.Utf8.Span, ShowcaseJson.Default.Envelope);
        }
        catch (JsonException)
        {
            return; // not one of ours; ignored
        }

        switch (envelope?.Type)
        {
            // Latency probes are answered to the sender only, unchanged.
            case "ping":
                await session.WriteAsync(text, CancellationToken.None);
                break;
            case "chat":
                ClientChat? chat = TryRead(text, ShowcaseJson.Default.ClientChat);
                if (chat is not null && Validation.TryChat(chat.Name, chat.Text, out string name, out string body))
                    await PostChatAsync(name, body);
                break;
            case "clear":
                await BroadcastAsync(Text(new ClearEvent("clear"), ShowcaseJson.Default.ClearEvent), except: null);
                break;
        }
    }

    private static T? TryRead<T>(WsText text, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(text.Utf8.Span, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task BroadcastAsync(WsMessage message, long? except)
    {
        using var timeout = new CancellationTokenSource(BroadcastTimeout);
        await Task.WhenAll(_clients
            .Where(client => client.Key != except)
            .Select(client => SendAsync(client.Key, client.Value, message, timeout.Token)));
    }

    private async Task SendAsync(long id, Session<WsMessage, WsMessage> session, WsMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await session.WriteAsync(message, cancellationToken);
        }
        catch (Exception)
        {
            // Closed, faulted, disposed or too slow: the client is dropped, the broadcast to the others goes on.
            _clients.TryRemove(id, out _);
        }
    }

    private static WsText Text<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        WsMessage.CreateText(JsonSerializer.Serialize(value, type));
}
