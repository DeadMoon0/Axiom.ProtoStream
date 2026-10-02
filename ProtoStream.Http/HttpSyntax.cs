using System;
using System.Buffers;

namespace ProtoStream.Http;

/// <summary>Character classes of RFC 9110 / 9112, shared by the parser and the response validation.</summary>
internal static class HttpSyntax
{
    /// <summary>tchar: the characters of methods and header names.</summary>
    public static readonly SearchValues<byte> Token = SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8);

    /// <summary>Bytes a field value must not contain: controls other than HTAB, and DEL.</summary>
    public static readonly SearchValues<byte> InvalidValue = SearchValues.Create(
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
         0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x7F]);

    public static bool IsToken(ReadOnlySpan<byte> value) => !value.IsEmpty && value.IndexOfAnyExcept(Token) < 0;

    public static bool IsToken(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return false;
        foreach (char c in value)
        {
            if (c > 0x7F || !Token.Contains((byte)c))
                return false;
        }

        return true;
    }

    public static bool IsFieldValue(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (c > 0xFF || (c < 0x20 && c != '\t') || c == 0x7F)
                return false;
        }

        return true;
    }

    public static ReadOnlySpan<byte> TrimWhitespace(ReadOnlySpan<byte> value)
    {
        int start = 0;
        int end = value.Length;
        while (start < end && (value[start] == ' ' || value[start] == '\t'))
            start++;
        while (end > start && (value[end - 1] == ' ' || value[end - 1] == '\t'))
            end--;
        return value[start..end];
    }

    /// <summary>True when the comma-separated list <paramref name="list"/> contains <paramref name="token"/>, ignoring case.</summary>
    public static bool ListContains(ReadOnlySpan<byte> list, ReadOnlySpan<byte> token)
    {
        while (!list.IsEmpty)
        {
            int comma = list.IndexOf((byte)',');
            ReadOnlySpan<byte> item = TrimWhitespace(comma < 0 ? list : list[..comma]);
            if (System.Text.Ascii.EqualsIgnoreCase(item, token))
                return true;
            list = comma < 0 ? default : list[(comma + 1)..];
        }

        return false;
    }

    public static string ReasonPhrase(int status) => status switch
    {
        100 => "Continue",
        101 => "Switching Protocols",
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        204 => "No Content",
        206 => "Partial Content",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        304 => "Not Modified",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        408 => "Request Timeout",
        409 => "Conflict",
        411 => "Length Required",
        413 => "Content Too Large",
        414 => "URI Too Long",
        415 => "Unsupported Media Type",
        426 => "Upgrade Required",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        505 => "HTTP Version Not Supported",
        _ => "",
    };
}
