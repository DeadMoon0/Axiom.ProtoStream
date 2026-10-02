using System;

namespace Axiom.ProtoStream;

/// <summary>
/// Marks a public positional record (or record struct) whose shape is deliberately closed.
/// </summary>
/// <remarks>
/// Adding a member to a positional record changes its constructor and <c>Deconstruct</c> signatures,
/// which breaks every already-compiled package that calls them. Public records therefore use
/// <c>init</c>/<c>required</c> properties, unless their shape can never grow — that exception must be
/// stated here, and the PublicSurfaceCanGrow convention test refuses positional records without it.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
internal sealed class ClosedShapeAttribute(string reason) : Attribute
{
    /// <summary>Why the shape can never grow.</summary>
    public string Reason { get; } = reason;
}
