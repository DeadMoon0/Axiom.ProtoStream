![Icon](https://raw.githubusercontent.com/DeadMoon0/Axiom/refs/heads/main/Assets/Icon.svg)

# Axiom.ProtoStream.Testing

Test harnesses for [Axiom.ProtoStream](https://www.nuget.org/packages/Axiom.ProtoStream) protocol authors. Framework
agnostic: failures throw `HarnessAssertionException`, which every test runner reports.

```csharp
// Every message survives encoding and decoding, with the bytes split at every possible position.
CodecHarness.VerifyRoundTrip(definition, messages, (sent, received) => sent.Equals(received));

// Thousands of mutated inputs: only messages, incomplete input and violations are acceptable outcomes.
CodecHarness.Fuzz(definition, samples, seed: 42, iterations: 20_000);

// A codec that reuses message objects must not leak data from one message into the next.
MessageReuseContract.Verify(definition, firstInput, secondInput, message => message.ToString());

// The definition linter found nothing.
DefinitionAssert.NoWarnings(definition);
```

Transports for session tests: `InMemoryTransport.CreatePair()` (cross-wired pipes) and `ScriptedTransport`
(input delivered in chosen chunks, each a separate buffer segment, output captured).

The built-in HTTP and WebSocket packages are tested with these same harnesses.
