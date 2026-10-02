using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ProtoStream.Tests.Conventions;

/// <summary>
/// Guards binary compatibility of the shipped packages: a compiled downstream package must keep
/// working when a minor version adds members.
/// </summary>
public sealed class PublicSurfaceTests
{
    public static TheoryData<string> ShippedAssemblies => new()
    {
        "ProtoStream", "ProtoStream.Http", "ProtoStream.WebSockets", "ProtoStream.Testing",
    };

    // An optional parameter is baked into the caller at compile time; adding one later, or adding a
    // parameter next to one, is a MissingMethodException in every package compiled against the old shape.
    [Theory]
    [MemberData(nameof(ShippedAssemblies))]
    public void NoPublicMemberTakesAnOptionalParameter(string assemblyName)
    {
        var offenders = PublicMembers(assemblyName)
            .OfType<MethodBase>()
            .Where(m => !IsOverride(m))
            .Where(m => m.GetParameters().Any(p => p.IsOptional))
            .Select(m => $"{m.DeclaringType}.{m.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "Optional parameters on public API: " + string.Join(", ", offenders));
    }

    // A positional record's constructor and Deconstruct grow with every new member. Records that may grow
    // use init/required properties; the few that never can say why with [ClosedShape].
    [Theory]
    [MemberData(nameof(ShippedAssemblies))]
    public void NoPublicPositionalRecordWithoutAClosedShape(string assemblyName)
    {
        var offenders = Assembly.Load(assemblyName).GetExportedTypes()
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

    private static IEnumerable<MemberInfo> PublicMembers(string assemblyName) =>
        Assembly.Load(assemblyName).GetExportedTypes().SelectMany(t => t.GetMembers(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Concat(Assembly.Load(assemblyName).GetExportedTypes().SelectMany(t => t.GetMembers(
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m is MethodBase { IsFamily: true } or MethodBase { IsFamilyOrAssembly: true })));
}
