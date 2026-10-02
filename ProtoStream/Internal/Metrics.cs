using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace ProtoStream.Internal;

/// <summary>
/// The "ProtoStream" meter. Instruments are only touched when a listener enabled them, so the steady state
/// pays one branch per message when nobody listens.
/// </summary>
internal static class Metrics
{
    private static readonly Meter Meter = new("ProtoStream", "0.1.0");

    public static readonly UpDownCounter<long> ActiveSessions = Meter.CreateUpDownCounter<long>("protostream.sessions.active", description: "Sessions currently open.");
    public static readonly Counter<long> MessagesReceived = Meter.CreateCounter<long>("protostream.messages.received", description: "Messages parsed.");
    public static readonly Counter<long> MessagesSent = Meter.CreateCounter<long>("protostream.messages.sent", description: "Messages written.");
    public static readonly Counter<long> Violations = Meter.CreateCounter<long>("protostream.violations", description: "Protocol violations, by code.");

    public static void Add(Counter<long> counter, string protocol)
    {
        if (counter.Enabled)
            counter.Add(1, new KeyValuePair<string, object?>("protocol", protocol));
    }

    public static void AddViolation(string protocol, ViolationCode code)
    {
        if (Violations.Enabled)
            Violations.Add(1, new KeyValuePair<string, object?>("protocol", protocol), new KeyValuePair<string, object?>("code", CodeName(code)));
    }

    public static void AddActive(string protocol, long delta)
    {
        if (ActiveSessions.Enabled)
            ActiveSessions.Add(delta, new KeyValuePair<string, object?>("protocol", protocol));
    }

    private static string CodeName(ViolationCode code) => code switch
    {
        ViolationCode.Malformed => "malformed",
        ViolationCode.LimitExceeded => "limit_exceeded",
        ViolationCode.InvalidData => "invalid_data",
        ViolationCode.UnexpectedMessage => "unexpected_message",
        ViolationCode.Truncated => "truncated",
        ViolationCode.Timeout => "timeout",
        _ => "unsupported",
    };
}
