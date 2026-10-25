using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PECSCommunicator;

/// <summary>Outcome of talking to PECSListener: success flag plus a plain-language reason.</summary>
public sealed record ListenerResult(bool Ok, string Message);

/// <summary>
/// All HTTP traffic to PECSListener in one place:
/// POST a tile press (with one retry) and GET a health check.
/// Every method takes the URL explicitly so callers always use the current setting.
/// </summary>
public static class ListenerClient
{
    // Short timeout so a missing Listener never leaves a request hanging for long.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    // Pause before the single retry of a failed press.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Sends a tile press. If it fails (no connection or a non-2xx reply) it waits ~1 s and tries once more.
    /// Never throws; the result reflects the final attempt.
    /// </summary>
    public static async Task<ListenerResult> SendPressAsync(string url, string tile)
    {
        // Same timestamp on both attempts: it is the time Ian pressed, not the time it arrived.
        var json = JsonSerializer.Serialize(new { tile, time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });

        var result = await PostOnceAsync(url, json);
        if (result.Ok) return result;

        await Task.Delay(RetryDelay);
        var retry = await PostOnceAsync(url, json);
        return retry.Ok ? retry with { Message = retry.Message + " (after retry)" } : retry;
    }

    private static async Task<ListenerResult> PostOnceAsync(string url, string json)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content);
            return response.IsSuccessStatusCode
                ? new ListenerResult(true, "Press delivered.")
                : new ListenerResult(false, $"Listener replied {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception ex)
        {
            return new ListenerResult(false, Describe(ex));
        }
    }

    /// <summary>
    /// GETs the URL and confirms the reply really is PECSListener ({"status":"ok","app":"PECSListener",...}).
    /// Never throws.
    /// </summary>
    public static async Task<ListenerResult> CheckAsync(string url)
    {
        try
        {
            using var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return new ListenerResult(false, "Wrong response: that isn't PECSListener.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            bool isListener = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("status", out var s) && s.GetString() == "ok"
                && root.TryGetProperty("app", out var a) && a.GetString() == "PECSListener";
            if (!isListener)
                return new ListenerResult(false, "Wrong response: that isn't PECSListener.");

            var machine = root.TryGetProperty("machine", out var m) ? m.GetString() ?? "?" : "?";
            return new ListenerResult(true, $"Connected to PECSListener on {machine}");
        }
        catch (JsonException)
        {
            return new ListenerResult(false, "Wrong response: that isn't PECSListener.");
        }
        catch (Exception ex)
        {
            return new ListenerResult(false, Describe(ex));
        }
    }

    /// <summary>Turns a network exception into a short, plain reason.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "Timed out: no answer within 3 seconds.",
        HttpRequestException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } }
            => "Refused: Listener not running on that port.",
        HttpRequestException { InnerException: SocketException se } => $"Could not connect ({se.SocketErrorCode}).",
        _ => "Failed: " + ex.Message
    };
}
