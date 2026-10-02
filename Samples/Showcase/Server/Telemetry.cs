using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using Axiom.ProtoStream;

namespace Showcase.Server;

/// <summary>
/// Reads ProtoStream's own "Axiom.ProtoStream" meter, so the pulse card shows what the framework counted, and
/// observes sessions for violations.
/// </summary>
public sealed class Telemetry : IProtocolObserver, IDisposable
{
    private const string MeterName = "Axiom.ProtoStream";

    private readonly MeterListener _listener = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private long _messagesReceived;
    private long _messagesSent;
    private long _violations;
    private long _activeSessions;
    private long _httpRequests;

    public Telemetry()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.Start();
    }

    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

    public long MessagesSent => Interlocked.Read(ref _messagesSent);

    public long Violations => Interlocked.Read(ref _violations);

    public long ActiveSessions => Interlocked.Read(ref _activeSessions);

    public long HttpRequests => Interlocked.Read(ref _httpRequests);

    public TimeSpan Uptime => _uptime.Elapsed;

    public void CountHttpRequest() => Interlocked.Increment(ref _httpRequests);

    public void OnViolation(ISession session, Violation violation) =>
        Console.WriteLine($"#{session.Connection.Id} {violation.Protocol}: {violation.Code} {violation.ProtocolErrorCode} - {violation.Detail}");

    public void OnFault(ISession session, Exception exception) =>
        Console.WriteLine($"#{session.Connection.Id} {session.Protocol}: fault - {exception.Message}");

    public void Dispose() => _listener.Dispose();

    private void OnMeasurement(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        switch (instrument.Name)
        {
            case "protostream.messages.received":
                Interlocked.Add(ref _messagesReceived, value);
                break;
            case "protostream.messages.sent":
                Interlocked.Add(ref _messagesSent, value);
                break;
            case "protostream.violations":
                Interlocked.Add(ref _violations, value);
                break;
            case "protostream.sessions.active":
                Interlocked.Add(ref _activeSessions, value);
                break;
        }
    }
}
