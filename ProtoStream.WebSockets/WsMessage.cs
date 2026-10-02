using System;
using System.Text;
using ProtoStream.Codecs;
using ProtoStream.Errors;

namespace ProtoStream.WebSockets;

/// <summary>
/// A WebSocket message. Received messages are reused by the session: valid until its next read, after which
/// they throw <see cref="StaleMessageException"/>; call <c>Retain()</c> to keep one. Messages created with the
/// static factories are owned and can be sent any number of times.
/// </summary>
public abstract class WsMessage
{
    private MessageStamp _stamp;

    private protected WsMessage()
    {
    }

    /// <summary>A text message.</summary>
    public static WsText CreateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new WsText(text);
    }

    /// <summary>A binary message.</summary>
    public static WsBinary CreateBinary(ReadOnlyMemory<byte> data) => WsBinary.Owned(data);

    /// <summary>A ping; at most <see cref="MaxControlPayload"/> bytes of data.</summary>
    public static WsPing CreatePing(ReadOnlyMemory<byte> data) => WsPing.Owned(CheckControl(data));

    /// <summary>A pong; at most <see cref="MaxControlPayload"/> bytes of data.</summary>
    public static WsPong CreatePong(ReadOnlyMemory<byte> data) => WsPong.Owned(CheckControl(data));

    /// <summary>
    /// A close frame with a status code from <see cref="WsCloseCode"/> and a reason of at most
    /// <see cref="MaxCloseReasonBytes"/> UTF-8 bytes, or with neither when <paramref name="code"/> is null.
    /// </summary>
    public static WsClose CreateClose(int? code, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (code is null && reason.Length > 0)
            throw new ProtocolStateException("A close frame can only carry a reason together with a status code.");
        if (code is { } value && !WsCloseCode.IsSendable(value))
            throw new ProtocolStateException($"{value} is not a close code that may be sent.");
        if (Encoding.UTF8.GetByteCount(reason) > MaxCloseReasonBytes)
            throw new ProtocolStateException($"A close reason is limited to {MaxCloseReasonBytes} UTF-8 bytes.");
        return WsClose.Owned(code, reason);
    }

    /// <summary>Largest payload of a ping, pong or close frame.</summary>
    public const int MaxControlPayload = WsFrame.MaxControlPayload;

    /// <summary>Largest close reason in UTF-8 bytes.</summary>
    public const int MaxCloseReasonBytes = WsFrame.MaxCloseReasonBytes;

    internal void Stamp(MessageStamp stamp) => _stamp = stamp;

    private protected void Check() => _stamp.ThrowIfStale();

    private static ReadOnlyMemory<byte> CheckControl(ReadOnlyMemory<byte> data) =>
        data.Length <= MaxControlPayload ? data : throw new ProtocolStateException($"A control frame carries at most {MaxControlPayload} bytes.");
}

/// <summary>A text message. Received text is validated UTF-8.</summary>
public sealed class WsText : WsMessage
{
    private ReadOnlyMemory<byte> _utf8;
    private string? _text;

    internal WsText()
    {
    }

    internal WsText(string text) => _text = text;

    /// <summary>The text, decoded on first use.</summary>
    public string Text
    {
        get
        {
            Check();
            return _text ??= Encoding.UTF8.GetString(_utf8.Span);
        }
    }

    /// <summary>The text as UTF-8 bytes. For a received message a view of the session's buffer, not a copy.</summary>
    public ReadOnlyMemory<byte> Utf8
    {
        get
        {
            Check();
            if (_utf8.IsEmpty && !string.IsNullOrEmpty(_text))
                _utf8 = Encoding.UTF8.GetBytes(_text);
            return _utf8;
        }
    }

    /// <summary>An owned copy that stays valid after the next read.</summary>
    public WsText Retain() => new(Text);

    internal void Load(ReadOnlyMemory<byte> utf8, MessageStamp stamp)
    {
        _utf8 = utf8;
        _text = null;
        Stamp(stamp);
    }

    internal int Utf8Length => _text is not null && _utf8.IsEmpty ? Encoding.UTF8.GetByteCount(_text) : _utf8.Length;

    internal void CopyUtf8(Span<byte> destination)
    {
        if (_text is not null && _utf8.IsEmpty)
            Encoding.UTF8.GetBytes(_text, destination);
        else
            _utf8.Span.CopyTo(destination);
    }

    internal void CheckCurrent() => Check();
}

/// <summary>Data carried by binary, ping and pong messages.</summary>
public abstract class WsDataMessage : WsMessage
{
    private ReadOnlyMemory<byte> _data;

    private protected WsDataMessage()
    {
    }

    /// <summary>The data. For a received message a view of the session's buffer, not a copy.</summary>
    public ReadOnlyMemory<byte> Data
    {
        get
        {
            Check();
            return _data;
        }
    }

    internal void Load(ReadOnlyMemory<byte> data, MessageStamp stamp)
    {
        _data = data;
        Stamp(stamp);
    }
}

/// <summary>A binary message.</summary>
public sealed class WsBinary : WsDataMessage
{
    internal WsBinary()
    {
    }

    /// <summary>An owned copy that stays valid after the next read.</summary>
    public WsBinary Retain() => Owned(Data.ToArray());

    internal static WsBinary Owned(ReadOnlyMemory<byte> data)
    {
        var message = new WsBinary();
        message.Load(data, default);
        return message;
    }
}

/// <summary>A ping; the framework answers it with a pong.</summary>
public sealed class WsPing : WsDataMessage
{
    internal WsPing()
    {
    }

    internal static WsPing Owned(ReadOnlyMemory<byte> data)
    {
        var message = new WsPing();
        message.Load(data, default);
        return message;
    }
}

/// <summary>A pong.</summary>
public sealed class WsPong : WsDataMessage
{
    internal WsPong()
    {
    }

    internal static WsPong Owned(ReadOnlyMemory<byte> data)
    {
        var message = new WsPong();
        message.Load(data, default);
        return message;
    }
}

/// <summary>A close frame.</summary>
public sealed class WsClose : WsMessage
{
    private int? _code;
    private string _reason = string.Empty;

    internal WsClose()
    {
    }

    /// <summary>The status code, or null when the frame carried none.</summary>
    public int? Code
    {
        get
        {
            Check();
            return _code;
        }
    }

    /// <summary>The reason, possibly empty.</summary>
    public string Reason
    {
        get
        {
            Check();
            return _reason;
        }
    }

    internal static WsClose Owned(int? code, string reason)
    {
        var message = new WsClose();
        message.Load(code, reason, default);
        return message;
    }

    internal void Load(int? code, string reason, MessageStamp stamp)
    {
        _code = code;
        _reason = reason;
        Stamp(stamp);
    }
}
