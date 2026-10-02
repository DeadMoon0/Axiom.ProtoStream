using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Errors;

namespace Axiom.ProtoStream.Testing;

/// <summary>A harness check failed. Test-framework agnostic: every runner reports it as a failure.</summary>
public sealed class HarnessAssertionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public HarnessAssertionException(string message) : base(message) { }

    /// <summary>Creates the exception with its cause.</summary>
    public HarnessAssertionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>What a fuzzing run saw.</summary>
public sealed class FuzzReport
{
    /// <summary>Mutated inputs decoded.</summary>
    public required int Iterations { get; init; }

    /// <summary>Messages decoded across all inputs.</summary>
    public required int Messages { get; init; }

    /// <summary>Inputs that ended in a violation.</summary>
    public required int Invalid { get; init; }

    /// <summary>Inputs that ended inside a message.</summary>
    public required int Incomplete { get; init; }
}

/// <summary>Checks codecs the way the network will exercise them.</summary>
public static class CodecHarness
{
    /// <summary>
    /// Encodes <paramref name="messages"/> with <paramref name="writer"/> and decodes them with
    /// <paramref name="reader"/> from the same bytes split at every position; every message must come back
    /// equal by <paramref name="equals"/> and every byte must be consumed.
    /// </summary>
    public static void VerifyRoundTrip<TWriterIn, TMessage, TRead, TReaderOut>(
        ProtocolDefinition<TWriterIn, TMessage> writer,
        ProtocolDefinition<TRead, TReaderOut> reader,
        IReadOnlyList<TMessage> messages,
        Func<TMessage, TRead, bool> equals)
        where TWriterIn : class
        where TMessage : class
        where TRead : class
        where TReaderOut : class
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(equals);

        var encoded = new ArrayBufferWriter<byte>();
        using (var encoder = new CodecRunner<TWriterIn, TMessage>(writer))
        {
            foreach (TMessage message in messages)
                encoder.Encode(message, encoded);
        }

        byte[] wire = encoded.WrittenSpan.ToArray();
        int split = 0;
        foreach (ReadOnlySequence<byte> input in Splits(wire))
        {
            using var decoder = new CodecRunner<TRead, TReaderOut>(reader);
            ReadOnlySequence<byte> rest = input;
            for (int i = 0; i < messages.Count; i++)
            {
                DecodeOutcome<TRead> outcome = decoder.Decode(rest, isCompleted: true);
                if (outcome.Status != DecodeStatus.Message)
                    throw new HarnessAssertionException($"Split #{split}: message {i} decoded as {outcome.Status} ({outcome.Code}: {outcome.Detail}).");
                if (!equals(messages[i], outcome.Message!))
                    throw new HarnessAssertionException($"Split #{split}: message {i} came back different.");
                rest = rest.Slice(outcome.Consumed);
            }

            if (!rest.IsEmpty)
                throw new HarnessAssertionException($"Split #{split}: {rest.Length} bytes were left after the last message.");
            split++;
        }
    }

    /// <summary>The same check for a protocol whose two directions use the same messages.</summary>
    public static void VerifyRoundTrip<TMessage>(ProtocolDefinition<TMessage, TMessage> definition, IReadOnlyList<TMessage> messages, Func<TMessage, TMessage, bool> equals)
        where TMessage : class
        => VerifyRoundTrip<TMessage, TMessage, TMessage, TMessage>(definition, definition, messages, equals);

    /// <summary>
    /// Decodes <paramref name="iterations"/> random mutations of <paramref name="samples"/>. The only
    /// acceptable outcomes are messages, an incomplete message and violations; any exception (including a
    /// <see cref="CodecContractException"/>) or a decoder that hangs fails the run. Deterministic for a seed.
    /// </summary>
    public static FuzzReport Fuzz<TIn, TOut>(ProtocolDefinition<TIn, TOut> definition, IReadOnlyList<byte[]> samples, int seed, int iterations)
        where TIn : class
        where TOut : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("Fuzzing needs at least one sample.", nameof(samples));

        int current = -1;
        byte[] currentInput = [];
        int messages = 0, invalid = 0, incomplete = 0;
        var random = new Random(seed);

        Task run = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                Volatile.Write(ref current, i);
                byte[] input = Mutate(samples[random.Next(samples.Count)], random);
                currentInput = input;
                using var runner = new CodecRunner<TIn, TOut>(definition);
                ReadOnlySequence<byte> rest = new(input);
                for (int step = 0; step <= input.Length; step++)
                {
                    DecodeOutcome<TIn> outcome;
                    try
                    {
                        outcome = runner.Decode(rest, isCompleted: true);
                    }
                    catch (Exception ex)
                    {
                        throw new HarnessAssertionException($"Iteration {i} (seed {seed}) threw on input {Convert.ToHexString(input)}.", ex);
                    }

                    if (outcome.Status == DecodeStatus.Message)
                    {
                        messages++;
                        rest = rest.Slice(outcome.Consumed);
                        if (rest.IsEmpty)
                            break;
                        continue;
                    }

                    if (outcome.Status == DecodeStatus.Invalid)
                        invalid++;
                    else
                        incomplete++;
                    break;
                }
            }
        });

        bool finished;
        try
        {
            finished = run.Wait(WatchdogBase + TimeSpan.FromMilliseconds(iterations));
        }
        catch (AggregateException failure) when (failure.InnerException is not null)
        {
            // Wait wraps the failure; callers expect the harness's own exception.
            ExceptionDispatchInfo.Throw(failure.InnerException);
            throw;
        }

        if (!finished)
            throw new HarnessAssertionException($"Iteration {Volatile.Read(ref current)} (seed {seed}) did not finish; the decoder hangs on {Convert.ToHexString(currentInput)}.");

        return new FuzzReport { Iterations = iterations, Messages = messages, Invalid = invalid, Incomplete = incomplete };
    }

    private static IEnumerable<ReadOnlySequence<byte>> Splits(byte[] wire)
    {
        // Every split for small inputs; for large inputs a spread of positions keeps the check fast.
        if (wire.Length <= FullSplitLimit)
        {
            foreach (ReadOnlySequence<byte> split in Segments.EverySplit(wire))
                yield return split;
            yield break;
        }

        yield return new ReadOnlySequence<byte>(wire);
        int step = wire.Length / SampledSplits;
        for (int cut = 1; cut < wire.Length; cut += step)
            yield return Segments.Split(wire, cut);
    }

    /// <summary>Time every fuzzing run may take, on top of one millisecond per iteration, before it counts as hung.</summary>
    private static readonly TimeSpan WatchdogBase = TimeSpan.FromSeconds(30);

    /// <summary>Inputs up to this size are split at every position; larger ones at a spread of positions.</summary>
    private const int FullSplitLimit = 2048;

    /// <summary>Number of split positions tried for a large input.</summary>
    private const int SampledSplits = 512;

    /// <summary>Most mutations applied to one sample.</summary>
    private const int MaxMutations = 4;

    /// <summary>Longest run of bytes a duplication repeats.</summary>
    private const int MaxDuplicatedRun = 15;

    private const int BitsPerByte = 8;

    private enum Mutation
    {
        FlipBit,
        ReplaceByte,
        InsertByte,
        RemoveByte,
        Truncate,
        DuplicateRun,
    }

    private static byte[] Mutate(byte[] sample, Random random)
    {
        var bytes = new List<byte>(sample);
        int mutations = random.Next(1, MaxMutations + 1);
        Mutation[] kinds = Enum.GetValues<Mutation>();
        for (int m = 0; m < mutations; m++)
        {
            int position = bytes.Count == 0 ? 0 : random.Next(bytes.Count);
            Mutation kind = kinds[random.Next(kinds.Length)];
            if (bytes.Count == 0 && kind != Mutation.InsertByte)
                kind = Mutation.InsertByte;

            switch (kind)
            {
                case Mutation.FlipBit:
                    bytes[position] ^= (byte)(1 << random.Next(BitsPerByte));
                    break;
                case Mutation.ReplaceByte:
                    bytes[position] = RandomByte(random);
                    break;
                case Mutation.InsertByte:
                    bytes.Insert(position, RandomByte(random));
                    break;
                case Mutation.RemoveByte:
                    bytes.RemoveAt(position);
                    break;
                case Mutation.Truncate:
                    bytes.RemoveRange(position, bytes.Count - position);
                    break;
                default:
                    int length = Math.Min(bytes.Count - position, random.Next(1, MaxDuplicatedRun + 1));
                    if (length > 0)
                        bytes.InsertRange(position, bytes.GetRange(position, length));
                    break;
            }
        }

        return [.. bytes];
    }

    private static byte RandomByte(Random random) => (byte)random.Next(byte.MaxValue + 1);
}

/// <summary>Assertions about protocol definitions.</summary>
public static class DefinitionAssert
{
    /// <summary>Fails when the definition linter reported anything.</summary>
    public static void NoWarnings<TIn, TOut>(ProtocolDefinition<TIn, TOut> definition)
        where TIn : class
        where TOut : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Warnings.Count > 0)
            throw new HarnessAssertionException($"'{definition.Name}' has warnings:{Environment.NewLine} - " + string.Join(Environment.NewLine + " - ", definition.Warnings));
    }
}

/// <summary>Checks that codecs which reuse message objects reset them completely.</summary>
public static class MessageReuseContract
{
    /// <summary>
    /// Decodes <paramref name="first"/> then <paramref name="second"/> with one codec, and
    /// <paramref name="second"/> alone with a fresh one. Both views of the second message must be equal by
    /// <paramref name="describe"/>; a difference means data from the first message leaked into the second.
    /// </summary>
    public static void Verify<TIn, TOut>(ProtocolDefinition<TIn, TOut> definition, byte[] first, byte[] second, Func<TIn, string> describe)
        where TIn : class
        where TOut : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(describe);

        using var reused = new CodecRunner<TIn, TOut>(definition);
        Decode(reused, first, describe);
        string afterReuse = Decode(reused, second, describe);

        using var fresh = new CodecRunner<TIn, TOut>(definition);
        string alone = Decode(fresh, second, describe);

        if (afterReuse != alone)
            throw new HarnessAssertionException($"Data leaked between messages. After reuse: {afterReuse}{Environment.NewLine}Alone: {alone}");
    }

    private static string Decode<TIn, TOut>(CodecRunner<TIn, TOut> runner, byte[] input, Func<TIn, string> describe)
        where TIn : class
        where TOut : class
    {
        DecodeOutcome<TIn> outcome = runner.Decode(new ReadOnlySequence<byte>(input), isCompleted: true);
        if (outcome.Status != DecodeStatus.Message)
            throw new HarnessAssertionException($"The input did not decode into a message: {outcome.Status} ({outcome.Detail}).");
        return describe(outcome.Message!);
    }
}
