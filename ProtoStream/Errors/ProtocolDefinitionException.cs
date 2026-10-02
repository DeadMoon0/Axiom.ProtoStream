using System;
using System.Collections.Generic;

namespace ProtoStream.Errors;

/// <summary>
/// A protocol description is inconsistent. Thrown by <c>Build()</c>, never per connection.
/// </summary>
/// <remarks>
/// All problems found are reported at once in <see cref="Errors"/>, so fixing a definition takes one
/// round trip instead of one per mistake.
/// </remarks>
public sealed class ProtocolDefinitionException : ProtoStreamException
{
    /// <summary>Creates the exception for the given protocol and the list of problems found.</summary>
    public ProtocolDefinitionException(string protocolName, IReadOnlyList<string> errors)
        : base(FormatMessage(protocolName, errors))
    {
        ProtocolName = protocolName;
        Errors = errors;
    }

    /// <summary>Name of the protocol whose description failed.</summary>
    public string ProtocolName { get; }

    /// <summary>Every problem found, one sentence each.</summary>
    public IReadOnlyList<string> Errors { get; }

    private static string FormatMessage(string protocolName, IReadOnlyList<string> errors) =>
        $"Protocol '{protocolName}' is not valid ({errors.Count} problem(s)):{Environment.NewLine} - "
        + string.Join(Environment.NewLine + " - ", errors);
}
