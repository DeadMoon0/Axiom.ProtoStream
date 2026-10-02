using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using ProtoStream.Errors;
using ProtoStream.Framing;

namespace ProtoStream.Codecs;

/// <summary>How the message id at the start of a frame is encoded.</summary>
public sealed class Discriminator
{
    private Discriminator(LengthPrefix encoding) => Encoding = encoding;

    /// <summary>One byte.</summary>
    public static Discriminator UInt8 { get; } = new(LengthPrefix.UInt8);

    /// <summary>Two bytes, most significant first.</summary>
    public static Discriminator UInt16BigEndian { get; } = new(LengthPrefix.UInt16BigEndian);

    /// <summary>Unsigned LEB128.</summary>
    public static Discriminator VarInt { get; } = new(LengthPrefix.VarInt);

    internal LengthPrefix Encoding { get; }

    /// <inheritdoc />
    public override string ToString() => Encoding.Name;
}

/// <summary>
/// Serializes the bodies of a message set with a library of your choice (MessagePack, protobuf, JSON source
/// generation). The message id stays the message set's job.
/// </summary>
public interface IMessageSerializer
{
    /// <summary>Deserializes a body known to hold a <paramref name="type"/>. Return false when it does not.</summary>
    bool TryDeserialize(Type type, ReadOnlyMemory<byte> body, out object? message);

    /// <summary>Serializes <paramref name="message"/> of <paramref name="type"/>.</summary>
    void Serialize(Type type, object message, IBufferWriter<byte> output);
}

/// <summary>
/// Maps message types to ids: every frame is <c>[id][body]</c>. The id is read first and decides which type
/// reads the rest; the wire never names a CLR type.
/// </summary>
public sealed class MessageSetBuilder<TIn, TOut>
    where TIn : class
    where TOut : class
{
    private readonly List<Entry> _entries = [];
    private Codecs.Discriminator _discriminator = Codecs.Discriminator.UInt8;
    private IMessageSerializer? _serializer;

    internal List<string> Errors { get; } = [];

    /// <summary>Encoding of the message id. The default is <see cref="Codecs.Discriminator.UInt8"/>.</summary>
    public MessageSetBuilder<TIn, TOut> Discriminator(Codecs.Discriminator discriminator)
    {
        ArgumentNullException.ThrowIfNull(discriminator);
        _discriminator = discriminator;
        return this;
    }

    /// <summary>Registers a message that reads and writes itself.</summary>
    public MessageSetBuilder<TIn, TOut> Add<T>(long id) where T : class, IWireMessage<T>
    {
        _entries.Add(new Entry(typeof(T), id, ReadWire<T>, WriteWire<T>));
        return this;
    }

    /// <summary>Registers a message whose body the <see cref="Serializer"/> handles.</summary>
    public MessageSetBuilder<TIn, TOut> Map<T>(long id) where T : class
    {
        _entries.Add(new Entry(typeof(T), id, null, null));
        return this;
    }

    /// <summary>The serializer for types registered with <see cref="Map{T}"/>.</summary>
    public MessageSetBuilder<TIn, TOut> Serializer(IMessageSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _serializer = serializer;
        return this;
    }

    internal IReadOnlyCollection<Type> InboundTypes
    {
        get
        {
            var types = new List<Type>();
            foreach (Entry entry in _entries)
                if (typeof(TIn).IsAssignableFrom(entry.Type))
                    types.Add(entry.Type);
            return types;
        }
    }

    internal IReadOnlyCollection<Type> OutboundTypes
    {
        get
        {
            var types = new List<Type>();
            foreach (Entry entry in _entries)
                if (typeof(TOut).IsAssignableFrom(entry.Type))
                    types.Add(entry.Type);
            return types;
        }
    }

    internal MessageSetCodec<TIn, TOut>? Compile()
    {
        var ids = new HashSet<long>();
        var types = new HashSet<Type>();
        foreach (Entry entry in _entries)
        {
            if (entry.Id < 0 || entry.Id > _discriminator.Encoding.MaxValue)
                Errors.Add($"Message id {entry.Id} of {entry.Type.Name} cannot be encoded as {_discriminator}.");
            if (!ids.Add(entry.Id))
                Errors.Add($"Message id {entry.Id} is registered twice.");
            if (!types.Add(entry.Type))
                Errors.Add($"{entry.Type.Name} is registered twice.");
            if (!typeof(TIn).IsAssignableFrom(entry.Type) && !typeof(TOut).IsAssignableFrom(entry.Type))
                Errors.Add($"{entry.Type.Name} is neither a {typeof(TIn).Name} nor a {typeof(TOut).Name}.");
            if (entry.Read is null && _serializer is null)
                Errors.Add($"{entry.Type.Name} is mapped for a serializer, but no serializer was given.");
        }

        if (_entries.Count == 0)
            Errors.Add("The message set registers no messages.");
        if (Errors.Count > 0)
            return null;

        var byId = new Dictionary<long, Entry>();
        var byType = new Dictionary<Type, Entry>();
        foreach (Entry entry in _entries)
        {
            byId[entry.Id] = entry;
            byType[entry.Type] = entry;
        }

        return new MessageSetCodec<TIn, TOut>(_discriminator.Encoding, byId.ToFrozenDictionary(), byType.ToFrozenDictionary(), _serializer);
    }

    private static bool ReadWire<T>(ref WireReader reader, out object? message) where T : class, IWireMessage<T>
    {
        if (T.TryRead(ref reader, out T value) && reader.IsAtEnd)
        {
            message = value;
            return true;
        }

        message = null;
        return false;
    }

    private static void WriteWire<T>(object message, IBufferWriter<byte> output) where T : class, IWireMessage<T>
    {
        var writer = new WireWriter(output);
        ((T)message).Write(ref writer);
    }

    internal delegate bool ReadFunc(ref WireReader reader, out object? message);

    internal sealed record Entry(Type Type, long Id, ReadFunc? Read, Action<object, IBufferWriter<byte>>? Write);
}

/// <summary>The compiled message set; stateless and shared by every session.</summary>
internal sealed class MessageSetCodec<TIn, TOut>(
    LengthPrefix discriminator,
    FrozenDictionary<long, MessageSetBuilder<TIn, TOut>.Entry> byId,
    FrozenDictionary<Type, MessageSetBuilder<TIn, TOut>.Entry> byType,
    IMessageSerializer? serializer) : IFrameCodec<TIn, TOut>
    where TIn : class
    where TOut : class
{
    public DecodeResult Decode(in Frame frame, out TIn message)
    {
        message = null!;
        var reader = new WireReader(frame.Memory);
        long id = reader.Length(discriminator, discriminator.MaxValue);
        if (!reader.Ok)
            return DecodeResult.Invalid(ViolationCode.Malformed, "A frame has no message id.");
        if (!byId.TryGetValue(id, out MessageSetBuilder<TIn, TOut>.Entry? entry) || !typeof(TIn).IsAssignableFrom(entry.Type))
            return DecodeResult.Invalid(ViolationCode.UnexpectedMessage, $"Message id {id} is not a message this side receives.");

        object? decoded;
        bool ok = entry.Read is not null
            ? entry.Read(ref reader, out decoded)
            : serializer!.TryDeserialize(entry.Type, frame.Memory[reader.Position..], out decoded);
        if (!ok || decoded is not TIn typed)
            return DecodeResult.Invalid(ViolationCode.Malformed, $"Message id {id} ({entry.Type.Name}) has a malformed body.");

        message = typed;
        return DecodeResult.Ok;
    }

    public void Encode(TOut message, IBufferWriter<byte> output)
    {
        Type type = message.GetType();
        if (!byType.TryGetValue(type, out MessageSetBuilder<TIn, TOut>.Entry? entry))
            throw new ProtocolStateException($"{type.Name} is not registered in the message set.");

        discriminator.WriteTo(entry.Id, output);
        if (entry.Write is not null)
            entry.Write(message, output);
        else
            serializer!.Serialize(type, message, output);
    }
}
