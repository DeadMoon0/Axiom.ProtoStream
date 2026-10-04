using System;
using System.Text;
using System.Threading.Tasks;

namespace Axiom.ProtoStream.Http.Tests;

public sealed partial class Http11Tests
{
    // Content above the inline limit goes to the connection in slices instead of through the write buffer; what the
    // peer receives must not change, for a frozen response sent twice nor for HEAD.
    [Fact]
    public async Task LargeContentArrivesWhole_FromAFrozenResponseSentTwice_AndHeadSendsNone()
    {
        await using HttpPair http = await HttpPair.OpenAsync();
        byte[] content = new byte[200_000];
        for (int i = 0; i < content.Length; i++)
            content[i] = (byte)('a' + (i % 26));
        HttpResponse response = HttpResponse.Bytes(HttpStatus.Ok, content, "text/plain").Freeze();
        string expectedBody = Encoding.Latin1.GetString(content);

        foreach (string method in new[] { "GET", "HEAD", "GET" })
        {
            await http.SendAsync(method + " / HTTP/1.1\r\nHost: x\r\n\r\n");
            await http.Session.ReadAsync(Ct);

            // The pipe pauses at 64 KiB: the client must read while the server writes.
            Task written = http.Session.WriteAsync(response, Ct).AsTask();
            string received = method == "HEAD"
                ? await http.ReceiveAsync(text => text.EndsWith("\r\n\r\n", StringComparison.Ordinal))
                : await http.ReceiveResponseAsync();
            await written;

            Assert.Contains("Content-Length: 200000\r\n", received, StringComparison.Ordinal);
            string body = received[(received.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
            Assert.Equal(method == "HEAD" ? string.Empty : expectedBody, body);
        }
    }
}
