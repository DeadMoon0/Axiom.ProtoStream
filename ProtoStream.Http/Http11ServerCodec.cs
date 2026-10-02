using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using ProtoStream.Codecs;
using ProtoStream.Errors;

namespace ProtoStream.Http;

/// <summary>
/// Parses request heads and writes responses for one connection. Fails closed on everything RFC 9112 calls
/// ambiguous, because ambiguity between two parsers is what request smuggling exploits.
/// </summary>
internal sealed class Http11ServerCodec(Http11Options options) : ICodec<HttpRequest, HttpResponse>
{
    private static ReadOnlySpan<byte> HeadEnd => "\r\n\r\n"u8;

    private readonly HttpRequest[] _requests = [new(), new()];
    private readonly FixedLengthPayloadDecoder _fixedBody = new();
    private readonly ChunkedPayloadDecoder _chunkedBody = new();
    private int _nextRequest;
    private HttpRequestMethod _lastMethod;
    private HttpProtocolVersion _lastVersion = HttpProtocolVersion.Http11;
    private bool _lastKeepAlive;

    public ParseResult TryParse(ref MessageParseContext context, out HttpRequest message)
    {
        message = null!;
        ReadOnlySequence<byte> input = context.Input;
        long window = options.MaxRequestHeadBytes + 4L;
        var reader = new SequenceReader<byte>(input.Length > window ? input.Slice(0, window) : input);

        // RFC 9112 section 2.2: empty lines before the request line are ignored. They stay in the buffer until a
        // request follows, so an endless stream of them still hits the head limit.
        while (reader.IsNext("\r\n"u8, advancePast: true))
        {
        }

        if (reader.End || !reader.TryReadTo(out ReadOnlySequence<byte> _, HeadEnd, advancePastDelimiter: true))
        {
            return input.Length > window
                ? context.Invalid(ViolationCode.LimitExceeded, 431, $"The request head exceeds {options.MaxRequestHeadBytes} bytes.")
                : context.NeedMore();
        }

        DetachedHead head = context.Detach(reader.Position);
        HttpRequest request = _requests[_nextRequest ^= 1];
        int status = ParseHead(head.Memory, context.Stamp, request, out string? error);
        if (status != 0)
            return context.Invalid(CodeFor(status), status, error!);

        IPayloadDecoder decoder;
        if (request.IsChunked)
        {
            _chunkedBody.Reset(options.MaxRequestBodySize, options.MaxTrailerBytes);
            decoder = _chunkedBody;
        }
        else
        {
            _fixedBody.Reset(request.ContentLength ?? 0);
            decoder = _fixedBody;
        }

        _lastMethod = request.Method;
        _lastVersion = request.Version;
        _lastKeepAlive = request.KeepAlive;

        ParseResult result = context.DoneWithPayload(head, decoder, out PipeReader body);
        request.SetBody(body);
        message = request;
        return result;
    }

    public WriteResult Write(HttpResponse response, IBufferWriter<byte> output)
    {
        int status = response.StatusCode;
        bool bodiless = status < 200 || status == 204 || status == 304;
        bool hasStream = response.ContentStream is not null;
        if (hasStream && !response.Content.IsEmpty)
            throw new ProtocolStateException("A response has either Content or a ContentStream, not both.");
        if (bodiless && (hasStream || !response.Content.IsEmpty))
            throw new ProtocolStateException($"A {status} response cannot have a body.");

        bool close = status != 101 && (response.CloseConnection || !_lastKeepAlive);
        bool chunked = false;

        WriteStatusLine(status, response.Reason, output);
        foreach (KeyValuePair<string, string> field in response.Headers.Fields)
            WriteField(field.Key, field.Value, output);

        if (!bodiless)
        {
            if (!hasStream)
                WriteLength("Content-Length: "u8, response.Content.Length, output);
            else if (response.ContentLength is { } length)
                WriteLength("Content-Length: "u8, length, output);
            else if (_lastVersion == HttpProtocolVersion.Http11)
            {
                output.Write("Transfer-Encoding: chunked\r\n"u8);
                chunked = true;
            }
            else
                close = true; // HTTP/1.0 cannot frame an unknown length other than by closing
        }

        if (status == 101)
            output.Write("Connection: upgrade\r\n"u8);
        else if (close)
            output.Write("Connection: close\r\n"u8);
        else if (_lastVersion == HttpProtocolVersion.Http10)
            output.Write("Connection: keep-alive\r\n"u8);
        output.Write("\r\n"u8);

        WriteResult result = WriteResult.Done;
        if (!bodiless && _lastMethod != HttpRequestMethod.Head)
        {
            if (!hasStream)
                output.Write(response.Content.Span);
            else
                result = WriteResult.WithPayload(response.ContentStream!, response.ContentLength, chunked ? ChunkedPayloadEncoder.Instance : IdentityPayloadEncoder.Instance);
        }

        return close ? result.ThenClose() : result;
    }

    /// <returns>0 when the head is valid, otherwise the status code to answer with.</returns>
    private int ParseHead(ReadOnlyMemory<byte> memory, MessageStamp stamp, HttpRequest request, out string? error)
    {
        ReadOnlySpan<byte> head = memory.Span;
        int position = 0;

        // RFC 9112 section 2.2: ignore empty lines received before the request line.
        while (head[position..].StartsWith("\r\n"u8))
            position += 2;

        int lineLength = head[position..].IndexOf("\r\n"u8);
        if (lineLength <= 0)
            return Fail(400, "The request line is missing.", out error);
        int lineEnd = position + lineLength;
        ReadOnlySpan<byte> line = head[position..lineEnd];
        if (line.Length > options.MaxRequestLineBytes)
            return Fail(414, "The request line is too long.", out error);

        int firstSpace = line.IndexOf((byte)' ');
        if (firstSpace <= 0 || !HttpSyntax.IsToken(line[..firstSpace]))
            return Fail(400, "The request method is malformed.", out error);
        ReadOnlySpan<byte> afterMethod = line[(firstSpace + 1)..];
        int secondSpace = afterMethod.IndexOf((byte)' ');
        if (secondSpace <= 0)
            return Fail(400, "The request line is malformed.", out error);

        ReadOnlySpan<byte> target = afterMethod[..secondSpace];
        if (target.IndexOfAnyExceptInRange((byte)0x21, (byte)0x7E) >= 0)
            return Fail(400, "The request target contains invalid characters.", out error);

        ReadOnlySpan<byte> versionText = afterMethod[(secondSpace + 1)..];
        HttpProtocolVersion version;
        if (versionText.SequenceEqual("HTTP/1.1"u8))
            version = HttpProtocolVersion.Http11;
        else if (versionText.SequenceEqual("HTTP/1.0"u8))
            version = HttpProtocolVersion.Http10;
        else if (versionText.Length == 8 && versionText.StartsWith("HTTP/"u8) && char.IsAsciiDigit((char)versionText[5]) && versionText[6] == '.' && char.IsAsciiDigit((char)versionText[7]))
            return Fail(505, "The HTTP version is not supported.", out error);
        else
            return Fail(400, "The HTTP version is malformed.", out error);

        request.Load(memory, stamp, position, firstSpace, position + firstSpace + 1, secondSpace, MethodOf(line[..firstSpace]), version);
        position = lineEnd + 2;

        long? contentLength = null;
        bool hasTransferEncoding = false;
        bool chunked = false;
        int hosts = 0;
        bool connectionClose = false;
        bool connectionKeepAlive = false;
        bool connectionUpgrade = false;
        bool hasUpgrade = false;

        while (true)
        {
            lineEnd = position + head[position..].IndexOf("\r\n"u8);
            if (lineEnd == position)
                break; // the empty line ending the head

            line = head[position..lineEnd];
            if (line[0] is (byte)' ' or (byte)'\t')
                return Fail(400, "Obsolete line folding is not accepted.", out error);

            int colon = line.IndexOf((byte)':');
            if (colon <= 0 || !HttpSyntax.IsToken(line[..colon]))
                return Fail(400, "A header field name is malformed.", out error);

            ReadOnlySpan<byte> name = line[..colon];
            ReadOnlySpan<byte> rawValue = line[(colon + 1)..];
            ReadOnlySpan<byte> value = HttpSyntax.TrimWhitespace(rawValue);
            if (value.IndexOfAny(HttpSyntax.InvalidValue) >= 0)
                return Fail(400, "A header field value contains control characters.", out error);
            if (request.HeaderCount >= options.MaxHeaderCount)
                return Fail(431, $"More than {options.MaxHeaderCount} header fields.", out error);

            int leading = 0;
            while (leading < rawValue.Length && rawValue[leading] is (byte)' ' or (byte)'\t')
                leading++;
            int valueStart = position + colon + 1 + leading;
            request.AddHeader(new HeaderEntry(position, colon, valueStart, value.Length));

            if (Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
            {
                if (value.IsEmpty || value.Length > 18 || value.IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0
                    || !Utf8Parser.TryParse(value, out long length, out _))
                    return Fail(400, "Content-Length is malformed.", out error);
                if (contentLength is { } previous && previous != length)
                    return Fail(400, "Content-Length is declared twice with different values.", out error);
                contentLength = length;
            }
            else if (Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8))
            {
                if (hasTransferEncoding || !Ascii.EqualsIgnoreCase(value, "chunked"u8))
                    return Fail(501, "Only the chunked transfer coding is supported.", out error);
                hasTransferEncoding = true;
                chunked = true;
            }
            else if (Ascii.EqualsIgnoreCase(name, "Host"u8))
            {
                hosts++;
            }
            else if (Ascii.EqualsIgnoreCase(name, "Connection"u8))
            {
                connectionClose |= HttpSyntax.ListContains(value, "close"u8);
                connectionKeepAlive |= HttpSyntax.ListContains(value, "keep-alive"u8);
                connectionUpgrade |= HttpSyntax.ListContains(value, "upgrade"u8);
            }
            else if (Ascii.EqualsIgnoreCase(name, "Upgrade"u8))
            {
                hasUpgrade = true;
            }

            position = lineEnd + 2;
        }

        if (hosts > 1 || (version == HttpProtocolVersion.Http11 && hosts == 0))
            return Fail(400, "An HTTP/1.1 request needs exactly one Host field.", out error);
        if (hasTransferEncoding && contentLength is not null)
            return Fail(400, "Content-Length and Transfer-Encoding together are ambiguous.", out error);
        if (hasTransferEncoding && version == HttpProtocolVersion.Http10)
            return Fail(400, "HTTP/1.0 has no transfer codings.", out error);
        if (contentLength > options.MaxRequestBodySize)
            return Fail(413, $"The body exceeds {options.MaxRequestBodySize} bytes.", out error);

        bool keepAlive = version == HttpProtocolVersion.Http11 ? !connectionClose : connectionKeepAlive && !connectionClose;
        request.SetSemantics(contentLength, chunked, keepAlive, connectionUpgrade && hasUpgrade);
        error = null;
        return 0;
    }

    private static int Fail(int status, string message, out string? error)
    {
        error = message;
        return status;
    }

    private static ViolationCode CodeFor(int status) => status switch
    {
        413 or 414 or 431 => ViolationCode.LimitExceeded,
        501 or 505 => ViolationCode.Unsupported,
        _ => ViolationCode.Malformed,
    };

    private static HttpRequestMethod MethodOf(ReadOnlySpan<byte> method) => method.Length switch
    {
        3 when method.SequenceEqual("GET"u8) => HttpRequestMethod.Get,
        3 when method.SequenceEqual("PUT"u8) => HttpRequestMethod.Put,
        4 when method.SequenceEqual("POST"u8) => HttpRequestMethod.Post,
        4 when method.SequenceEqual("HEAD"u8) => HttpRequestMethod.Head,
        5 when method.SequenceEqual("PATCH"u8) => HttpRequestMethod.Patch,
        5 when method.SequenceEqual("TRACE"u8) => HttpRequestMethod.Trace,
        6 when method.SequenceEqual("DELETE"u8) => HttpRequestMethod.Delete,
        7 when method.SequenceEqual("CONNECT"u8) => HttpRequestMethod.Connect,
        7 when method.SequenceEqual("OPTIONS"u8) => HttpRequestMethod.Options,
        _ => HttpRequestMethod.Other,
    };

    private static void WriteStatusLine(int status, string reason, IBufferWriter<byte> output)
    {
        Span<byte> span = output.GetSpan(13 + reason.Length);
        "HTTP/1.1 "u8.CopyTo(span);
        span[9] = (byte)('0' + status / 100);
        span[10] = (byte)('0' + status / 10 % 10);
        span[11] = (byte)('0' + status % 10);
        span[12] = (byte)' ';
        int written = 13 + Encoding.Latin1.GetBytes(reason, span[13..]);
        output.Advance(written);
        output.Write("\r\n"u8);
    }

    private static void WriteField(string name, string value, IBufferWriter<byte> output)
    {
        int length = name.Length + 2 + value.Length + 2;
        Span<byte> span = output.GetSpan(length);
        int written = Encoding.ASCII.GetBytes(name, span);
        span[written++] = (byte)':';
        span[written++] = (byte)' ';
        written += Encoding.Latin1.GetBytes(value, span[written..]);
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        output.Advance(written);
    }

    private static void WriteLength(ReadOnlySpan<byte> name, long length, IBufferWriter<byte> output)
    {
        output.Write(name);
        Span<byte> digits = output.GetSpan(20);
        Utf8Formatter.TryFormat(length, digits, out int written);
        digits[written++] = (byte)'\r';
        digits[written++] = (byte)'\n';
        output.Advance(written);
    }
}
