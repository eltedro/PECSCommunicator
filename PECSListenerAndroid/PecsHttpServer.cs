using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PECSListenerAndroid;

/// <summary>
/// Minimal HTTP/1.1 server for the PECS protocol, built on TcpListener because
/// HttpListener isn't supported on Android. It has no UI dependencies, so it can
/// later move unchanged into the always-on foreground service (TODO #2).
///
/// Protocol (same as the Windows Listener):
///   GET  /pecs/  -> 200 {"status":"ok","app":"PECSListenerAndroid","machine":...}
///   POST /pecs/  -> body {"tile":...,"time":...}; 200, then PressReceived fires
///   other method -> 405 with "Allow: GET, POST"
///   other path   -> 404
/// </summary>
public sealed class PecsHttpServer : IDisposable
{
    public const int Port = 5050;
    public const string BasePath = "/pecs";

    private const int MaxBodyBytes = 64 * 1024;        // presses are tiny; refuse anything big
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(10);

    private readonly string _machineName;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>A tile press arrived (tile, sender's time, sender's IP). Raised on a background thread.</summary>
    public event Action<string, string, string>? PressReceived;

    /// <summary>A connection test (GET) arrived from the given IP. Raised on a background thread.</summary>
    public event Action<string>? HealthChecked;

    public PecsHttpServer(string machineName) => _machineName = machineName;

    /// <summary>Starts listening on all interfaces. Throws SocketException if the port is taken.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);   // one task per connection
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { /* ignore transient accept errors and keep listening */ }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ClientTimeout);
            var fromIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "?";

            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, timeout.Token);
                if (request == null)
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", null, timeout.Token);
                    return;
                }
                var (method, path, body) = request.Value;

                // Accept /pecs, /pecs/ and /pecs/... (ignore any query string).
                var cleanPath = path.Split('?')[0];
                if (!cleanPath.Equals(BasePath, StringComparison.OrdinalIgnoreCase) &&
                    !cleanPath.StartsWith(BasePath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 404, "Not Found", null, timeout.Token);
                    return;
                }

                switch (method)
                {
                    case "GET":
                        var reply = JsonSerializer.Serialize(new { status = "ok", app = "PECSListenerAndroid", machine = _machineName });
                        await WriteResponseAsync(stream, 200, "OK", reply, timeout.Token);
                        HealthChecked?.Invoke(fromIp);
                        break;

                    case "POST":
                        // Respond first so the Communicator isn't kept waiting, then notify.
                        await WriteResponseAsync(stream, 200, "OK", null, timeout.Token);
                        var (tile, time) = ParsePress(body);
                        PressReceived?.Invoke(tile, time, fromIp);
                        break;

                    default:
                        await WriteResponseAsync(stream, 405, "Method Not Allowed", null, timeout.Token, "Allow: GET, POST");
                        break;
                }
            }
            catch { /* client went away or timed out; nothing to do */ }
        }
    }

    /// <summary>Reads the request line, headers and Content-Length body. Returns null if malformed.</summary>
    private static async Task<(string Method, string Path, string Body)?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        // Read until the blank line that ends the headers.
        var buffer = new List<byte>(1024);
        var chunk = new byte[1024];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(chunk, ct);
            if (n == 0) return null;
            buffer.AddRange(chunk.AsSpan(0, n).ToArray());
            headerEnd = IndexOfHeaderEnd(buffer);
            if (headerEnd < 0 && buffer.Count > MaxBodyBytes) return null;
        }

        var all = buffer.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        int contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[(colon + 1)..].Trim(), out contentLength);
        }
        if (contentLength < 0 || contentLength > MaxBodyBytes) return null;

        // Body: whatever came with the headers, plus the rest from the stream.
        var body = new byte[contentLength];
        int have = Math.Min(all.Length - (headerEnd + 4), contentLength);
        Array.Copy(all, headerEnd + 4, body, 0, have);
        while (have < contentLength)
        {
            int n = await stream.ReadAsync(body.AsMemory(have, contentLength - have), ct);
            if (n == 0) return null;
            have += n;
        }

        return (requestLine[0].ToUpperInvariant(), requestLine[1], Encoding.UTF8.GetString(body));
    }

    private static int IndexOfHeaderEnd(List<byte> b)
    {
        for (int i = 0; i + 3 < b.Count; i++)
            if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int code, string reason, string? json,
                                                 CancellationToken ct, string? extraHeader = null)
    {
        var body = json == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Connection: close\r\n");
        if (json != null) head.Append("Content-Type: application/json; charset=utf-8\r\n");
        if (extraHeader != null) head.Append(extraHeader).Append("\r\n");
        head.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Extracts tile and time; a body that isn't valid press JSON gives "(unparseable)".</summary>
    private static (string Tile, string Time) ParsePress(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var tile = root.GetProperty("tile").GetString() ?? "?";
            var time = root.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "";
            return (tile, time);
        }
        catch
        {
            return ("(unparseable)", "");
        }
    }
}
