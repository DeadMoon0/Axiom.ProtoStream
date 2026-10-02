using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    private FrozenResponseHead? _frozenHead;

    /// <summary>Creates a response with the given status code and no content.</summary>
    public HttpResponse(int statusCode)
    {
        if (statusCode is < HttpStatus.MinValue or > HttpStatus.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "An HTTP status code has three digits.");
        StatusCode = statusCode;
    }

    /// <summary>The status code.</summary>
    public int StatusCode { get; }

    /// <summary>The reason phrase; the standard phrase of the status code when not set.</summary>
    public string? ReasonPhrase
    {
        get => _reasonPhrase;
        init
        {
            if (value is not null && (!HttpSyntax.IsFieldValue(value) || value.Contains('\t', StringComparison.Ordinal)))
                throw new ProtocolStateException("A reason phrase must not contain control characters.");
            _reasonPhrase = value;
        }
    }

    /// <summary>Header fields other than the framing fields.</summary>
    public HttpResponseHeaders Headers { get; } = new();

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

    internal bool HasField(string name)
    {
        foreach (KeyValuePair<string, string> field in Headers.Fields)
        {
            if (field.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal string? FieldValue(string name)
    {
        foreach (KeyValuePair<string, string> field in Headers.Fields)
        {
            if (field.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return field.Value;
        }

        return null;
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
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        ThrowIfFrozen();
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
