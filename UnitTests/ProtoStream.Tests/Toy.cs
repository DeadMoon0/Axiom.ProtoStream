using System;
using ProtoStream.Codecs;
using ProtoStream.Framing;

namespace ProtoStream.Tests;

/// <summary>A small binary protocol used throughout the tests: hello, data, ping/pong, bye.</summary>
public abstract class ToyMessage
{
}

public sealed class Hello : ToyMessage, IWireMessage<Hello>
{
    public required string Name { get; init; }

    public static bool TryRead(ref WireReader reader, out Hello message)
    {
        message = new Hello { Name = reader.String(LengthPrefix.UInt8, maxBytes: 64) };
        return reader.Ok;
    }

    public void Write(ref WireWriter writer) => writer.String(LengthPrefix.UInt8, Name, maxBytes: 64);
}

public sealed class Data : ToyMessage, IWireMessage<Data>
{
    public required byte[] Payload { get; init; }

    public static bool TryRead(ref WireReader reader, out Data message)
    {
        message = new Data { Payload = reader.Bytes(reader.Remaining) };
        return reader.Ok;
    }

    public void Write(ref WireWriter writer) => writer.Bytes(Payload);
}

public sealed class Ping : ToyMessage, IWireMessage<Ping>
{
    public required uint Id { get; init; }

    public static bool TryRead(ref WireReader reader, out Ping message)
    {
        message = new Ping { Id = reader.UInt32BigEndian() };
        return reader.Ok;
    }

    public void Write(ref WireWriter writer) => writer.UInt32BigEndian(Id);
}

public sealed class Pong : ToyMessage, IWireMessage<Pong>
{
    public required uint Id { get; init; }

    public static bool TryRead(ref WireReader reader, out Pong message)
    {
        message = new Pong { Id = reader.UInt32BigEndian() };
        return reader.Ok;
    }

    public void Write(ref WireWriter writer) => writer.UInt32BigEndian(Id);
}

public sealed class Bye : ToyMessage, IWireMessage<Bye>
{
    public static bool TryRead(ref WireReader reader, out Bye message)
    {
        message = new Bye();
        return reader.Ok;
    }

    public void Write(ref WireWriter writer) { }
}

public static class Toy
{
    public static IFramer Framer { get; } = Framers.LengthPrefixed(LengthPrefix.UInt16BigEndian, maxFrameSize: 4096);

    public static IStatesStage<ToyMessage, ToyMessage> Describe() =>
        Protocol.Describe<ToyMessage, ToyMessage>("toy")
            .Framing(Framer)
            .Messages(m => m.Add<Hello>(1).Add<Data>(2).Add<Ping>(3).Add<Pong>(4).Add<Bye>(5));

    /// <summary>Server role: waits for hello, then exchanges data; pings are answered by the framework.</summary>
    public static IPolicyStage<ToyMessage, ToyMessage> Server() => Describe()
        .States(s => s
            .Start("AwaitHello")
            .In("AwaitHello").On<Hello>().Delegate().GoTo("Open")
            .In("Open")
                .On<Data>().Delegate()
                .On<Ping>().Respond(p => new Pong { Id = p.Id })
                .On<Pong>().Wait()
                .On<Bye>().Respond(_ => new Bye()).GoTo("Closed")
                .OnSend<Data>()
                .OnSend<Ping>()
                .Switchable()
            .Final("Closed"));

    /// <summary>Client role: everything the server sends is handed to the user.</summary>
    public static ProtocolDefinition<ToyMessage, ToyMessage> Client() => Describe()
        .States(s => s
            .Start("Open")
            .In("Open")
                .On<Data>().Delegate()
                .On<Ping>().Delegate()
                .On<Pong>().Delegate()
                .On<Bye>().Delegate().GoTo("Closed")
                .OnSend<Hello>()
                .OnSend<Data>()
                .OnSend<Ping>()
                .OnSend<Pong>()
                .OnSend<Bye>()
            .Final("Closed"))
        .Limits(l => l.NoIdleTimeout())
        .Build();

    public static byte[] Frame(byte id, params byte[] body)
    {
        var frame = new byte[3 + body.Length];
        frame[0] = (byte)((body.Length + 1) >> 8);
        frame[1] = (byte)(body.Length + 1);
        frame[2] = id;
        body.CopyTo(frame, 3);
        return frame;
    }

    public static byte[] HelloFrame(string name)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name);
        var body = new byte[utf8.Length + 1];
        body[0] = (byte)utf8.Length;
        utf8.CopyTo(body, 1);
        return Frame(1, body);
    }

    public static byte[] PingFrame(uint id) => Frame(3, (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id);

    public static byte[] Concat(params byte[][] parts)
    {
        int length = 0;
        foreach (byte[] part in parts)
            length += part.Length;
        var all = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(all, offset);
            offset += part.Length;
        }

        return all;
    }

    public static ReadOnlyMemory<byte> AsMemory(this byte[] bytes) => bytes;
}
