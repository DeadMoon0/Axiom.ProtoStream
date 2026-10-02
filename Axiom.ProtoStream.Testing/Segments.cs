using System;
using System.Buffers;
using System.Collections.Generic;

namespace Axiom.ProtoStream.Testing;

/// <summary>
/// Builds multi-segment <see cref="ReadOnlySequence{T}"/> values, the shape real network input arrives in.
/// </summary>
/// <remarks>
/// A codec that only ever sees single-segment input in tests will break on the first message that
/// straddles two network reads. Splitting the same bytes at every position catches that class of bug.
/// </remarks>
public static class Segments
{
    /// <summary>Splits <paramref name="data"/> into segments at the given ascending offsets.</summary>
    public static ReadOnlySequence<byte> Split(ReadOnlyMemory<byte> data, params int[] cuts)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        var parts = new List<ReadOnlyMemory<byte>>(cuts.Length + 1);
        int start = 0;
        foreach (int cut in cuts)
        {
            if (cut < start || cut > data.Length)
                throw new ArgumentOutOfRangeException(nameof(cuts), "Cuts must be ascending offsets inside the data.");
            parts.Add(data[start..cut]);
            start = cut;
        }

        parts.Add(data[start..]);
        return Join(parts);
    }

    /// <summary>One segment per byte: the most fragmented form of <paramref name="data"/>.</summary>
    public static ReadOnlySequence<byte> OneBytePerSegment(ReadOnlyMemory<byte> data)
    {
        var parts = new List<ReadOnlyMemory<byte>>(data.Length);
        for (int i = 0; i < data.Length; i++)
            parts.Add(data.Slice(i, 1));
        return Join(parts);
    }

    /// <summary>The data split in two at every possible position, plus the one-byte-per-segment form.</summary>
    public static IEnumerable<ReadOnlySequence<byte>> EverySplit(ReadOnlyMemory<byte> data)
    {
        yield return new ReadOnlySequence<byte>(data);
        for (int cut = 1; cut < data.Length; cut++)
            yield return Split(data, cut);
        yield return OneBytePerSegment(data);
    }

    /// <summary>Joins blocks into one sequence without copying them.</summary>
    public static ReadOnlySequence<byte> Join(IReadOnlyList<ReadOnlyMemory<byte>> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
            return ReadOnlySequence<byte>.Empty;

        var first = new Segment(parts[0], 0);
        Segment last = first;
        for (int i = 1; i < parts.Count; i++)
            last = last.Append(parts[i]);
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
