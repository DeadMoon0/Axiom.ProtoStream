using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Axiom.ProtoStream.Errors;

namespace Axiom.ProtoStream.Http;

/// <summary>
/// A response to write. Framing fields (<c>Content-Length</c>, <c>Transfer-Encoding</c>, <c>Connection</c>) are
/// the framework's: it derives them from the content and the request, so they can never contradict the body.
/// </summary>
public sealed class HttpResponse
{
    private readonly string? _reasonPhrase;
    private readonly HttpResponseHead? _head;
    private HttpResponseHeaders? _headers;
    private FrozenResponseHead? _frozenHead;

    /// <summary>Creates a response with the given status code and no content.</summary>
    public HttpResponse(int statusCode)
    {
        HttpResponseHead.ThrowIfInvalidStatus(statusCode);
        StatusCode = statusCode;
    }

    /// <summary>
    /// Creates a response whose status line and fields come from <paramref name="head"/>, validated and encoded
    /// once. Nothing of the head is checked or encoded again; fields added to <see cref="Headers"/> follow it.
    /// </summary>
    public HttpResponse(HttpResponseHead head)
    {
        ArgumentNullException.ThrowIfNull(head);
        _head = head;
        StatusCode = head.StatusCode;
    }

    /// <summary>The status code.</summary>
    public int StatusCode { get; }

    /// <summary>The reason phrase; the standard phrase of the status code when not set.</summary>
    /// <exception cref="ProtocolStateException">Set on a response created from a head, which carries its own.</exception>
    public string? ReasonPhrase
    {
        get => _head is null ? _reasonPhrase : _head.ReasonPhrase;
        init
        {
            if (_head is not null)
                throw new ProtocolStateException("The reason phrase of a response created from a head is the head's.") { Guidance = "Pass it to HttpResponseHead.Create." };
            HttpResponseHead.ThrowIfInvalidReason(value);
            _reasonPhrase = value;
        }
    }

    /// <summary>The head this response was created from, if any.</summary>
    public HttpResponseHead? Head => _head;

    /// <summary>Header fields other than the framing fields; for a response created from a head, the fields after it.</summary>
    public HttpResponseHeaders Headers => _headers ??= new();

    /// <summary>The body as bytes. Leave empty when <see cref="ContentStream"/> is used.</summary>
    public ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>A body streamed from this source, chunked when <see cref="ContentLength"/> is not known.</summary>
    public Stream? ContentStream { get; init; }

    /// <summary>The length of <see cref="ContentStream"/>, when known.</summary>
    public long? ContentLength { get; init; }

    /// <summary>Close the connection after this response.</summary>
    public bool CloseConnection { get; init; }

    /// <summary>A UTF-8 text response.</summary>
    public static HttpResponse Text(int statusCode, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var response = new HttpResponse(statusCode) { Content = Encoding.UTF8.GetBytes(text) };
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        return response;
    }

    /// <summary>A response with the given bytes and content type.</summary>
    public static HttpResponse Bytes(int statusCode, ReadOnlyMemory<byte> content, string contentType)
    {
        var response = new HttpResponse(statusCode) { Content = content };
        response.Headers.Add("Content-Type", contentType);
        return response;
    }

    /// <summary>A response streamed from <paramref name="content"/>; chunked when <paramref name="length"/> is null.</summary>
    public static HttpResponse FromStream(int statusCode, Stream content, long? length, string contentType)
    {
        ArgumentNullException.ThrowIfNull(content);
        var response = new HttpResponse(statusCode) { ContentStream = content, ContentLength = length };
        response.Headers.Add("Content-Type", contentType);
        return response;
    }

    /// <summary>True once <see cref="Freeze"/> made the response immutable.</summary>
    public bool IsFrozen => Volatile.Read(ref _frozenHead) is not null;

    /// <summary>
    /// Makes the response immutable and encodes its status line and fields once. A frozen response can be sent
    /// any number of times, from any session and concurrently, without allocating or encoding its head again.
    /// </summary>
    /// <remarks>
    /// <para>Freeze it before it is shared; afterwards <see cref="Headers"/> refuses changes. The memory behind
    /// <see cref="Content"/> must not change either: it is sent as it is, to every peer.</para>
    /// <para>The framing fields and <c>Date</c> are still written per request, so a frozen response stays correct
    /// for HEAD, HTTP/1.0 and closing connections.</para>
    /// </remarks>
    /// <exception cref="ProtocolStateException">The response streams its content: a stream can be sent only once.</exception>
    public HttpResponse Freeze()
    {
        if (IsFrozen)
            return this;
        if (ContentStream is not null)
            throw new ProtocolStateException("A response with a ContentStream cannot be frozen: its stream can be sent only once.");

        Headers.Freeze();
        var head = new ArrayBufferWriter<byte>();
        Http11ServerCodec.WriteHead(this, head);
        Interlocked.CompareExchange(ref _frozenHead, new FrozenResponseHead(head.WrittenSpan.ToArray(), HasField("Date")), null);
        return this;
    }

    /// <summary>A response with only a status code.</summary>
    public static HttpResponse Status(int statusCode) => new(statusCode);

    /// <summary>An empty error response that closes the connection.</summary>
    public static HttpResponse Error(int statusCode) => new(statusCode) { CloseConnection = true };

    /// <summary>
    /// <c>101 Switching Protocols</c> to <paramref name="protocol"/>, which the request must have offered in its
    /// Upgrade field. It hands the connection over, so it is sent with <c>Session.SwitchAsync</c>.
    /// </summary>
    public static HttpResponse SwitchingProtocols(string protocol)
    {
        var response = new HttpResponse(HttpStatus.SwitchingProtocols);
        response.Headers.Add("Upgrade", protocol);
        return response;
    }

    /// <summary>
    /// <c>200</c> to a CONNECT request: the connection becomes a tunnel, so it is sent with
    /// <c>Session.SwitchAsync</c>, typically to <c>Raw.Definition</c>.
    /// </summary>
    public static HttpResponse ConnectionEstablished() => new(HttpStatus.Ok);

    internal string Reason => _reasonPhrase ?? HttpStatus.ReasonPhrase(StatusCode);

    internal FrozenResponseHead? FrozenHead => Volatile.Read(ref _frozenHead);

    /// <summary>The fields added to <see cref="Headers"/>, without creating the collection.</summary>
    internal ReadOnlySpan<KeyValuePair<string, string>> AddedFields => _headers is null ? default : CollectionsMarshal.AsSpan(_headers.Fields);

    /// <summary>Whether the application set Date itself; answered from the encoded heads without a lookup when it can be.</summary>
    internal bool HasDate =>
        FrozenHead?.HasDate ?? ((_head?.Encoded.HasDate ?? false) || HttpResponseHeaders.Find(AddedFields, "Date") is not null);

    internal bool HasField(string name) => FieldValue(name) is not null;

    internal string? FieldValue(string name)
    {
        if (_head?.FieldValue(name) is { } inHead)
            return inHead;
        return HttpResponseHeaders.Find(AddedFields, name);
    }
}

/// <summary>
/// A status line and header fields, validated and encoded once. Responses created from it with
/// <see cref="HttpResponse(HttpResponseHead)"/> share the encoded bytes: per request, only the content, the
/// framing fields and <c>Date</c> are written. Immutable and safe to share across sessions and threads.
/// </summary>
public sealed class HttpResponseHead
{
    private readonly KeyValuePair<string, string>[] _fields;

    private HttpResponseHead(int statusCode, string? reasonPhrase, KeyValuePair<string, string>[] fields)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        _fields = fields;
        var bytes = new ArrayBufferWriter<byte>();
        Http11ServerCodec.WriteStatusLine(statusCode, reasonPhrase ?? HttpStatus.ReasonPhrase(statusCode), bytes);
        foreach (KeyValuePair<string, string> field in fields)
            Http11ServerCodec.WriteField(field.Key, field.Value, bytes);
        Encoded = new FrozenResponseHead(bytes.WrittenSpan.ToArray(), FieldValue("Date") is not null);
    }

    /// <summary>The status code.</summary>
    public int StatusCode { get; }

    /// <summary>The reason phrase; the standard phrase of the status code when null.</summary>
    public string? ReasonPhrase { get; }

    /// <summary>The fields, in the order they are sent.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields => _fields;

    /// <summary>Validates and encodes a head. The fields follow the rules of <see cref="HttpResponseHeaders.Add"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The status code does not have three digits.</exception>
    /// <exception cref="ProtocolStateException">A field or the reason phrase is malformed, or a field is a framing field.</exception>
    public static HttpResponseHead Create(int statusCode, IEnumerable<KeyValuePair<string, string>> fields) => Create(statusCode, fields, null);

    /// <summary>Validates and encodes a head with its own reason phrase; null means the status code's standard phrase.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The status code does not have three digits.</exception>
    /// <exception cref="ProtocolStateException">A field or the reason phrase is malformed, or a field is a framing field.</exception>
    public static HttpResponseHead Create(int statusCode, IEnumerable<KeyValuePair<string, string>> fields, string? reasonPhrase)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ThrowIfInvalidStatus(statusCode);
        ThrowIfInvalidReason(reasonPhrase);
        KeyValuePair<string, string>[] copy = [.. fields];
        foreach (KeyValuePair<string, string> field in copy)
            HttpResponseHeaders.ThrowIfInvalid(field.Key, field.Value);
        return new HttpResponseHead(statusCode, reasonPhrase, copy);
    }

    internal FrozenResponseHead Encoded { get; }

    internal string? FieldValue(string name) => HttpResponseHeaders.Find(_fields, name);

    internal static void ThrowIfInvalidStatus(int statusCode)
    {
        if (statusCode is < HttpStatus.MinValue or > HttpStatus.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "An HTTP status code has three digits.");
    }

    internal static void ThrowIfInvalidReason(string? reasonPhrase)
    {
        if (reasonPhrase is not null && (!HttpSyntax.IsFieldValue(reasonPhrase) || reasonPhrase.Contains('\t', StringComparison.Ordinal)))
            throw new ProtocolStateException("A reason phrase must not contain control characters.");
    }
}

/// <summary>The header fields of a response, validated when added so nothing malformed reaches the wire.</summary>
public sealed class HttpResponseHeaders : IEnumerable<KeyValuePair<string, string>>
{
    private readonly List<KeyValuePair<string, string>> _fields = [];
    private bool _frozen;

    /// <summary>Number of fields.</summary>
    public int Count => _fields.Count;

    /// <summary>
    /// Adds a field. Throws <see cref="ProtocolStateException"/> for a name that is not a token, a value with
    /// control characters (a CR or LF would inject fields), or a framing field the framework manages.
    /// </summary>
    public HttpResponseHeaders Add(string name, string value)
    {
        ThrowIfFrozen();
        ThrowIfInvalid(name, value);
        _fields.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    /// <summary>Removes every field named <paramref name="name"/>.</summary>
    public bool Remove(string name)
    {
        ThrowIfFrozen();
        return _fields.RemoveAll(f => f.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal List<KeyValuePair<string, string>> Fields => _fields;

    internal void Freeze() => Volatile.Write(ref _frozen, true);

    internal static string? Find(ReadOnlySpan<KeyValuePair<string, string>> fields, string name)
    {
        foreach (KeyValuePair<string, string> field in fields)
        {
            if (field.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return field.Value;
        }

        return null;
    }

    internal static void ThrowIfInvalid(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!HttpSyntax.IsToken(name))
            throw new ProtocolStateException($"'{name}' is not a valid header name.");
        if (!HttpSyntax.IsFieldValue(value))
            throw new ProtocolStateException($"The value of '{name}' contains control characters.");
        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            throw new ProtocolStateException($"'{name}' is derived from the content and the request; it cannot be set.")
            {
                Guidance = "Set Content, ContentStream and ContentLength, or CloseConnection, instead.",
            };
    }

    private void ThrowIfFrozen()
    {
        if (Volatile.Read(ref _frozen))
            throw new ProtocolStateException("The response is frozen; its fields can no longer change.") { Guidance = "Build a new response, or change it before calling Freeze." };
    }
}

/// <summary>The encoded status line and application fields of a frozen response.</summary>
internal sealed class FrozenResponseHead(byte[] bytes, bool hasDate)
{
    public byte[] Bytes { get; } = bytes;

    public bool HasDate { get; } = hasDate;
}
