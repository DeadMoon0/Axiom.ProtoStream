using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Axiom.ProtoStream.Errors;
using Axiom.ProtoStream.Http;

namespace Axiom.ProtoStream.WebSockets.Tests;

/// <summary>
/// Many real TCP connections at once, HTTP bodies and WebSocket messages mixed, with the server disposing some
/// connections while they read or write. Every byte echoed back is checked against what that client sent: a
/// buffer shared between two connections, or released while still in use, shows up as a mismatch.
/// </summary>
public sealed class IsolationStressTests
{
    private const int Clients = 32;
    private const int Rounds = 30;
    private const int MaxPayload = 20_000;

    /// <summary>One request in this many makes the server dispose that connection from another task.</summary>
    private const int DisposeOneIn = 12;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    private readonly List<string> _mismatchDetails = [];
    private int _mismatches;
    private int _completed;
    private int _dropped;

    [Fact]
    public async Task ManyConnectionsUnderChurnOnlyEverSeeTheirOwnBytes()
    {
        using var shutdown = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = AcceptAsync(listener, shutdown.Token);

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Clients).Select(id => Task.Run(() => RunClientAsync(id, port)))).WaitAsync(TestTimeout);
        }
        finally
        {
            shutdown.Cancel();
            listener.Stop();
            await server;
        }

        Assert.True(_mismatches == 0, string.Join(Environment.NewLine, _mismatchDetails.Take(10)));
        Assert.Equal(Clients * Rounds, _completed);
        Assert.True(_dropped > 0, "no connection was disposed mid-flight, so the churn path was not exercised");
    }

    // ---- server ------------------------------------------------------------------------------------

    private static async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        var running = new List<Task>();
        try
        {
            while (true)
            {
                Socket socket = await listener.AcceptSocketAsync(cancellationToken);
                running.Add(Task.Run(() => ServeAsync(socket)));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        await Task.WhenAll(running);
    }

    private static async Task ServeAsync(Socket socket)
    {
        await using Connection connection = Connection.FromStream(new NetworkStream(socket, ownsSocket: true));
        try
        {
            Session<HttpRequest, HttpResponse> http = await connection.OpenAsync(Http11.Server(), CancellationToken.None);
            await foreach (HttpRequest request in http.Messages)
            {
                if (WebSocket.IsUpgrade(request) && WebSocket.TryAccept(request, new WebSocketAcceptOptions(), out var accepted, out _))
                {
                    Session<WsMessage, WsMessage> ws = await http.SwitchAsync(accepted!, CancellationToken.None);
                    await EchoAsync(connection, ws);
                    return;
                }

                byte[] body = await ReadAllAsync(request.Body);
                if (body.Length > 0 && body[0] % DisposeOneIn == 0)
                    _ = Task.Run(() => connection.DisposeAsync().AsTask()); // races the write below and the next read

                await http.WriteAsync(HttpResponse.Bytes(HttpStatus.Ok, body, "application/octet-stream"), CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is ProtoStreamException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // A connection disposed under its own loop ends with one of these; never with another exception.
        }
    }

    private static async Task EchoAsync(Connection connection, Session<WsMessage, WsMessage> ws)
    {
        await foreach (WsMessage message in ws.Messages)
        {
            if (message is not WsBinary binary)
                continue;
            if (binary.Data.Span[0] % DisposeOneIn == 0)
                _ = Task.Run(() => connection.DisposeAsync().AsTask());

            // Written straight from the pooled received message, as a relay would.
            await ws.WriteAsync(binary, CancellationToken.None);
        }
    }

    // ---- client ------------------------------------------------------------------------------------

    private async Task RunClientAsync(int id, int port)
    {
        var random = new Random(id);
        int round = 0;
        while (round < Rounds)
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port);
            NetworkStream stream = client.GetStream();
            bool webSocket = id % 2 == 1;
            try
            {
                if (webSocket)
                    await UpgradeAsync(stream);
                for (; round < Rounds; round++)
                {
                    byte[] sent = Payload(random, id, round);
                    byte[] echoed = webSocket ? await WebSocketRoundTripAsync(stream, sent, random) : await HttpRoundTripAsync(stream, sent);
                    if (!echoed.AsSpan().SequenceEqual(sent))
                    {
                        Interlocked.Increment(ref _mismatches);
                        lock (_mismatchDetails)
                            _mismatchDetails.Add(Describe(webSocket, id, round, sent, echoed));
                    }
                    Interlocked.Increment(ref _completed);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
            {
                // The server disposed this connection mid-flight: count it and carry on with a new one.
                Interlocked.Increment(ref _dropped);
            }
        }
    }

    private static string Describe(bool webSocket, int id, int round, byte[] sent, byte[] echoed)
    {
        string owner = echoed.Length >= 12
            ? $"stamped client {BinaryPrimitives.ReadInt32BigEndian(echoed.AsSpan(4))} round {BinaryPrimitives.ReadInt32BigEndian(echoed.AsSpan(8))}"
            : "too short to carry a stamp";
        int firstDifference = echoed.AsSpan().CommonPrefixLength(sent);
        return $"{(webSocket ? "ws" : "http")} client {id} round {round}: sent {sent.Length} bytes, got {echoed.Length} ({owner}), first difference at {firstDifference}";
    }

    /// <summary>Random length, random bytes, stamped with the client and round so a foreign echo cannot match.</summary>
    private static byte[] Payload(Random random, int id, int round)
    {
        var payload = new byte[random.Next(16, MaxPayload)];
        random.NextBytes(payload);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), id);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), round);
        return payload;
    }

    private static async Task<byte[]> HttpRoundTripAsync(NetworkStream stream, byte[] body)
    {
        byte[] head = Encoding.ASCII.GetBytes($"POST /echo HTTP/1.1\r\nHost: test\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);

        string responseHead = await ReadHeadAsync(stream);
        if (!responseHead.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
            return Encoding.ASCII.GetBytes(responseHead); // never expected: reported as a mismatch
        int lengthAt = responseHead.IndexOf("Content-Length: ", StringComparison.Ordinal);
        int length = int.Parse(responseHead[(lengthAt + 16)..].Split("\r\n")[0]);
        var echoed = new byte[length];
        await stream.ReadExactlyAsync(echoed);
        return echoed;
    }

    private static async Task UpgradeAsync(NetworkStream stream)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /ws HTTP/1.1\r\nHost: test\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n" +
            "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n"));
        string head = await ReadHeadAsync(stream);
        if (!head.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
            throw new IOException("The upgrade was refused.");
    }

    private static async Task<byte[]> WebSocketRoundTripAsync(NetworkStream stream, byte[] data, Random random)
    {
        // A masked binary frame with a 16- or 64-bit length, as a browser sends it.
        var mask = new byte[4];
        random.NextBytes(mask);
        var frame = new List<byte> { 0x82 };
        if (data.Length <= ushort.MaxValue)
        {
            frame.Add(0x80 | 126);
            frame.Add((byte)(data.Length >> 8));
            frame.Add((byte)data.Length);
        }
        else
        {
            frame.Add(0x80 | 127);
            for (int shift = 56; shift >= 0; shift -= 8)
                frame.Add((byte)((long)data.Length >> shift));
        }

        frame.AddRange(mask);
        for (int i = 0; i < data.Length; i++)
            frame.Add((byte)(data[i] ^ mask[i % 4]));
        await stream.WriteAsync(frame.ToArray());

        var header = new byte[2];
        await stream.ReadExactlyAsync(header);
        const byte FinalClose = 0x88;
        if (header[0] == FinalClose)
            throw new EndOfStreamException("The server closed the WebSocket."); // the close frame of a disposal
        long length = header[1] & 0x7F;
        if (length == 126)
        {
            var extended = new byte[2];
            await stream.ReadExactlyAsync(extended);
            length = BinaryPrimitives.ReadUInt16BigEndian(extended);
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            await stream.ReadExactlyAsync(extended);
            length = BinaryPrimitives.ReadInt64BigEndian(extended);
        }

        var echoed = new byte[length];
        await stream.ReadExactlyAsync(echoed);
        return echoed;
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
        {
            if (await stream.ReadAsync(one) == 0)
                throw new EndOfStreamException();
            head.Add(one[0]);
        }

        return Encoding.ASCII.GetString([.. head]);
    }

    private static async Task<byte[]> ReadAllAsync(PipeReader body)
    {
        var all = new List<byte>();
        while (true)
        {
            ReadResult read = await body.ReadAsync();
            foreach (ReadOnlyMemory<byte> segment in read.Buffer)
                all.AddRange(segment.Span.ToArray());
            body.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                return [.. all];
        }
    }
}
