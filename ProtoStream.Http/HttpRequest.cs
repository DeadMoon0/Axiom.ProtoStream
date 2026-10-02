using System;
using System.IO.Pipelines;
using System.Text;
using ProtoStream.Codecs;

namespace ProtoStream.Http;

/// <summary>Request methods the parser recognises; anything else is <see cref="Other"/>.</summary>
public enum HttpRequestMethod
{
    /// <summary>A method not listed here; see <see cref="HttpRequest.MethodName"/>.</summary>
    Other,

    /// <summary>GET.</summary>
    Get,

    /// <summary>HEAD.</summary>
    Head,

    /// <summary>POST.</summary>
    Post,

    /// <summary>PUT.</summary>
    Put,

    /// <summary>DELETE.</summary>
    Delete,

    /// <summary>CONNECT.</summary>
    Connect,

    /// <summary>OPTIONS.</summary>
    Options,

    /// <summary>TRACE.</summary>
    Trace,

    /// <summary>PATCH.</summary>
    Patch,
}

/// <summary>HTTP versions this package speaks.</summary>
public enum HttpProtocolVersion
{
    /// <summary>HTTP/1.0.</summary>
    Http10,

    /// <summary>HTTP/1.1.</summary>
    Http11,
}

/// <summary>
/// A parsed request head plus its body. The framework understands what it needs to frame, validate and keep
/// the connection healthy; everything else (path parsing, cookies, content negotiation) is left to the
/// application, as bytes or strings on demand.
/// </summary>
/// <remarks>
/// Requests are reused: an instance is valid until the next read on its session, and every member throws
/// <see cref="Errors.StaleMessageException"/> after that. Call <see cref="Retain"/> to keep a copy.
/// </remarks>
public sealed class HttpRequest
{
    private MessageStamp _stamp;
    private ReadOnlyMemory<byte> _head;
    private int _methodStart;
    private int _methodLength;
    private int _targetStart;
    private int _targetLength;
    private HttpRequestMethod _method;
    private HttpProtocolVersion _version;
    private HeaderEntry[] _headers = new HeaderEntry[16];
    private int _headerCount;
    private long? _contentLength;
    private bool _isChunked;
    private bool _keepAlive;
    private bool _isUpgrade;
    private PipeReader _body = null!;
    private string? _target;

    internal HttpRequest()
    {
    }

    /// <summary>The method.</summary>
    public HttpRequestMethod Method => Current()._method;

    /// <summary>The method as sent.</summary>
    public string MethodName => Current()._method switch
    {
        HttpRequestMethod.Get => "GET",
        HttpRequestMethod.Head => "HEAD",
        HttpRequestMethod.Post => "POST",
        HttpRequestMethod.Put => "PUT",
        HttpRequestMethod.Delete => "DELETE",
        HttpRequestMethod.Connect => "CONNECT",
        HttpRequestMethod.Options => "OPTIONS",
        HttpRequestMethod.Trace => "TRACE",
        HttpRequestMethod.Patch => "PATCH",
        _ => Encoding.ASCII.GetString(_head.Span.Slice(_methodStart, _methodLength)),
    };

    /// <summary>The request target as sent: printable ASCII, not decoded.</summary>
    public ReadOnlyMemory<byte> TargetBytes => Current()._head.Slice(_targetStart, _targetLength);

    /// <summary>The request target as a string, created on first use.</summary>
    public string Target => Current()._target ??= Encoding.ASCII.GetString(_head.Span.Slice(_targetStart, _targetLength));

    /// <summary>The HTTP version of the request.</summary>
    public HttpProtocolVersion Version => Current()._version;

    /// <summary>The header fields, in the order received.</summary>
    public HttpRequestHeaders Headers => new(Current());

    /// <summary>The declared body length, or null when there is none or the body is chunked.</summary>
    public long? ContentLength => Current()._contentLength;

    /// <summary>True when the body uses chunked transfer coding.</summary>
    public bool IsChunked => Current()._isChunked;

    /// <summary>True when the connection stays open after the response.</summary>
    public bool KeepAlive => Current()._keepAlive;

    /// <summary>True when the client asks to switch protocols (<c>Connection: upgrade</c> plus an <c>Upgrade</c> field).</summary>
    public bool IsUpgradeRequest => Current()._isUpgrade;

    /// <summary>
    /// The body, read straight from the connection. Empty when there is none. A body the application does not
    /// read is discarded before the next request, up to the protocol's drain limit.
    /// </summary>
    public PipeReader Body => Current()._body;

    /// <summary>An owned copy of the head that stays valid after the next read. The body is not part of it.</summary>
    public HttpRequest Retain()
    {
        Current();
        var copy = new HttpRequest();
        copy.Load(_head.ToArray(), default, _methodStart, _methodLength, _targetStart, _targetLength, _method, _version);
        for (int i = 0; i < _headerCount; i++)
            copy.AddHeader(_headers[i]);
        copy.SetSemantics(_contentLength, _isChunked, _keepAlive, _isUpgrade);
        copy._body = PipeReader.Create(System.Buffers.ReadOnlySequence<byte>.Empty);
        return copy;
    }

    /// <inheritdoc />
    public override string ToString() => _stamp.IsCurrent ? $"{MethodName} {Target}" : "(stale request)";

    internal int HeaderCount => _headerCount;

    internal ReadOnlySpan<byte> HeadSpan => _head.Span;

    internal ReadOnlyMemory<byte> Head => _head;

    internal HeaderEntry HeaderAt(int index) => _headers[index];

    internal HttpRequest Current()
    {
        _stamp.ThrowIfStale();
        return this;
    }

    internal void Load(ReadOnlyMemory<byte> head, MessageStamp stamp, int methodStart, int methodLength, int targetStart, int targetLength, HttpRequestMethod method, HttpProtocolVersion version)
    {
        _head = head;
        _stamp = stamp;
        _methodStart = methodStart;
        _methodLength = methodLength;
        _targetStart = targetStart;
        _targetLength = targetLength;
        _method = method;
        _version = version;
        _headerCount = 0;
        _contentLength = null;
        _isChunked = false;
        _keepAlive = false;
        _isUpgrade = false;
        _target = null;
    }

    internal void AddHeader(HeaderEntry entry)
    {
        if (_headerCount == _headers.Length)
            Array.Resize(ref _headers, _headers.Length * 2);
        _headers[_headerCount++] = entry;
    }

    internal void SetSemantics(long? contentLength, bool isChunked, bool keepAlive, bool isUpgrade)
    {
        _contentLength = contentLength;
        _isChunked = isChunked;
        _keepAlive = keepAlive;
        _isUpgrade = isUpgrade;
    }

    internal void SetBody(PipeReader body) => _body = body;
}

/// <summary>Where a header field lies in the request head.</summary>
internal readonly record struct HeaderEntry(int NameStart, int NameLength, int ValueStart, int ValueLength);

/// <summary>One header field of a request.</summary>
public readonly struct HttpHeader
{
    internal HttpHeader(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value)
    {
        NameBytes = name;
        ValueBytes = value;
    }

    /// <summary>The field name as sent.</summary>
    public ReadOnlyMemory<byte> NameBytes { get; }

    /// <summary>The field value, without surrounding whitespace.</summary>
    public ReadOnlyMemory<byte> ValueBytes { get; }

    /// <summary>The field name as a string.</summary>
    public string Name => Encoding.ASCII.GetString(NameBytes.Span);

    /// <summary>The field value as a string (ISO-8859-1, as RFC 9110 defines field octets).</summary>
    public string Value => Encoding.Latin1.GetString(ValueBytes.Span);
}

/// <summary>The header fields of a request. Names compare case-insensitively.</summary>
public readonly struct HttpRequestHeaders
{
    private readonly HttpRequest _request;

    internal HttpRequestHeaders(HttpRequest request) => _request = request;

    /// <summary>Number of fields.</summary>
    public int Count => _request.Current().HeaderCount;

    /// <summary>The field at <paramref name="index"/>.</summary>
    public HttpHeader this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            HeaderEntry entry = _request.HeaderAt(index);
            return new HttpHeader(_request.Head.Slice(entry.NameStart, entry.NameLength), _request.Head.Slice(entry.ValueStart, entry.ValueLength));
        }
    }

    /// <summary>True when a field named <paramref name="name"/> is present.</summary>
    public bool Contains(string name) => IndexOf(name) >= 0;

    /// <summary>The value of the first field named <paramref name="name"/>.</summary>
    public bool TryGetValue(string name, out string value)
    {
        int index = IndexOf(name);
        value = index < 0 ? string.Empty : this[index].Value;
        return index >= 0;
    }

    /// <summary>The value bytes of the first field named <paramref name="name"/>, without allocating.</summary>
    public bool TryGetValueBytes(string name, out ReadOnlyMemory<byte> value)
    {
        int index = IndexOf(name);
        value = index < 0 ? default : this[index].ValueBytes;
        return index >= 0;
    }

    /// <summary>True when any field named <paramref name="name"/> lists <paramref name="token"/> (comma-separated, case-insensitive).</summary>
    public bool ContainsToken(string name, string token)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(token);
        Span<byte> tokenBytes = stackalloc byte[Math.Min(token.Length, 256)];
        if (token.Length > 256 || Encoding.ASCII.GetBytes(token, tokenBytes) != token.Length)
            return false;

        ReadOnlySpan<byte> head = _request.Current().HeadSpan;
        for (int i = 0; i < _request.HeaderCount; i++)
        {
            HeaderEntry entry = _request.HeaderAt(i);
            if (Ascii.EqualsIgnoreCase(head.Slice(entry.NameStart, entry.NameLength), name)
                && HttpSyntax.ListContains(head.Slice(entry.ValueStart, entry.ValueLength), tokenBytes))
                return true;
        }

        return false;
    }

    private int IndexOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ReadOnlySpan<byte> head = _request.Current().HeadSpan;
        for (int i = 0; i < _request.HeaderCount; i++)
        {
            HeaderEntry entry = _request.HeaderAt(i);
            if (Ascii.EqualsIgnoreCase(head.Slice(entry.NameStart, entry.NameLength), name))
                return i;
        }

        return -1;
    }
}
