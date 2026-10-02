using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Axiom.ProtoStream.Definition;

internal enum InboundAction : byte
{
    Delegate,
    Respond,
    Wait,
}

internal readonly struct InboundTransition<TIn, TOut>
{
    public bool IsDefined { get; init; }

    public InboundAction Action { get; init; }

    /// <summary>Next state id, or -1 to stay.</summary>
    public int Next { get; init; }

    public Func<TIn, TOut>? Responder { get; init; }
}

internal readonly struct OutboundTransition<TOut>
{
    public bool IsDefined { get; init; }

    /// <summary>Next state id, or -1 to stay.</summary>
    public int Next { get; init; }

    /// <summary>Next state id when <see cref="Predicate"/> is false.</summary>
    public int NextIfFalse { get; init; }

    public Func<TOut, bool>? Predicate { get; init; }

    public int Resolve(TOut message) => Predicate is null || Predicate(message) ? Next : NextIfFalse;
}

internal sealed class CompiledState<TIn, TOut>
{
    public required string Name { get; init; }

    public required int Id { get; init; }

    public required bool IsFinal { get; init; }

    public required bool IsSwitchable { get; init; }

    /// <summary>Index into the definition's state readers, or -1 for the codec.</summary>
    public required int ReaderIndex { get; init; }

    public required Func<TOut>? OnEnter { get; init; }

    /// <summary>Indexed by inbound type id.</summary>
    public required InboundTransition<TIn, TOut>[] Inbound { get; init; }

    /// <summary>Indexed by outbound type id.</summary>
    public required OutboundTransition<TOut>[] Outbound { get; init; }
}

/// <summary>Maps message types to dense ids for array-indexed transition lookup.</summary>
internal sealed class TypeMap
{
    private readonly FrozenDictionary<Type, int> _ids;

    public TypeMap(IEnumerable<Type> types)
    {
        var ids = new Dictionary<Type, int>();
        foreach (Type type in types)
            ids.TryAdd(type, ids.Count);
        _ids = ids.ToFrozenDictionary();
        Types = [.. ids.Keys];
    }

    public int Count => _ids.Count;

    public Type[] Types { get; }

    /// <summary>The id of <paramref name="type"/> or of its nearest described base type; -1 when none.</summary>
    public int Find(Type type)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (_ids.TryGetValue(current, out int id))
                return id;
        }

        return -1;
    }
}
