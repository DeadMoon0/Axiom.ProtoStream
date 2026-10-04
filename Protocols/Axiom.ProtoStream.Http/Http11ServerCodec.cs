using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;
using Axiom.ProtoStream.Codecs;
using Axiom.ProtoStream.Errors;

namespace Axiom.ProtoStream.Http;

/// <summary>
/// Parses request heads and writes responses for one connection, following RFC 9110 and RFC 9112. Fails closed
/// on everything the RFCs call ambiguous, because ambiguity between two parsers is what request smuggling
/// exploits.
/// </summary>
internal sealed class Http11ServerCodec(Http11Options options) : ICodec<HttpRequest, HttpResponse>
{
    /// <summary>Returned by the parsing steps for a valid head.</summary>
    private const int Valid = 0;

    /// <summary>An IMF-fixdate ("Sun, 06 Nov 1994 08:49:37 GMT") always has this many characters.</summary>
    private const int ImfFixdateLength = 29;

    /// <summary>Marks that no Date value was formatted yet.</summary>
    private const long NoDateCached = -1;

    /// <summary>The standard format code for RFC 1123 / IMF-fixdate dates in <see cref="Utf8Formatter"/>.</summary>
    private const char ImfFixdateFormat = 'R';

    /// <summary>
    /// Content up to this size is written with the head; larger content goes to the connection in slices, so it is
    /// never copied whole into the session's write buffer.
    /// </summary>
    private const int MaxInlineContentBytes = 64 * 1024;

    private static ReadOnlySpan<byte> HttpScheme => "http"u8;

    private static ReadOnlySpan<byte> HttpsScheme => "https"u8;

    private static ReadOnlySpan<byte> AuthorityPrefix => "//"u8;

    private readonly HttpRequest[] _requests = [new(), new()];
    private readonly FixedLengthPayloadDecoder _fixedBody = new();
    private readonly ChunkedPayloadDecoder _chunkedBody = new();
    private readonly ExpectContinueDecoder _body = new();
    private readonly byte[] _date = new byte[ImfFixdateLength];
    private long _dateSecond = NoDateCached;
    private int _nextRequest;
    private HttpRequest? _current;
    private HttpRequestMethod _lastMethod;
    private HttpProtocolVersion _lastVersion = HttpProtocolVersion.Http11;
    private bool _lastKeepAlive;

    /// <summary>How far an incomplete head was already searched for its end, so a trickled head is scanned once.</summary>
    private long _headScanned;

    public ParseResult TryParse(ref MessageParseContext context, out HttpRequest message)
    {
        message = null!;
        ReadOnlySequence<byte> input = context.Input;
        long window = options.MaxRequestHeadBytes + (long)HttpGrammar.HeadEnd.Length;
        var reader = new SequenceReader<byte>(input.Length > window ? input.Slice(0, window) : input);

        // RFC 9112 section 2.2: empty lines before the request line are ignored. They stay in the buffer until a
        // request follows, so an endless stream of them still hits the head limit.
        while (reader.IsNext(HttpGrammar.Crlf, advancePast: true))
        {
        }

        // Without this, a head trickled in one byte at a time is searched from its start on every byte.
        if (_headScanned > reader.Consumed)
            reader.Advance(Math.Min(_headScanned, reader.Remaining + reader.Consumed) - reader.Consumed);

        if (reader.End || !reader.TryReadTo(out ReadOnlySequence<byte> _, HttpGrammar.HeadEnd, advancePastDelimiter: true))
        {
            _headScanned = Math.Max(0, Math.Min(input.Length, window) - (HttpGrammar.HeadEnd.Length - 1));
            return input.Length > window
                ? context.Invalid(ViolationCode.LimitExceeded, HttpStatus.RequestHeaderFieldsTooLarge, $"The request head exceeds {options.MaxRequestHeadBytes} bytes.")
                : context.NeedMore();
        }

        _headScanned = 0;

        DetachedHead head = context.Detach(reader.Position);
        HttpRequest request = _requests[_nextRequest ^= 1];
        int status = ParseHead(head.Memory, context.Stamp, request, out string? error);
        if (status != Valid)
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

        // Only a body that exists is worth waiting for; RFC 9110 section 10.1.1 lets the server skip 100 otherwise.
        bool bodyFollows = request.IsChunked || request.ContentLength > 0;
        _body.Reset(decoder, waiting: request.ExpectsContinue && bodyFollows);

        _current = request;
        _lastMethod = request.Method;
        _lastVersion = request.Version;
        _lastKeepAlive = request.KeepAlive;

        ParseResult result = context.DoneWithPayload(head, _body, out PipeReader body);
        request.SetBody(body);
        message = request;
        return result;
    }

    public WriteResult Write(HttpResponse response, IBufferWriter<byte> output)
    {
        int status = response.StatusCode;
        bool informational = HttpStatus.IsInformational(status);
        bool switching = status == HttpStatus.SwitchingProtocols;
        bool tunnel = _lastMethod == HttpRequestMethod.Connect && HttpStatus.IsSuccess(status);
        bool bodiless = HttpStatus.IsBodiless(status) || tunnel;
        bool hasStream = response.ContentStream is not null;

        Validate(response, informational, switching, bodiless, hasStream);

        if (informational && status == HttpStatus.Continue)
            _body.Satisfied(); // the application answered the expectation itself

        // A client still waiting for 100 Continue will not send its body; the connection cannot be reused.
        bool close = !informational && !tunnel && (response.CloseConnection || !_lastKeepAlive || _body.IsWaiting);
        bool chunked = false;

        if (response.FrozenHead is { } frozen)
            output.Write(frozen.Bytes);
        else
            WriteHead(response, output);

        // RFC 9110 section 6.6.1: an origin server with a clock MUST send Date in 2xx, 3xx and 4xx responses.
        if (!informational && !response.HasDate)
            WriteDate(output);

        if (!bodiless)
        {
            if (!hasStream)
                WriteContentLength(response.Content.Length, output);
            else if (response.ContentLength is { } length)
                WriteContentLength(length, output);
            else if (_lastVersion == HttpProtocolVersion.Http11)
            {
                output.Write(HttpGrammar.TransferEncodingChunkedField);
                chunked = true;
            }
            else
                close = true; // HTTP/1.0 cannot frame an unknown length other than by closing
        }

        if (switching)
            output.Write(HttpGrammar.ConnectionUpgradeField);
        else if (close)
            output.Write(HttpGrammar.ConnectionCloseField);
        else if (!informational && !tunnel && _lastVersion == HttpProtocolVersion.Http10)
            output.Write(HttpGrammar.ConnectionKeepAliveField);
        output.Write(HttpGrammar.Crlf);

        WriteResult result = WriteResult.Done;
        if (!bodiless && _lastMethod != HttpRequestMethod.Head)
        {
            if (!hasStream && response.Content.Length <= MaxInlineContentBytes)
                output.Write(response.Content.Span);
            else if (!hasStream)
                result = WriteResult.WithPayload(new MemoryPayloadSource(response.Content), response.Content.Length, IdentityPayloadEncoder.Instance);
            else
                result = WriteResult.WithPayload(response.ContentStream!, response.ContentLength, chunked ? ChunkedPayloadEncoder.Instance : IdentityPayloadEncoder.Instance);
        }

        if (switching || tunnel)
            return result.ThenHandOver();
        return close ? result.ThenClose() : result;
    }

    // Requirements on the response the application builds; a violation is a programming error, so nothing is sent.
    private void Validate(HttpResponse response, bool informational, bool switching, bool bodiless, bool hasStream)
    {
        int status = response.StatusCode;
        if (hasStream && !response.Content.IsEmpty)
            throw new ProtocolStateException("A response has either Content or a ContentStream, not both.");
        if (bodiless && (hasStream || !response.Content.IsEmpty))
            throw new ProtocolStateException($"A {status} response cannot have content.");

        // RFC 9110 section 15.2: a server MUST NOT send a 1xx response to an HTTP/1.0 client.
        if (informational && _lastVersion == HttpProtocolVersion.Http10)
            throw new ProtocolStateException($"A {status} response cannot be sent to an HTTP/1.0 client.");

        if (RequiredField(status) is { } required && !response.HasField(required))
            throw new ProtocolStateException($"A {status} response must carry a {required} field (RFC 9110).");

        // RFC 9110 section 7.8: a server MUST NOT switch to a protocol the client did not offer.
        if (switching)
        {
            if (_current is null || !_current.IsUpgradeRequest)
                throw new ProtocolStateException("101 Switching Protocols answers only a request that asked to upgrade.");
            foreach (string protocol in response.FieldValue("Upgrade")!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Offered(_current, protocol))
                    throw new ProtocolStateException($"The client did not offer '{protocol}' in its Upgrade field.");
            }
        }
    }

    /// <summary>Fields RFC 9110 requires in responses with the given status.</summary>
    private static string? RequiredField(int status) => status switch
    {
        HttpStatus.SwitchingProtocols or HttpStatus.UpgradeRequired => "Upgrade", // sections 7.8 and 15.5.22
        HttpStatus.Unauthorized => "WWW-Authenticate", // section 15.5.2
        HttpStatus.MethodNotAllowed => "Allow", // section 15.5.6
        HttpStatus.ProxyAuthenticationRequired => "Proxy-Authenticate", // section 15.5.8
        _ => null,
    };

    private static bool Offered(HttpRequest request, string protocol)
    {
        HttpRequestHeaders headers = request.Headers;
        for (int i = 0; i < headers.Count; i++)
        {
            HttpHeader field = headers[i];
            if (!Ascii.EqualsIgnoreCase(field.NameBytes.Span, HttpGrammar.Upgrade))
                continue;
            foreach (string offered in field.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (offered.Equals(protocol, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <returns><see cref="Valid"/> when the head is valid, otherwise the status code to answer with.</returns>
    private int ParseHead(ReadOnlyMemory<byte> memory, MessageStamp stamp, HttpRequest request, out string? error)
    {
        ReadOnlySpan<byte> head = memory.Span;
        int position = 0;

        // RFC 9112 section 2.2: ignore empty lines received before the request line.
        while (head[position..].StartsWith(HttpGrammar.Crlf))
            position += HttpGrammar.Crlf.Length;

        int lineLength = head[position..].IndexOf(HttpGrammar.Crlf);
        if (lineLength <= 0)
            return Fail(HttpStatus.BadRequest, "The request line is missing.", out error);
        int lineEnd = position + lineLength;
        ReadOnlySpan<byte> line = head[position..lineEnd];
        if (line.Length > options.MaxRequestLineBytes)
            return Fail(HttpStatus.UriTooLong, "The request line is too long.", out error);

        // request-line = method SP request-target SP HTTP-version (RFC 9112 section 3)
        int firstSpace = line.IndexOf(HttpGrammar.Space);
        if (firstSpace <= 0 || !HttpSyntax.IsToken(line[..firstSpace]))
            return Fail(HttpStatus.BadRequest, "The request method is malformed.", out error);
        ReadOnlySpan<byte> afterMethod = line[(firstSpace + 1)..];
        int secondSpace = afterMethod.IndexOf(HttpGrammar.Space);
        if (secondSpace <= 0)
            return Fail(HttpStatus.BadRequest, "The request line is malformed.", out error);

        ReadOnlySpan<byte> versionText = afterMethod[(secondSpace + 1)..];
        HttpProtocolVersion version;
        if (versionText.SequenceEqual(HttpGrammar.Http11))
            version = HttpProtocolVersion.Http11;
        else if (versionText.SequenceEqual(HttpGrammar.Http10))
            version = HttpProtocolVersion.Http10;
        else if (HttpGrammar.IsHttpVersion(versionText))
            return Fail(HttpStatus.HttpVersionNotSupported, "The HTTP version is not supported.", out error);
        else
            return Fail(HttpStatus.BadRequest, "The HTTP version is malformed.", out error);

        HttpRequestMethod method = MethodOf(line[..firstSpace]);
        int targetStart = position + firstSpace + 1;
        request.Load(memory, stamp, position, firstSpace, targetStart, secondSpace, method, version);

        int targetStatus = ParseTarget(head, targetStart, secondSpace, method, request, out error);
        if (targetStatus != Valid)
            return targetStatus;

        position = lineEnd + HttpGrammar.Crlf.Length;
        return ParseFields(head, position, version, request, out error);
    }

    /// <summary>The request-target forms of RFC 9112 section 3.2, checked against the URI grammar of RFC 3986.</summary>
    private static int ParseTarget(ReadOnlySpan<byte> head, int start, int length, HttpRequestMethod method, HttpRequest request, out string? error)
    {
        ReadOnlySpan<byte> target = head.Slice(start, length);
        if (!HttpSyntax.IsVisibleAscii(target))
            return Fail(HttpStatus.BadRequest, "The request target contains invalid characters.", out error);

        // authority-form is used only, and always, with CONNECT (RFC 9112 section 3.2.3, RFC 9110 section 9.3.6).
        if (method == HttpRequestMethod.Connect)
        {
            if (!UriSyntax.TrySplitHostPort(target, out ReadOnlySpan<byte> host, out ReadOnlySpan<byte> port, out bool hasPort)
                || !hasPort || port.IsEmpty || !UriSyntax.IsPort(port) || host.IsEmpty || !UriSyntax.IsHost(host))
                return Fail(HttpStatus.BadRequest, "A CONNECT target must be host:port.", out error);
            request.SetTarget(RequestTargetForm.Authority, start, 0, HttpRequest.NoPart, 0, start, length);
            error = null;
            return Valid;
        }

        // asterisk-form is used only with a server-wide OPTIONS (RFC 9112 section 3.2.4).
        if (target.Length == 1 && target[0] == HttpGrammar.Asterisk)
        {
            if (method != HttpRequestMethod.Options)
                return Fail(HttpStatus.BadRequest, "The asterisk target is only valid for OPTIONS.", out error);
            request.SetTarget(RequestTargetForm.Asterisk, start, 0, HttpRequest.NoPart, 0, HttpRequest.NoPart, 0);
            error = null;
            return Valid;
        }

        // origin-form = absolute-path [ "?" query ] (RFC 9112 section 3.2.1)
        if (target[0] == HttpGrammar.Slash)
            return SetPathAndQuery(target, start, RequestTargetForm.Origin, HttpRequest.NoPart, 0, request, out error);

        // absolute-form = absolute-URI = scheme ":" hier-part [ "?" query ] (RFC 9112 section 3.2.2, RFC 3986 4.3)
        int colon = target.IndexOf(HttpGrammar.Colon);
        if (colon <= 0 || !UriSyntax.IsScheme(target[..colon]))
            return Fail(HttpStatus.BadRequest, "The request target is not a valid URI.", out error);

        ReadOnlySpan<byte> scheme = target[..colon];
        bool isHttp = Ascii.EqualsIgnoreCase(scheme, HttpScheme) || Ascii.EqualsIgnoreCase(scheme, HttpsScheme);
        int afterScheme = colon + 1;
        ReadOnlySpan<byte> hierarchical = target[afterScheme..];
        if (!hierarchical.StartsWith(AuthorityPrefix))
        {
            // An http(s) URI needs an authority with a host (RFC 9110 section 4.2.1).
            if (isHttp)
                return Fail(HttpStatus.BadRequest, "An http URI needs an authority.", out error);
            return SetPathAndQuery(hierarchical, start + afterScheme, RequestTargetForm.Absolute, HttpRequest.NoPart, 0, request, out error, rootless: true);
        }

        int authorityStart = afterScheme + AuthorityPrefix.Length;
        ReadOnlySpan<byte> afterPrefix = target[authorityStart..];
        int authorityEnd = afterPrefix.IndexOfAny(HttpGrammar.Slash, HttpGrammar.QuestionMark);
        ReadOnlySpan<byte> authority = authorityEnd < 0 ? afterPrefix : afterPrefix[..authorityEnd];
        int at = authority.IndexOf(UriSyntax.At);
        if (at >= 0)
        {
            // userinfo is deprecated for http(s) and its presence an error (RFC 9110 section 4.2.4).
            if (isHttp || !UriSyntax.IsUserInfo(authority[..at]))
                return Fail(HttpStatus.BadRequest, "The URI carries user information.", out error);
            authority = authority[(at + 1)..];
            authorityStart += at + 1;
        }

        if (!UriSyntax.IsHostAndOptionalPort(authority)
            || (isHttp && UriSyntax.TrySplitHostPort(authority, out ReadOnlySpan<byte> uriHost, out _, out _) && uriHost.IsEmpty))
            return Fail(HttpStatus.BadRequest, "The URI authority is malformed.", out error);

        ReadOnlySpan<byte> rest = authorityEnd < 0 ? default : afterPrefix[authorityEnd..];
        int restStart = start + authorityStart + authority.Length;
        return SetPathAndQuery(rest, restStart, RequestTargetForm.Absolute, start + authorityStart, authority.Length, request, out error);
    }

    private static int SetPathAndQuery(ReadOnlySpan<byte> pathAndQuery, int start, RequestTargetForm form, int authorityStart, int authorityLength, HttpRequest request, out string? error, bool rootless = false)
    {
        int question = pathAndQuery.IndexOf(HttpGrammar.QuestionMark);
        ReadOnlySpan<byte> path = question < 0 ? pathAndQuery : pathAndQuery[..question];
        ReadOnlySpan<byte> query = question < 0 ? default : pathAndQuery[(question + 1)..];

        bool pathValid = rootless ? UriSyntax.IsPathWithoutAuthority(path) : form == RequestTargetForm.Origin ? UriSyntax.IsAbsolutePath(path) : UriSyntax.IsPathAbEmpty(path);
        if (!pathValid)
            return Fail(HttpStatus.BadRequest, "The request path is malformed.", out error);
        if (question >= 0 && !UriSyntax.IsQuery(query))
            return Fail(HttpStatus.BadRequest, "The request query is malformed.", out error);

        int queryStart = question < 0 ? HttpRequest.NoPart : start + question + 1;
        request.SetTarget(form, start, path.Length, queryStart, query.Length, authorityStart, authorityLength);
        error = null;
        return Valid;
    }

    private struct TransferCodings
    {
        public int Count;
        public int Chunked;
        public bool LastIsChunked;
        public bool Malformed;
    }

    private int ParseFields(ReadOnlySpan<byte> head, int position, HttpProtocolVersion version, HttpRequest request, out string? error)
    {
        long? contentLength = null;
        var codings = new TransferCodings();
        bool hasTransferEncoding = false;
        int hostField = HttpRequest.NoPart;
        int hosts = 0;
        bool connectionClose = false;
        bool connectionKeepAlive = false;
        bool connectionUpgrade = false;
        bool hasUpgrade = false;
        bool expectsContinue = false;
        bool unknownExpectation = false;

        while (true)
        {
            int lineEnd = position + head[position..].IndexOf(HttpGrammar.Crlf);
            if (lineEnd == position)
                break; // the empty line ending the head

            ReadOnlySpan<byte> line = head[position..lineEnd];

            // RFC 9112 section 5.2: obs-fold in a request is rejected.
            if (HttpSyntax.IsWhitespace(line[0]))
                return Fail(HttpStatus.BadRequest, "Obsolete line folding is not accepted.", out error);

            // RFC 9112 section 5.1: no whitespace between the field name and the colon.
            int colon = line.IndexOf(HttpGrammar.Colon);
            if (colon <= 0 || !HttpSyntax.IsToken(line[..colon]))
                return Fail(HttpStatus.BadRequest, "A header field name is malformed.", out error);

            ReadOnlySpan<byte> name = line[..colon];
            ReadOnlySpan<byte> rawValue = line[(colon + 1)..];
            ReadOnlySpan<byte> value = HttpSyntax.TrimWhitespace(rawValue);

            // RFC 9110 section 5.5: CR, LF and NUL (and every other control) make a field value invalid.
            if (!HttpSyntax.IsFieldValue(value))
                return Fail(HttpStatus.BadRequest, "A header field value contains control characters.", out error);
            if (request.HeaderCount >= options.MaxHeaderCount)
                return Fail(HttpStatus.RequestHeaderFieldsTooLarge, $"More than {options.MaxHeaderCount} header fields.", out error);

            int valueStart = position + colon + 1 + HttpSyntax.LeadingWhitespace(rawValue);
            int fieldIndex = request.HeaderCount;
            request.AddHeader(new HeaderEntry(position, colon, valueStart, value.Length));

            if (Ascii.EqualsIgnoreCase(name, HttpGrammar.ContentLength))
            {
                // RFC 9112 section 6.3: an invalid Content-Length is an unrecoverable framing error.
                if (!HttpSyntax.TryParseDecimal(value, out long length))
                    return Fail(HttpStatus.BadRequest, "Content-Length is malformed.", out error);
                if (contentLength is { } previous && previous != length)
                    return Fail(HttpStatus.BadRequest, "Content-Length is declared twice with different values.", out error);
                contentLength = length;
            }
            else if (Ascii.EqualsIgnoreCase(name, HttpGrammar.TransferEncoding))
            {
                hasTransferEncoding = true;
                HttpSyntax.ForEachListElement(value, ref codings, static (ReadOnlySpan<byte> element, ref TransferCodings state) =>
                {
                    // transfer-coding = token *( OWS ";" OWS transfer-parameter )
                    int semicolon = element.IndexOf(HttpGrammar.Semicolon);
                    ReadOnlySpan<byte> coding = HttpSyntax.TrimWhitespace(semicolon < 0 ? element : element[..semicolon]);
                    if (!HttpSyntax.IsToken(coding))
                    {
                        state.Malformed = true;
                        return false;
                    }

                    state.Count++;
                    state.LastIsChunked = Ascii.EqualsIgnoreCase(coding, HttpGrammar.Chunked);
                    if (state.LastIsChunked)
                    {
                        // chunked defines no parameters; "chunked;q=1" is something another parser may not
                        // recognise as chunked at all (a TE.0 desync), so it is refused rather than read as chunked.
                        if (semicolon >= 0)
                        {
                            state.Malformed = true;
                            return false;
                        }

                        state.Chunked++;
                    }

                    return true;
                });
            }
            else if (Ascii.EqualsIgnoreCase(name, HttpGrammar.Host))
            {
                // RFC 9110 section 7.2 / RFC 9112 section 3.2: Host = uri-host [ ":" port ], once.
                if (!UriSyntax.IsHostAndOptionalPort(value))
                    return Fail(HttpStatus.BadRequest, "The Host field is malformed.", out error);

                // A comma is legal in a reg-name, but it is also how intermediaries join repeated fields: one
                // Host "a,b" and two Hosts "a" and "b" would look the same downstream.
                if (value.Contains(HttpGrammar.Comma))
                    return Fail(HttpStatus.BadRequest, "The Host field contains a comma.", out error);
                hosts++;
                hostField = fieldIndex;
            }
            else if (Ascii.EqualsIgnoreCase(name, HttpGrammar.Connection))
            {
                connectionClose |= HttpSyntax.ListContains(value, HttpGrammar.Close);
                connectionKeepAlive |= HttpSyntax.ListContains(value, HttpGrammar.KeepAlive);
                connectionUpgrade |= HttpSyntax.ListContains(value, HttpGrammar.Upgrade);
            }
            else if (Ascii.EqualsIgnoreCase(name, HttpGrammar.Upgrade))
            {
                hasUpgrade = true;
            }
            else if (Ascii.EqualsIgnoreCase(name, HttpGrammar.Expect))
            {
                bool continueOnly = HttpSyntax.ForEachListElement(value, ref expectsContinue, static (ReadOnlySpan<byte> element, ref bool continues) =>
                {
                    continues |= Ascii.EqualsIgnoreCase(element, HttpGrammar.ContinueExpectation);
                    return Ascii.EqualsIgnoreCase(element, HttpGrammar.ContinueExpectation);
                });
                unknownExpectation |= !continueOnly;
            }

            position = lineEnd + HttpGrammar.Crlf.Length;
        }

        // RFC 9112 section 3.2: exactly one Host in HTTP/1.1, at most one in HTTP/1.0.
        if (hosts > 1 || (version == HttpProtocolVersion.Http11 && hosts == 0))
            return Fail(HttpStatus.BadRequest, "An HTTP/1.1 request needs exactly one Host field.", out error);

        if (hasTransferEncoding)
        {
            // RFC 9112 section 6.1: HTTP/1.0 has no transfer codings; the framing is faulty.
            if (version == HttpProtocolVersion.Http10)
                return Fail(HttpStatus.BadRequest, "HTTP/1.0 has no transfer codings.", out error);
            // RFC 9112 section 6.3: both length fields together are ambiguous; this server rejects them.
            if (contentLength is not null)
                return Fail(HttpStatus.BadRequest, "Content-Length and Transfer-Encoding together are ambiguous.", out error);
            // RFC 9112 section 6.3: chunked not last means the length cannot be determined: 400 and close.
            if (codings.Malformed || codings.Count == 0 || !codings.LastIsChunked || codings.Chunked > 1)
                return Fail(HttpStatus.BadRequest, "Transfer-Encoding must end with chunked, applied once.", out error);
            // RFC 9112 section 6.1: a transfer coding the server does not understand gets 501.
            if (codings.Count > 1)
                return Fail(HttpStatus.NotImplemented, "Only the chunked transfer coding is supported.", out error);
        }

        if (contentLength > options.MaxRequestBodySize)
            return Fail(HttpStatus.ContentTooLarge, $"The body exceeds {options.MaxRequestBodySize} bytes.", out error);

        // RFC 9110 section 10.1.1: expectations are ignored in HTTP/1.0; an unknown one may get 417.
        if (version == HttpProtocolVersion.Http10)
            expectsContinue = false;
        else if (unknownExpectation)
            return Fail(HttpStatus.ExpectationFailed, "The request carries an expectation other than 100-continue.", out error);

        bool keepAlive = version == HttpProtocolVersion.Http11 ? !connectionClose : connectionKeepAlive && !connectionClose;
        request.SetSemantics(contentLength, hasTransferEncoding, keepAlive, connectionUpgrade && hasUpgrade, expectsContinue, hostField);
        error = null;
        return Valid;
    }

    private static int Fail(int status, string message, out string? error)
    {
        error = message;
        return status;
    }

    private static ViolationCode CodeFor(int status) => status switch
    {
        HttpStatus.ContentTooLarge or HttpStatus.UriTooLong or HttpStatus.RequestHeaderFieldsTooLarge => ViolationCode.LimitExceeded,
        HttpStatus.NotImplemented or HttpStatus.HttpVersionNotSupported or HttpStatus.ExpectationFailed => ViolationCode.Unsupported,
        _ => ViolationCode.Malformed,
    };

    private static HttpRequestMethod MethodOf(ReadOnlySpan<byte> method)
    {
        // Methods are case-sensitive (RFC 9110 section 9.1): "get" is not GET.
        if (method.SequenceEqual("GET"u8))
            return HttpRequestMethod.Get;
        if (method.SequenceEqual("POST"u8))
            return HttpRequestMethod.Post;
        if (method.SequenceEqual("HEAD"u8))
            return HttpRequestMethod.Head;
        if (method.SequenceEqual("PUT"u8))
            return HttpRequestMethod.Put;
        if (method.SequenceEqual("DELETE"u8))
            return HttpRequestMethod.Delete;
        if (method.SequenceEqual("PATCH"u8))
            return HttpRequestMethod.Patch;
        if (method.SequenceEqual("OPTIONS"u8))
            return HttpRequestMethod.Options;
        if (method.SequenceEqual("CONNECT"u8))
            return HttpRequestMethod.Connect;
        if (method.SequenceEqual("TRACE"u8))
            return HttpRequestMethod.Trace;
        return HttpRequestMethod.Other;
    }

    private void WriteDate(IBufferWriter<byte> output)
    {
        DateTimeOffset now = options.TimeProvider.GetUtcNow();
        long second = now.ToUnixTimeSeconds();
        if (second != _dateSecond)
        {
            Utf8Formatter.TryFormat(now, _date, out _, ImfFixdateFormat);
            _dateSecond = second;
        }

        output.Write(HttpGrammar.Date);
        output.Write(HttpGrammar.FieldSeparator);
        output.Write(_date);
        output.Write(HttpGrammar.Crlf);
    }

    /// <summary>The status line and the application's fields: everything of the head that does not depend on the request.</summary>
    internal static void WriteHead(HttpResponse response, IBufferWriter<byte> output)
    {
        if (response.Head is { } head)
            output.Write(head.Encoded.Bytes);
        else
            WriteStatusLine(response.StatusCode, response.Reason, output);
        foreach (KeyValuePair<string, string> field in response.AddedFields)
            WriteField(field.Key, field.Value, output);
    }

    internal static void WriteStatusLine(int status, string reason, IBufferWriter<byte> output)
    {
        // status-line = HTTP-version SP status-code SP [ reason-phrase ] (RFC 9112 section 4)
        output.Write(HttpGrammar.Http11);
        output.Write([HttpGrammar.Space]);
        WriteDecimal(status, output);
        output.Write([HttpGrammar.Space]);
        WriteLatin1(reason, output);
        output.Write(HttpGrammar.Crlf);
    }

    internal static void WriteField(string name, string value, IBufferWriter<byte> output)
    {
        WriteLatin1(name, output);
        output.Write(HttpGrammar.FieldSeparator);
        WriteLatin1(value, output);
        output.Write(HttpGrammar.Crlf);
    }

    private static void WriteContentLength(long length, IBufferWriter<byte> output)
    {
        output.Write(HttpGrammar.ContentLength);
        output.Write(HttpGrammar.FieldSeparator);
        WriteDecimal(length, output);
        output.Write(HttpGrammar.Crlf);
    }

    private static void WriteDecimal(long value, IBufferWriter<byte> output)
    {
        Span<byte> digits = output.GetSpan(HttpGrammar.MaxDecimalDigits);
        Utf8Formatter.TryFormat(value, digits, out int written);
        output.Advance(written);
    }

    private static void WriteLatin1(string text, IBufferWriter<byte> output)
    {
        int written = Encoding.Latin1.GetBytes(text, output.GetSpan(text.Length));
        output.Advance(written);
    }
}

/// <summary>
/// Wraps the body decoder of a request that expects 100-continue: the first read of the body sends
/// <c>100 Continue</c> (RFC 9110 section 10.1.1), and a final response sent while the client still waits closes
/// the connection.
/// </summary>
internal sealed class ExpectContinueDecoder : IPayloadDecoder, IPayloadPreamble
{
    private static readonly byte[] ContinueResponse = HttpGrammar.ContinueResponse.ToArray();

    private IPayloadDecoder _inner = null!;

    /// <summary>True while the client waits for 100 Continue.</summary>
    public bool IsWaiting { get; private set; }

    public void Reset(IPayloadDecoder inner, bool waiting)
    {
        _inner = inner;
        IsWaiting = waiting;
    }

    /// <summary>The application sent the interim 100 response itself.</summary>
    public void Satisfied() => IsWaiting = false;

    public PayloadStep Next(in ReadOnlySequence<byte> input, bool isCompleted) => _inner.Next(input, isCompleted);

    public void OnConsumed(long dataBytes) => _inner.OnConsumed(dataBytes);

    public bool TryTakePreamble(out ReadOnlyMemory<byte> preamble)
    {
        preamble = ContinueResponse;
        if (!IsWaiting)
            return false;
        IsWaiting = false;
        return true;
    }
}
