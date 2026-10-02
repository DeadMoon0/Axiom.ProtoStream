using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Axiom.ProtoStream.Tests.Shared;

/// <summary>
/// Guards binary compatibility of a shipped package: a compiled downstream package must keep working when a
/// minor version adds members. Each test project checks its own package by deriving with a public type of it.
/// </summary>
/// <typeparam name="TAnchor">Any public type of the package under test.</typeparam>
public abstract class PublicSurfaceTests<TAnchor>
{
    private static Assembly Package => typeof(TAnchor).Assembly;

    // An optional parameter is baked into the caller at compile time; adding one later, or adding a
    // parameter next to one, is a MissingMethodException in every package compiled against the old shape.
    [Fact]
    public void NoPublicMemberTakesAnOptionalParameter()
    {
        var offenders = PublicMembers()
            .OfType<MethodBase>()
            .Where(m => !IsOverride(m))
            .Where(m => m.GetParameters().Any(p => p.IsOptional))
            .Select(m => $"{m.DeclaringType}.{m.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "Optional parameters on public API: " + string.Join(", ", offenders));
    }

    // A positional record's constructor and Deconstruct grow with every new member. Records that may grow
    // use init/required properties; the few that never can say why with [ClosedShape].
    [Fact]
    public void NoPublicPositionalRecordWithoutAClosedShape()
    {
        var offenders = Package.GetExportedTypes()
            .Where(IsRecord)
            .Where(t => t.GetMethod("Deconstruct", BindingFlags.Public | BindingFlags.Instance) is not null)
            .Where(t => !t.GetCustomAttributes(inherit: false).Any(a => a.GetType().Name == "ClosedShapeAttribute"))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(offenders.Count == 0, "Positional records without [ClosedShape]: " + string.Join(", ", offenders));
    }

    // An override inherits its signature from the base class (PipeReader.ReadAsync has an optional token);
    // the rule is about signatures this library chooses.
    private static bool IsOverride(MethodBase method) =>
        method is MethodInfo info && info.GetBaseDefinition().DeclaringType != info.DeclaringType;

    // Records are recognised by the compiler-generated EqualityContract (classes) or PrintMembers (structs).
    private static bool IsRecord(Type t) =>
        t.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null
        || t.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    private static IEnumerable<MemberInfo> PublicMembers() =>
        Package.GetExportedTypes().SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Concat(Package.GetExportedTypes().SelectMany(t => t.GetMembers(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m is MethodBase { IsFamily: true } or MethodBase { IsFamilyOrAssembly: true })));
}
