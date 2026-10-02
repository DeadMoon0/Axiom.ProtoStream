using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ProtoStream.Codecs;
using ProtoStream.Errors;
using ProtoStream.WebSockets.Internal;

namespace ProtoStream.WebSockets;

/// <summary>Frame opcodes (RFC 6455 section 5.2).</summary>
internal enum WsOpcode : byte
{
    Continuation = 0x0,
    Text = 0x1,
    Binary = 0x2,
    Close = 0x8,
    Ping = 0x9,
    Pong = 0xA,
}

/// <summary>The fixed layout of a frame header (RFC 6455 section 5.2).</summary>
internal static class WsFrame
{
    /// <summary>First byte: this frame ends its message.</summary>
    public const byte FinBit = 0x80;

    /// <summary>First byte: bits reserved for extensions.</summary>
    public const byte ReservedBits = 0x70;

    /// <summary>First byte: the opcode.</summary>
    public const byte OpcodeMask = 0x0F;

    /// <summary>Second byte: the payload is masked.</summary>
    public const byte MaskBit = 0x80;

    /// <summary>Second byte: the 7-bit payload length or a marker for an extended length.</summary>
    public const byte LengthMask = 0x7F;

    /// <summary>7-bit length value announcing a 16-bit extended length.</summary>
    public const byte Length16Marker = 126;

    /// <summary>7-bit length value announcing a 64-bit extended length.</summary>
    public const byte Length64Marker = 127;

    /// <summary>Largest payload carried in the 7-bit length.</summary>
    public const int MaxSmallLength = 125;

    /// <summary>Largest payload of a control frame.</summary>
    public const int MaxControlPayload = 125;

    /// <summary>Largest close reason: the control payload minus the two-byte status code.</summary>
    public const int MaxCloseReasonBytes = MaxControlPayload - CloseCodeSize;

    /// <summary>Size of the status code at the start of a close payload.</summary>
    public const int CloseCodeSize = 2;

    /// <summary>Size of the two fixed header bytes.</summary>
    public const int BaseHeaderSize = 2;

    /// <summary>Size of the masking key.</summary>
    public const int MaskKeySize = 4;

    /// <summary>Largest header: base, 64-bit length, masking key.</summary>
    public const int MaxHeaderSize = BaseHeaderSize + sizeof(ulong) + MaskKeySize;

    public static bool IsControl(WsOpcode opcode) => opcode >= WsOpcode.Close;

    public static bool IsDefined(WsOpcode opcode) => opcode is WsOpcode.Continuation or WsOpcode.Text or WsOpcode.Binary or WsOpcode.Close or WsOpcode.Ping or WsOpcode.Pong;
}

/// <summary>
/// RFC 6455 framing for one session. Everything section 5 calls a protocol error closes with
/// <see cref="WsCloseCode.ProtocolError"/>, invalid UTF-8 with <see cref="WsCloseCode.InvalidPayloadData"/> and an
/// oversized message with <see cref="WsCloseCode.MessageTooBig"/> — the latter as soon as the length is declared.
/// </summary>
internal sealed class WsCodec : ICodec<WsMessage, WsMessage>, IDisposable
{
    private const int StackTextLimit = 512;

    /// <summary>Marks that outgoing messages are not split into fragments.</summary>
    private const int NoFragmentation = int.MaxValue;

    private readonly bool _isServer;
    private readonly int _maxMessageSize;
    private readonly int _fragmentSize;
    private readonly GrowableBuffer _message;
    private readonly byte[] _control = new byte[WsFrame.MaxControlPayload];
    private readonly WsText[] _texts = [new(), new()];
    private readonly WsBinary[] _binaries = [new(), new()];
    private readonly WsPing[] _pings = [new(), new()];
    private readonly WsPong[] _pongs = [new(), new()];
    private readonly WsClose[] _closes = [new(), new()];
    private int _next;
    private WsOpcode? _fragmentedOpcode;

    public WsCodec(bool isServer, WebSocketOptions options)
    {
        _isServer = isServer;
        _maxMessageSize = options.MaxMessageSize;
        _fragmentSize = options.FragmentSize ?? NoFragmentation;
        _message = new GrowableBuffer(options.MaxMessageSize);
    }

    public ParseResult TryParse(ref MessageParseContext context, out WsMessage message)
    {
        message = null!;
        ReadOnlySequence<byte> input = context.Input;
        long offset = 0;
        while (true)
        {
            ReadOnlySequence<byte> rest = input.Slice(offset);
            if (rest.Length < WsFrame.BaseHeaderSize)
                return NeedMore(ref context, input, offset);

            Span<byte> header = stackalloc byte[WsFrame.MaxHeaderSize];
            int available = (int)Math.Min(rest.Length, WsFrame.MaxHeaderSize);
            rest.Slice(0, available).CopyTo(header);

            bool fin = (header[0] & WsFrame.FinBit) != 0;
            var opcode = (WsOpcode)(header[0] & WsFrame.OpcodeMask);
            bool masked = (header[1] & WsFrame.MaskBit) != 0;
            long length = header[1] & WsFrame.LengthMask;

            if ((header[0] & WsFrame.ReservedBits) != 0)
                return ProtocolError(ref context, "A reserved bit is set; no extension was negotiated.");
            if (!WsFrame.IsDefined(opcode))
                return ProtocolError(ref context, $"Opcode {(int)opcode} is reserved.");
            if (masked != _isServer)
                return ProtocolError(ref context, _isServer ? "A client frame is not masked." : "A server frame is masked.");

            int headerSize = WsFrame.BaseHeaderSize;
            if (length == WsFrame.Length16Marker)
            {
                headerSize += sizeof(ushort);
                if (available < headerSize)
                    return NeedMore(ref context, input, offset);
                length = BinaryPrimitives.ReadUInt16BigEndian(header[WsFrame.BaseHeaderSize..]);
                if (length <= WsFrame.MaxSmallLength)
                    return ProtocolError(ref context, "A frame length is not minimally encoded.");
            }
            else if (length == WsFrame.Length64Marker)
            {
                headerSize += sizeof(ulong);
                if (available < headerSize)
                    return NeedMore(ref context, input, offset);
                ulong length64 = BinaryPrimitives.ReadUInt64BigEndian(header[WsFrame.BaseHeaderSize..]);
                if (length64 > long.MaxValue)
                    return ProtocolError(ref context, "A frame length has its most significant bit set.");
                if (length64 <= ushort.MaxValue)
                    return ProtocolError(ref context, "A frame length is not minimally encoded.");
                length = (long)length64;
            }

            bool control = WsFrame.IsControl(opcode);
            if (control && (!fin || length > WsFrame.MaxControlPayload))
                return ProtocolError(ref context, "A control frame is fragmented or longer than 125 bytes.");
            if (!control && (opcode == WsOpcode.Continuation) != _fragmentedOpcode.HasValue)
                return ProtocolError(ref context, opcode == WsOpcode.Continuation ? "A continuation frame has no message to continue." : "A new message started before the previous one ended.");
            long assembled = opcode == WsOpcode.Continuation ? _message.Length : 0;
            if (!control && assembled + length > _maxMessageSize)
                return context.Invalid(ViolationCode.LimitExceeded, WsCloseCode.MessageTooBig, $"A message exceeds {_maxMessageSize} bytes.");

            int maskOffset = headerSize;
            if (masked)
                headerSize += WsFrame.MaskKeySize;
            if (rest.Length < headerSize + length)
                return NeedMore(ref context, input, offset);

            ReadOnlySpan<byte> mask = masked ? header.Slice(maskOffset, WsFrame.MaskKeySize) : default;
            ReadOnlySequence<byte> payload = rest.Slice(headerSize, length);
            long frameEnd = offset + headerSize + length;

            if (control)
                return Control(ref context, opcode, payload, mask, input.GetPosition(frameEnd), out message);

            if (opcode != WsOpcode.Continuation)
            {
                _message.Reset();
                _fragmentedOpcode = opcode;
            }

            Unmask(payload, mask, _message.Append((int)length));
            if (!fin)
            {
                offset = frameEnd;
                continue; // the fragment is copied; look for the next one in the same input
            }

            WsOpcode messageOpcode = _fragmentedOpcode!.Value;
            _fragmentedOpcode = null;
            ReadOnlyMemory<byte> data = _message.Memory;
            if (messageOpcode == WsOpcode.Text)
            {
                if (!System.Text.Unicode.Utf8.IsValid(data.Span))
                    return context.Invalid(ViolationCode.InvalidData, WsCloseCode.InvalidPayloadData, "A text message is not valid UTF-8.");
                WsText text = _texts[_next ^= 1];
                text.Load(data, context.Stamp);
                message = text;
            }
            else
            {
                WsBinary binary = _binaries[_next ^= 1];
                binary.Load(data, context.Stamp);
                message = binary;
            }

            return context.Done(input.GetPosition(frameEnd));
        }
    }

    public WriteResult Write(WsMessage message, IBufferWriter<byte> output)
    {
        switch (message)
        {
            case WsText text:
            {
                text.CheckCurrent();
                int length = text.Utf8Length;
                byte[]? rented = null;
                Span<byte> utf8 = length <= StackTextLimit ? stackalloc byte[length] : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
                text.CopyUtf8(utf8);
                WriteMessage(WsOpcode.Text, utf8, output);
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
                break;
            }

            case WsBinary binary:
                WriteMessage(WsOpcode.Binary, binary.Data.Span, output);
                break;
            case WsPing ping:
                WriteFrame(WsOpcode.Ping, fin: true, ping.Data.Span, output);
                break;
            case WsPong pong:
                WriteFrame(WsOpcode.Pong, fin: true, pong.Data.Span, output);
                break;
            case WsClose close:
            {
                Span<byte> payload = stackalloc byte[WsFrame.MaxControlPayload];
                int length = 0;
                if (close.Code is { } code)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)code);
                    length = WsFrame.CloseCodeSize + Encoding.UTF8.GetBytes(close.Reason, payload[WsFrame.CloseCodeSize..]);
                }

                WriteFrame(WsOpcode.Close, fin: true, payload[..length], output);
                break;
            }

            default:
                throw new ProtocolStateException($"{message.GetType().Name} is not a WebSocket message.");
        }

        return WriteResult.Done;
    }

    public void Dispose() => _message.Dispose();

    private ParseResult Control(ref MessageParseContext context, WsOpcode opcode, in ReadOnlySequence<byte> payload, scoped ReadOnlySpan<byte> mask, SequencePosition end, out WsMessage message)
    {
        message = null!;
        Span<byte> data = _control.AsSpan(0, (int)payload.Length);
        Unmask(payload, mask, data);
        ReadOnlyMemory<byte> memory = _control.AsMemory(0, data.Length);

        switch (opcode)
        {
            case WsOpcode.Ping:
                WsPing ping = _pings[_next ^= 1];
                ping.Load(memory, context.Stamp);
                message = ping;
                break;
            case WsOpcode.Pong:
                WsPong pong = _pongs[_next ^= 1];
                pong.Load(memory, context.Stamp);
                message = pong;
                break;
            default:
                if (data.Length is > 0 and < WsFrame.CloseCodeSize)
                    return ProtocolError(ref context, "A close frame carries a payload too short for a status code.");
                int? code = null;
                string reason = string.Empty;
                if (data.Length >= WsFrame.CloseCodeSize)
                {
                    code = BinaryPrimitives.ReadUInt16BigEndian(data);
                    if (!WsCloseCode.IsSendable(code.Value))
                        return ProtocolError(ref context, $"Close code {code} may not be sent.");
                    ReadOnlySpan<byte> reasonBytes = data[WsFrame.CloseCodeSize..];
                    if (!System.Text.Unicode.Utf8.IsValid(reasonBytes))
                        return context.Invalid(ViolationCode.InvalidData, WsCloseCode.InvalidPayloadData, "A close reason is not valid UTF-8.");
                    reason = Encoding.UTF8.GetString(reasonBytes);
                }

                WsClose close = _closes[_next ^= 1];
                close.Load(code, reason, context.Stamp);
                message = close;
                break;
        }

        return context.Done(end);
    }

    private static ParseResult NeedMore(ref MessageParseContext context, in ReadOnlySequence<byte> input, long offset) =>
        offset == 0 ? context.NeedMore() : context.NeedMore(input.GetPosition(offset));

    private static ParseResult ProtocolError(ref MessageParseContext context, string detail) =>
        context.Invalid(ViolationCode.Malformed, WsCloseCode.ProtocolError, detail);

    private void WriteMessage(WsOpcode opcode, ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        if (_fragmentSize == NoFragmentation || payload.Length <= _fragmentSize)
        {
            WriteFrame(opcode, fin: true, payload, output);
            return;
        }

        for (int start = 0; start < payload.Length; start += _fragmentSize)
        {
            int length = Math.Min(_fragmentSize, payload.Length - start);
            WriteFrame(start == 0 ? opcode : WsOpcode.Continuation, start + length == payload.Length, payload.Slice(start, length), output);
        }
    }

    private void WriteFrame(WsOpcode opcode, bool fin, ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        bool mask = !_isServer;
        int extended = payload.Length <= WsFrame.MaxSmallLength ? 0 : payload.Length <= ushort.MaxValue ? sizeof(ushort) : sizeof(ulong);
        int keyOffset = WsFrame.BaseHeaderSize + extended;
        int headerSize = keyOffset + (mask ? WsFrame.MaskKeySize : 0);
        Span<byte> frame = output.GetSpan(headerSize + payload.Length);

        frame[0] = (byte)((fin ? WsFrame.FinBit : 0) | (byte)opcode);
        byte maskBit = mask ? WsFrame.MaskBit : (byte)0;
        switch (extended)
        {
            case 0:
                frame[1] = (byte)(maskBit | payload.Length);
                break;
            case sizeof(ushort):
                frame[1] = (byte)(maskBit | WsFrame.Length16Marker);
                BinaryPrimitives.WriteUInt16BigEndian(frame[WsFrame.BaseHeaderSize..], (ushort)payload.Length);
                break;
            default:
                frame[1] = (byte)(maskBit | WsFrame.Length64Marker);
                BinaryPrimitives.WriteUInt64BigEndian(frame[WsFrame.BaseHeaderSize..], (ulong)payload.Length);
                break;
        }

        Span<byte> body = frame.Slice(headerSize, payload.Length);
        if (mask)
        {
            // A predictable key would defeat the point of masking: cache-poisoning resistance for proxies.
            Span<byte> key = frame.Slice(keyOffset, WsFrame.MaskKeySize);
            RandomNumberGenerator.Fill(key);
            Xor(payload, key, body);
        }
        else
        {
            payload.CopyTo(body);
        }

        output.Advance(headerSize + payload.Length);
    }

    private static void Unmask(in ReadOnlySequence<byte> payload, ReadOnlySpan<byte> mask, Span<byte> destination)
    {
        if (mask.IsEmpty)
        {
            payload.CopyTo(destination);
            return;
        }

        int written = 0;
        Span<byte> rotated = stackalloc byte[WsFrame.MaskKeySize];
        foreach (ReadOnlyMemory<byte> segment in payload)
        {
            ReadOnlySpan<byte> source = segment.Span;
            // The key continues where the previous segment stopped.
            for (int i = 0; i < WsFrame.MaskKeySize; i++)
                rotated[i] = mask[(written + i) % WsFrame.MaskKeySize];
            Xor(source, rotated, destination.Slice(written, source.Length));
            written += source.Length;
        }
    }

    private static void Xor(ReadOnlySpan<byte> source, ReadOnlySpan<byte> key, Span<byte> destination)
    {
        uint key32 = BinaryPrimitives.ReadUInt32LittleEndian(key);
        ulong key64 = key32 | ((ulong)key32 << 32);
        int i = 0;
        for (; i + sizeof(ulong) <= source.Length; i += sizeof(ulong))
            BinaryPrimitives.WriteUInt64LittleEndian(destination[i..], BinaryPrimitives.ReadUInt64LittleEndian(source[i..]) ^ key64);
        for (; i < source.Length; i++)
            destination[i] = (byte)(source[i] ^ key[i % WsFrame.MaskKeySize]);
    }
}
