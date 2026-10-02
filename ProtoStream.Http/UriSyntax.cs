using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace ProtoStream.Http;

/// <summary>The URI grammar of RFC 3986 that request targets and Host fields are checked against.</summary>
internal static class UriSyntax
{
    public const byte PercentSign = (byte)'%';
    public const byte At = (byte)'@';
    public const byte OpenBracket = (byte)'[';
    public const byte CloseBracket = (byte)']';
    public const byte FutureVersion = (byte)'v';
    public const byte FutureVersionUpper = (byte)'V';

    /// <summary>Length of a percent-encoded octet: "%" HEXDIG HEXDIG.</summary>
    public const int PercentEncodedLength = 3;

    /// <summary>Longest textual IPv6 address, including an embedded IPv4 part.</summary>
    public const int MaxIpv6Length = 45;

    private const string Alpha = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const string Digit = "0123456789";
    private const string UnreservedMarks = "-._~";
    private const string SubDelims = "!$&'()*+,;=";

    /// <summary>pchar without pct-encoded, plus "/": the characters of a path.</summary>
    private static readonly SearchValues<byte> PathChars = Create(Alpha + Digit + UnreservedMarks + SubDelims + ":@/%");

    /// <summary>query = *( pchar / "/" / "?" ).</summary>
    private static readonly SearchValues<byte> QueryChars = Create(Alpha + Digit + UnreservedMarks + SubDelims + ":@/?%");

    /// <summary>reg-name = *( unreserved / pct-encoded / sub-delims ).</summary>
    private static readonly SearchValues<byte> RegNameChars = Create(Alpha + Digit + UnreservedMarks + SubDelims + "%");

    /// <summary>userinfo = *( unreserved / pct-encoded / sub-delims / ":" ).</summary>
    private static readonly SearchValues<byte> UserInfoChars = Create(Alpha + Digit + UnreservedMarks + SubDelims + ":%");

    /// <summary>scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ).</summary>
    private static readonly SearchValues<byte> SchemeChars = Create(Alpha + Digit + "+-.");

    /// <summary>The tail of IPvFuture: 1*( unreserved / sub-delims / ":" ).</summary>
    private static readonly SearchValues<byte> FutureChars = Create(Alpha + Digit + UnreservedMarks + SubDelims + ":");

    /// <summary>The characters an IPv6 address is written with.</summary>
    private static readonly SearchValues<byte> Ipv6Chars = Create(Digit + "ABCDEFabcdef:.");

    public static bool IsAbsolutePath(ReadOnlySpan<byte> path) => !path.IsEmpty && path[0] == HttpGrammar.Slash && Matches(path, PathChars);

    /// <summary>path-abempty: empty, or an absolute path.</summary>
    public static bool IsPathAbEmpty(ReadOnlySpan<byte> path) => path.IsEmpty || IsAbsolutePath(path);

    /// <summary>A path of a URI without authority (path-absolute, path-rootless or path-empty).</summary>
    public static bool IsPathWithoutAuthority(ReadOnlySpan<byte> path) =>
        Matches(path, PathChars) && !path.StartsWith("//"u8);

    public static bool IsQuery(ReadOnlySpan<byte> query) => Matches(query, QueryChars);

    public static bool IsScheme(ReadOnlySpan<byte> scheme) =>
        !scheme.IsEmpty && char.IsAsciiLetter((char)scheme[0]) && scheme.IndexOfAnyExcept(SchemeChars) < 0;

    public static bool IsPort(ReadOnlySpan<byte> port) => port.IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0;

    public static bool IsUserInfo(ReadOnlySpan<byte> userInfo) => Matches(userInfo, UserInfoChars);

    /// <summary>host = IP-literal / IPv4address / reg-name. IPv4 addresses are a subset of reg-name.</summary>
    public static bool IsHost(ReadOnlySpan<byte> host)
    {
        if (!host.IsEmpty && host[0] == OpenBracket)
            return host.Length > 2 && host[^1] == CloseBracket && IsIpLiteral(host[1..^1]);
        return Matches(host, RegNameChars);
    }

    /// <summary>Splits "host [ ":" port ]"; false when the shape is wrong (not whether each part is valid).</summary>
    public static bool TrySplitHostPort(ReadOnlySpan<byte> hostPort, out ReadOnlySpan<byte> host, out ReadOnlySpan<byte> port, out bool hasPort)
    {
        host = hostPort;
        port = default;
        hasPort = false;
        int searchFrom = 0;
        if (!hostPort.IsEmpty && hostPort[0] == OpenBracket)
        {
            int close = hostPort.IndexOf(CloseBracket);
            if (close < 0)
                return false;
            searchFrom = close + 1;
            if (searchFrom < hostPort.Length && hostPort[searchFrom] != HttpGrammar.Colon)
                return false;
        }

        int colon = hostPort[searchFrom..].IndexOf(HttpGrammar.Colon);
        if (colon >= 0)
        {
            colon += searchFrom;
            host = hostPort[..colon];
            port = hostPort[(colon + 1)..];
            hasPort = true;
        }

        return true;
    }

    /// <summary>uri-host [ ":" port ]: the Host field (RFC 9110 section 7.2) and authority-form.</summary>
    public static bool IsHostAndOptionalPort(ReadOnlySpan<byte> value) =>
        TrySplitHostPort(value, out ReadOnlySpan<byte> host, out ReadOnlySpan<byte> port, out _) && IsHost(host) && IsPort(port);

    /// <summary>Every "%" starts a percent-encoded octet: "%" HEXDIG HEXDIG (RFC 3986 section 2.1).</summary>
    public static bool HasValidPercentEncoding(ReadOnlySpan<byte> value)
    {
        int percent;
        while ((percent = value.IndexOf(PercentSign)) >= 0)
        {
            if (percent + PercentEncodedLength > value.Length || !char.IsAsciiHexDigit((char)value[percent + 1]) || !char.IsAsciiHexDigit((char)value[percent + 2]))
                return false;
            value = value[(percent + PercentEncodedLength)..];
        }

        return true;
    }

    private static bool Matches(ReadOnlySpan<byte> value, SearchValues<byte> allowed) =>
        value.IndexOfAnyExcept(allowed) < 0 && HasValidPercentEncoding(value);

    /// <summary>IP-literal content: IPv6address / IPvFuture. Zone identifiers are not part of RFC 3986 and are refused.</summary>
    private static bool IsIpLiteral(ReadOnlySpan<byte> literal)
    {
        if (literal[0] is FutureVersion or FutureVersionUpper)
        {
            // IPvFuture = "v" 1*HEXDIG "." 1*( unreserved / sub-delims / ":" )
            int dot = literal.IndexOf(HttpGrammar.Dot);
            return dot > 1
                && IsHexDigits(literal[1..dot])
                && dot + 1 < literal.Length
                && literal[(dot + 1)..].IndexOfAnyExcept(FutureChars) < 0;
        }

        if (literal.Length > MaxIpv6Length || literal.IndexOfAnyExcept(Ipv6Chars) >= 0)
            return false;
        Span<char> text = stackalloc char[literal.Length];
        for (int i = 0; i < literal.Length; i++)
            text[i] = (char)literal[i];
        return IPAddress.TryParse(text, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static bool IsHexDigits(ReadOnlySpan<byte> value)
    {
        foreach (byte b in value)
        {
            if (!char.IsAsciiHexDigit((char)b))
                return false;
        }

        return true;
    }

    private static SearchValues<byte> Create(string characters)
    {
        var bytes = new byte[characters.Length];
        for (int i = 0; i < characters.Length; i++)
            bytes[i] = (byte)characters[i];
        return SearchValues.Create(bytes);
    }
}
