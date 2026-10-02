using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Showcase.Server;

/// <summary>A chat message as posted over HTTP and pushed over WebSocket.</summary>
public sealed record ChatPost(string Name, string Text);

public sealed record ChatEvent(string Type, string Name, string Text, long At);

/// <summary>The server's pulse, pushed to every WebSocket once per second.</summary>
public sealed record PulseEvent(
    string Type,
    int Online,
    long HttpRequests,
    long MessagesReceived,
    long MessagesSent,
    long Violations,
    long ActiveSessions,
    double UptimeSeconds,
    long ManagedMemory);

public sealed record PresenceEvent(string Type, int Online);

public sealed record PingEvent(string Type, long Id, double SentAt);

public sealed record ClearEvent(string Type);

/// <summary>How ProtoStream parsed a request: what the inspector card shows.</summary>
public sealed record InspectResult(
    string Method,
    string Target,
    string TargetForm,
    string Path,
    string? Query,
    string Authority,
    string Version,
    bool KeepAlive,
    IReadOnlyList<HeaderField> Headers);

public sealed record HeaderField(string Name, string Value);

public sealed record UploadResult(long Bytes, string Sha256, double Milliseconds, double MegabytesPerSecond);

public sealed record ErrorResult(string Error);

/// <summary>Any WebSocket text message: only its type is read first.</summary>
public sealed record Envelope(string Type);

public sealed record ClientChat(string Type, string Name, string Text);

/// <summary>Source-generated serialization: no reflection, so the sample publishes as Native AOT.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ChatPost))]
[JsonSerializable(typeof(ChatEvent))]
[JsonSerializable(typeof(IReadOnlyList<ChatEvent>))]
[JsonSerializable(typeof(PulseEvent))]
[JsonSerializable(typeof(PresenceEvent))]
[JsonSerializable(typeof(PingEvent))]
[JsonSerializable(typeof(ClearEvent))]
[JsonSerializable(typeof(InspectResult))]
[JsonSerializable(typeof(UploadResult))]
[JsonSerializable(typeof(ErrorResult))]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(ClientChat))]
internal sealed partial class ShowcaseJson : JsonSerializerContext;
