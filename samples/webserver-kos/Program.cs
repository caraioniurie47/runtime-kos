using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// A web server on KasperskyOS: System.Net.HttpListener serves a status page at / and its data as JSON at
// /api/status. The image project's HOST_HTTP_PORT option forwards the port from the host, so a browser there
// opens http://localhost:<port>/. It serves until QEMU is stopped. Log lines go to stderr, like the showcase.

TextWriter o = Console.Error;
var uptime = Stopwatch.StartNew();
long requests = 0;

string? hostPort = Environment.GetEnvironmentVariable("HOST_HTTP_PORT");
int port = hostPort is null ? 8080 : int.Parse(hostPort, CultureInfo.InvariantCulture);

using var listener = new HttpListener();
// On Unix a "*" host listens on IPAddress.Any.
listener.Prefixes.Add($"http://*:{port}/");
listener.Start();
o.WriteLine($"webserver-kos: listening on port {port}");

Task serving = Task.Run(async () =>
{
    while (true)
    {
        HttpListenerContext context = await listener.GetContextAsync();
        // One task per request, so a slow client does not hold up the others.
        _ = Task.Run(() => Handle(context));
    }
});

// A request from inside KasperskyOS first, over a plain socket: the server works before any host client arrives.
using (var client = new TcpClient())
{
    await client.ConnectAsync(IPAddress.Loopback, port);
    NetworkStream stream = client.GetStream();
    await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /api/status HTTP/1.1\r\nHost: localhost:{port}\r\nConnection: close\r\n\r\n"));
    string response = await new StreamReader(stream).ReadToEndAsync();
    o.WriteLine($"webserver-kos: self-test GET /api/status -> {response.Split("\r\n")[0]}");
}

o.WriteLine(hostPort is null
    ? "webserver-kos: HOST_HTTP_PORT unset, so no port is forwarded from the host; configure the image with -D HOST_HTTP_PORT=8080"
    : $"webserver-kos: ready, open http://localhost:{port}/ on the host");
await serving;

async Task Handle(HttpListenerContext context)
{
    HttpListenerRequest request = context.Request;
    HttpListenerResponse response = context.Response;
    long number = Interlocked.Increment(ref requests);
    try
    {
        string path = request.Url?.AbsolutePath ?? "/";
        byte[] body;
        if (path == "/")
        {
            response.ContentType = "text/html; charset=utf-8";
            body = Encoding.UTF8.GetBytes(Page(CurrentStatus()));
        }
        else if (path == "/api/status")
        {
            response.ContentType = "application/json; charset=utf-8";
            body = JsonSerializer.SerializeToUtf8Bytes(CurrentStatus(), StatusJson.Default.Status);
        }
        else
        {
            response.StatusCode = 404;
            response.ContentType = "text/plain; charset=utf-8";
            body = Encoding.UTF8.GetBytes("not found\n");
        }
        response.Headers["Cache-Control"] = "no-store";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        o.WriteLine($"webserver-kos: #{number} {request.HttpMethod} {request.Url?.PathAndQuery} from {request.RemoteEndPoint} -> {response.StatusCode}");
    }
    catch (Exception e)
    {
        o.WriteLine($"webserver-kos: #{number} {request.HttpMethod} {request.Url?.PathAndQuery} failed: {e.GetType().Name}: {e.Message}");
    }
    finally
    {
        response.Close();
    }
}

Status CurrentStatus()
{
    GCMemoryInfo memory = GC.GetGCMemoryInfo();
    return new Status(
        RuntimeInformation.FrameworkDescription,
        RuntimeInformation.RuntimeIdentifier,
        RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.ProcessorCount,
        uptime.ElapsedMilliseconds,
        DateTime.UtcNow,
        Interlocked.Read(ref requests),
        GC.GetTotalMemory(forceFullCollection: false),
        [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)],
        memory.TotalAvailableMemoryBytes,
        ThreadPool.ThreadCount);
}

static string Page(Status s)
{
    static string E(object value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture)) ?? "";
    // The table's cells carry ids that the script refreshes from /api/status every two seconds.
    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>.NET on KasperskyOS</title>
        <style>
          :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
          body { max-width: 44rem; margin: 2rem; padding: 0 1rem; line-height: 1.5; }
          table { border-collapse: collapse; width: 100%; }
          th, td { text-align: left; padding: .35rem .6rem; border-bottom: 1px solid color-mix(in srgb, currentColor 20%, transparent); }
          th { font-weight: 600; width: 40%; }
          code { font-size: .95em; }
          footer { margin-top: 1.5rem; font-size: .9em; opacity: .75; }
        </style>
        </head>
        <body>
        <h1>Hello from .NET on KasperskyOS</h1>
        <p>This page is served by <code>System.Net.HttpListener</code> in a C# program compiled with NativeAOT,
        running on KasperskyOS under QEMU.</p>
        <table>
          <tr><th>Framework</th><td>{{E(s.Framework)}}</td></tr>
          <tr><th>Runtime ID</th><td>{{E(s.RuntimeIdentifier)}}</td></tr>
          <tr><th>Architecture</th><td>{{E(s.Architecture)}}</td></tr>
          <tr><th>Processors</th><td>{{E(s.ProcessorCount)}}</td></tr>
          <tr><th>Up</th><td id="uptime">{{E(s.UptimeMs / 1000)}} s</td></tr>
          <tr><th>UTC time</th><td id="utc">{{E(s.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}}</td></tr>
          <tr><th>Requests served</th><td id="requests">{{E(s.RequestsServed)}}</td></tr>
          <tr><th>Managed heap</th><td id="heap">{{E(s.ManagedHeapBytes / 1024)}} KiB</td></tr>
          <tr><th>GC collections (gen 0, 1, 2)</th><td id="gcs">{{E(string.Join(", ", s.Collections))}}</td></tr>
          <tr><th>Thread pool threads</th><td id="threads">{{E(s.ThreadPoolThreads)}}</td></tr>
        </table>
        <footer>JSON: <a href="/api/status"><code>/api/status</code></a></footer>
        <script>
        async function refresh() {
          try {
            const s = await (await fetch("/api/status", { cache: "no-store" })).json();
            document.getElementById("uptime").textContent = Math.floor(s.uptimeMs / 1000) + " s";
            document.getElementById("utc").textContent = s.utcNow.replace("T", " ").slice(0, 19);
            document.getElementById("requests").textContent = s.requestsServed;
            document.getElementById("heap").textContent = Math.floor(s.managedHeapBytes / 1024) + " KiB";
            document.getElementById("gcs").textContent = s.collections.join(", ");
            document.getElementById("threads").textContent = s.threadPoolThreads;
          } catch (e) { }
        }
        setInterval(refresh, 2000);
        </script>
        </body>
        </html>
        """;
}

record Status(
    // No OS version: RuntimeInformation.OSDescription comes from uname(), which SDK 1.4.0.102's C library fills with
    // the fixed strings "KOS", "1.0" and "1.0".
    string Framework,
    string RuntimeIdentifier,
    string Architecture,
    int ProcessorCount,
    long UptimeMs,
    DateTime UtcNow,
    long RequestsServed,
    long ManagedHeapBytes,
    int[] Collections,
    long TotalAvailableMemoryBytes,
    int ThreadPoolThreads);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Status))]
partial class StatusJson : JsonSerializerContext
{
}
