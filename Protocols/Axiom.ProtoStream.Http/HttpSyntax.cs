using System;
using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace Axiom.ProtoStream.Http;

/// <summary>The literal pieces of HTTP/1.1 message syntax (RFC 9112), named.</summary>
internal static class HttpGrammar
{
    public const byte Space = (byte)' ';
    public const byte HorizontalTab = (byte)'\t';
    public const byte Colon = (byte)':';
    public const byte Comma = (byte)',';
    public const byte Semicolon = (byte)';';
    public const byte EqualsSign = (byte)'=';
    public const byte Quote = (byte)'"';
    public const byte Backslash = (byte)'\\';
    public const byte Slash = (byte)'/';
    public const byte QuestionMark = (byte)'?';
    public const byte Asterisk = (byte)'*';
    public const byte Dot = (byte)'.';

    /// <summary>The first byte that is not a control character (CTL); every byte below it is one.</summary>
    public const byte FirstPrintable = 0x20;

    /// <summary>DEL, the one control character above <see cref="FirstPrintable"/>.</summary>
    public const byte Delete = 0x7F;

    /// <summary>First visible US-ASCII character (VCHAR).</summary>
    public const byte FirstVisible = (byte)'!';

    /// <summary>Last visible US-ASCII character (VCHAR).</summary>
    public const byte LastVisible = (byte)'~';

    /// <summary>Most decimal digits of a non-negative 64-bit value.</summary>
    public const int MaxDecimalDigits = 19;

    /// <summary>Most decimal digits accepted for Content-Length: lengths that cannot overflow a 64-bit value.</summary>
    public const int MaxContentLengthDigits = 18;

    public static ReadOnlySpan<byte> Crlf => "\r\n"u8;

    public static ReadOnlySpan<byte> HeadEnd => "\r\n\r\n"u8;

    public static ReadOnlySpan<byte> FieldSeparator => ": "u8;

    public static ReadOnlySpan<byte> Http11 => "HTTP/1.1"u8;

    public static ReadOnlySpan<byte> Http10 => "HTTP/1.0"u8;

    public static ReadOnlySpan<byte> HttpVersionPrefix => "HTTP/"u8;

    public static ReadOnlySpan<byte> ContentLength => "Content-Length"u8;

    public static ReadOnlySpan<byte> TransferEncoding => "Transfer-Encoding"u8;

    public static ReadOnlySpan<byte> Host => "Host"u8;

    public static ReadOnlySpan<byte> Connection => "Connection"u8;

    public static ReadOnlySpan<byte> Upgrade => "Upgrade"u8;

    public static ReadOnlySpan<byte> Expect => "Expect"u8;

    public static ReadOnlySpan<byte> Date => "Date"u8;

    public static ReadOnlySpan<byte> Chunked => "chunked"u8;

    public static ReadOnlySpan<byte> Close => "close"u8;

    public static ReadOnlySpan<byte> KeepAlive => "keep-alive"u8;

    public static ReadOnlySpan<byte> ContinueExpectation => "100-continue"u8;

    public static ReadOnlySpan<byte> ContinueResponse => "HTTP/1.1 100 Continue\r\n\r\n"u8;

    public static ReadOnlySpan<byte> TransferEncodingChunkedField => "Transfer-Encoding: chunked\r\n"u8;

    public static ReadOnlySpan<byte> ConnectionUpgradeField => "Connection: upgrade\r\n"u8;

    public static ReadOnlySpan<byte> ConnectionCloseField => "Connection: close\r\n"u8;

    public static ReadOnlySpan<byte> ConnectionKeepAliveField => "Connection: keep-alive\r\n"u8;

    /// <summary>"HTTP/" DIGIT "." DIGIT (RFC 9112 section 2.3): well-formed, whether supported or not.</summary>
    public static bool IsHttpVersion(ReadOnlySpan<byte> text)
    {
        if (!text.StartsWith(HttpVersionPrefix))
            return false;
        ReadOnlySpan<byte> number = text[HttpVersionPrefix.Length..];
        return number.Length == VersionNumberLength && char.IsAsciiDigit((char)number[0]) && number[1] == Dot && char.IsAsciiDigit((char)number[2]);
    }

    /// <summary>Length of a version number: DIGIT "." DIGIT.</summary>
    private const int VersionNumberLength = 3;
}

/// <summary>Character classes and small validators of RFC 9110 / 9112.</summary>
internal static class HttpSyntax
{
    /// <summary>tchar: the characters of methods, field names and tokens (RFC 9110 section 5.6.2).</summary>
    public static readonly SearchValues<byte> Token = SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8);

    /// <summary>Bytes a field value must not contain: controls other than HTAB, and DEL (RFC 9110 section 5.5).</summary>
    public static readonly SearchValues<byte> InvalidFieldValue = SearchValues.Create(Controls(allowTab: true));

    public static bool IsToken(ReadOnlySpan<byte> value) => !value.IsEmpty && value.IndexOfAnyExcept(Token) < 0;

    public static bool IsToken(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return false;
        foreach (char c in value)
        {
            if (!char.IsAscii(c) || !Token.Contains((byte)c))
                return false;
        }

        return true;
    }

    public static bool IsFieldValue(ReadOnlySpan<byte> value) => value.IndexOfAny(InvalidFieldValue) < 0;

    /// <summary>A value that can be written as field octets: Latin-1 without controls other than HTAB.</summary>
    public static bool IsFieldValue(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (c > byte.MaxValue || ((c < HttpGrammar.FirstPrintable || c == HttpGrammar.Delete) && c != HttpGrammar.HorizontalTab))
                return false;
        }

        return true;
    }

    public static bool IsVisibleAscii(ReadOnlySpan<byte> value) =>
        !value.IsEmpty && value.IndexOfAnyExceptInRange(HttpGrammar.FirstVisible, HttpGrammar.LastVisible) < 0;

    public static bool IsWhitespace(byte value) => value is HttpGrammar.Space or HttpGrammar.HorizontalTab;

    public static int LeadingWhitespace(ReadOnlySpan<byte> value)
    {
        int count = 0;
        while (count < value.Length && IsWhitespace(value[count]))
            count++;
        return count;
    }

    public static ReadOnlySpan<byte> TrimWhitespace(ReadOnlySpan<byte> value)
    {
        int start = LeadingWhitespace(value);
        int end = value.Length;
        while (end > start && IsWhitespace(value[end - 1]))
            end--;
        return value[start..end];
    }

    /// <summary>1*DIGIT that fits a non-negative 64-bit value; no sign, no whitespace, no list.</summary>
    public static bool TryParseDecimal(ReadOnlySpan<byte> value, out long result)
    {
        result = 0;
        return !value.IsEmpty
            && value.Length <= HttpGrammar.MaxContentLengthDigits
            && value.IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0
            && Utf8Parser.TryParse(value, out result, out _);
    }

    /// <summary>
    /// Calls <paramref name="visit"/> for each non-empty element of a comma-separated list (RFC 9110 section
    /// 5.6.1), trimmed. Empty elements are ignored as the RFC requires. Stops when the visitor returns false.
    /// </summary>
    public static bool ForEachListElement<TState>(ReadOnlySpan<byte> list, ref TState state, ListVisitor<TState> visit)
    {
        while (true)
        {
            int comma = list.IndexOf(HttpGrammar.Comma);
            ReadOnlySpan<byte> element = TrimWhitespace(comma < 0 ? list : list[..comma]);
            if (!element.IsEmpty && !visit(element, ref state))
                return false;
            if (comma < 0)
                return true;
            list = list[(comma + 1)..];
        }
    }

    public delegate bool ListVisitor<TState>(ReadOnlySpan<byte> element, ref TState state);

    /// <summary>True when the list contains <paramref name="token"/>, ignoring case.</summary>
    public static bool ListContains(ReadOnlySpan<byte> list, ReadOnlySpan<byte> token)
    {
        while (true)
        {
            int comma = list.IndexOf(HttpGrammar.Comma);
            ReadOnlySpan<byte> element = TrimWhitespace(comma < 0 ? list : list[..comma]);
            if (Ascii.EqualsIgnoreCase(element, token))
                return true;
            if (comma < 0)
                return false;
            list = list[(comma + 1)..];
        }
    }

    /// <summary>
    /// A quoted-string at the start of <paramref name="value"/> (RFC 9110 section 5.6.4); returns its length
    /// including the quotes, or -1 when it is malformed.
    /// </summary>
    public static int QuotedStringLength(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty || value[0] != HttpGrammar.Quote)
            return -1;
        for (int i = 1; i < value.Length; i++)
        {
            byte c = value[i];
            if (c == HttpGrammar.Quote)
                return i + 1;
            if (c == HttpGrammar.Backslash)
            {
                // quoted-pair = "\" ( HTAB / SP / VCHAR / obs-text )
                if (++i == value.Length || IsControl(value[i]) && value[i] != HttpGrammar.HorizontalTab)
                    return -1;
                continue;
            }

            if (IsControl(c) && c != HttpGrammar.HorizontalTab)
                return -1;
        }

        return -1;
    }

    private static bool IsControl(byte value) => value < HttpGrammar.FirstPrintable || value == HttpGrammar.Delete;

    private static byte[] Controls(bool allowTab)
    {
        var controls = new System.Collections.Generic.List<byte>();
        for (int b = 0; b < HttpGrammar.FirstPrintable; b++)
        {
            if (!allowTab || b != HttpGrammar.HorizontalTab)
                controls.Add((byte)b);
        }

        controls.Add(HttpGrammar.Delete);
        return [.. controls];
    }
}
