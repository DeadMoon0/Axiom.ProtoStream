using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Showcase.Server;

/// <summary>The web app, embedded in the executable and loaded once.</summary>
internal static class StaticFiles
{
    private const string Prefix = "wwwroot/";

    private static readonly FrozenDictionary<string, (byte[] Content, string ContentType)> Files = Load();

    public static bool TryGet(string path, out byte[] content, out string contentType)
    {
        string name = path == "/" ? "index.html" : path.TrimStart('/');
        if (Files.TryGetValue(name, out var file))
        {
            content = file.Content;
            contentType = file.ContentType;
            return true;
        }

        content = [];
        contentType = string.Empty;
        return false;
    }

    private static FrozenDictionary<string, (byte[], string)> Load()
    {
        Assembly assembly = typeof(StaticFiles).Assembly;
        var files = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal))
                continue;

            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            string name = resource[Prefix.Length..];
            files[name] = (copy.ToArray(), ContentTypeOf(name));
        }

        return files.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static string ContentTypeOf(string name) => Path.GetExtension(name) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
