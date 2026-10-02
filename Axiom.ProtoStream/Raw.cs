using System;
using System.Buffers;
using Axiom.ProtoStream.Codecs;

namespace Axiom.ProtoStream;

/// <summary>
/// Bytes as they arrive, without any framing: the protocol a connection switches to for tunnels
/// (HTTP <c>CONNECT</c>, SOCKS) or to hand the stream to code that is not protocol-aware.
/// </summary>
public static class Raw
{
    /// <summary>The raw protocol with default limits.</summary>
    public static ProtocolDefinition<RawData, RawData> Definition { get; } = Build(static _ => { });

    /// <summary>The raw protocol with the given limits, for example a longer idle timeout for a tunnel.</summary>
    public static ProtocolDefinition<RawData, RawData> Build(Action<LimitsBuilder> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return Protocol.Describe<RawData, RawData>("raw")
            .Codec(static () => new RawCodec())
            .States(static s => s.Start("Open").In("Open").On<RawData>().Delegate().OnSend<RawData>())
            .Limits(limits)
            .Build();
    }

    private sealed class RawCodec : ICodec<RawData, RawData>
    {
        // Two instances in rotation, so a block kept past the next read reports itself stale.
        private readonly RawData[] _pooled = [new(), new()];
        private int _next;

        public ParseResult TryParse(ref MessageParseContext context, out RawData message)
        {
            message = null!;
            if (context.Input.IsEmpty)
                return context.NeedMore();

            message = _pooled[_next ^= 1];
            message.Set(context.Input, context.Stamp);
            return context.Done(context.Input.End);
        }

        public WriteResult Write(RawData message, IBufferWriter<byte> output)
        {
            foreach (ReadOnlyMemory<byte> segment in message.Data)
                output.Write(segment.Span);
            return WriteResult.Done;
        }
    }
}

/// <summary>A block of raw bytes. Received blocks are views of the connection buffer, valid until the next read.</summary>
public sealed class RawData
{
    private ReadOnlySequence<byte> _data;
    private MessageStamp _stamp;

    internal RawData()
    {
    }

    /// <summary>The bytes. Throws <see cref="Errors.StaleMessageException"/> after the session read on.</summary>
    public ReadOnlySequence<byte> Data
    {
        get
        {
            _stamp.ThrowIfStale();
            return _data;
        }
    }

    /// <summary>A block to send.</summary>
    public static RawData From(ReadOnlyMemory<byte> bytes)
    {
        var data = new RawData();
        data.Set(new ReadOnlySequence<byte>(bytes), default);
        return data;
    }

    /// <summary>An owned copy that stays valid after the next read.</summary>
    public RawData Retain() => From(Data.ToArray());

    internal void Set(ReadOnlySequence<byte> data, MessageStamp stamp)
    {
        _data = data;
        _stamp = stamp;
    }
}
