namespace WebSocketEcho;

/// <summary>The test page: opens a WebSocket to the server it was loaded from and shows the echoes.</summary>
internal static class EchoPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>ProtoStream echo</title>
          <style>
            body { font: 15px/1.5 system-ui, sans-serif; margin: 2rem auto; max-width: 40rem; padding: 0 1rem; }
            #log { border: 1px solid #ccc; border-radius: 6px; padding: .5rem 1rem; min-height: 8rem; white-space: pre-wrap; }
            form { display: flex; gap: .5rem; margin: 1rem 0; }
            input { flex: 1; padding: .4rem; }
          </style>
        </head>
        <body>
          <h1>ProtoStream WebSocket echo</h1>
          <p id="status">Connecting…</p>
          <form id="form"><input id="text" autocomplete="off" placeholder="Type a message"><button>Send</button></form>
          <div id="log"></div>
          <script>
            const log = document.getElementById("log");
            const status = document.getElementById("status");
            const socket = new WebSocket(`ws://${location.host}/echo`);
            const write = line => log.textContent += line + "\n";
            socket.onopen = () => status.textContent = "Connected.";
            socket.onclose = e => status.textContent = `Closed (${e.code}).`;
            socket.onmessage = e => write("echo: " + e.data);
            document.getElementById("form").onsubmit = e => {
              e.preventDefault();
              const text = document.getElementById("text");
              socket.send(text.value);
              write("sent: " + text.value);
              text.value = "";
            };
          </script>
        </body>
        </html>
        """;
}
