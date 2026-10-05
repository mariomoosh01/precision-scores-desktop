using System.Text.Json;
using System.Text.Json.Serialization;
using Photino.NET;
using Serilog;

namespace PrecisionScoresDesktop.Shell;

// Thin wrapper around Photino's web-message channel for the shell
// strip. Owns the JSON envelope + routes inbound messages to typed
// handlers so Program.cs doesn't deal with string parsing.
internal sealed class StripChannel
{
    private readonly PhotinoWindow _window;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    // Photino rejects SendWebMessage before the window is initialized.
    // strip.js signals readiness by sending {kind:"ready"} once the DOM
    // is wired. Any Send* before then is queued and flushed on ready.
    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private bool _ready;

    public StripChannel(PhotinoWindow window)
    {
        _window = window;
        _window.RegisterWebMessageReceivedHandler((_, raw) => OnInbound(raw));
    }

    // Fires when the strip wants native to do something (download /
    // upload). Null handlers = silently dropped.
    public Action? OnDownloadRequested { get; set; }
    public Action? OnUploadRequested { get; set; }
    public Action<Guid>? OnMatchChosen { get; set; }

    public void SendState(bool online, bool syncing, int pending, DateTime? lastSyncAt)
    {
        var state = new
        {
            kind = "state",
            online,
            syncing,
            pending,
            lastSyncAt = lastSyncAt?.ToUniversalTime().ToString("O"),
        };
        Dispatch(JsonSerializer.Serialize(state, _json));
    }

    public void SendMatches(IEnumerable<MatchSummary> list)
    {
        Dispatch(JsonSerializer.Serialize(new { kind = "matches", list }, _json));
    }

    public void SendToast(string level, string text)
    {
        Dispatch(JsonSerializer.Serialize(new { kind = "toast", level, text }, _json));
    }

    private void Dispatch(string payload)
    {
        lock (_gate)
        {
            if (!_ready)
            {
                _pending.Enqueue(payload);
                return;
            }
        }
        try { _window.SendWebMessage(payload); }
        catch (Exception ex) { Log.Warning(ex, "SendWebMessage failed: {Payload}", payload); }
    }

    private void MarkReady()
    {
        List<string> flush;
        lock (_gate)
        {
            if (_ready) return;
            _ready = true;
            flush = _pending.ToList();
            _pending.Clear();
        }
        Log.Debug("Strip ready; flushing {Count} queued messages", flush.Count);
        foreach (var p in flush)
        {
            try { _window.SendWebMessage(p); }
            catch (Exception ex) { Log.Warning(ex, "SendWebMessage flush failed"); }
        }
    }

    private void OnInbound(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var kind = doc.RootElement.GetProperty("kind").GetString();
            switch (kind)
            {
                case "ready":
                    MarkReady();
                    break;
                case "downloadMatchListRequested":
                    OnDownloadRequested?.Invoke();
                    break;
                case "downloadMatchRequested":
                    if (doc.RootElement.TryGetProperty("matchId", out var mid)
                        && Guid.TryParse(mid.GetString(), out var guid))
                    {
                        OnMatchChosen?.Invoke(guid);
                    }
                    break;
                case "uploadRequested":
                    OnUploadRequested?.Invoke();
                    break;
                default:
                    Log.Debug("Strip sent unknown message: {Raw}", raw);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse strip message: {Raw}", raw);
        }
    }

    public sealed record MatchSummary(Guid Id, string Name, DateTime Date);
}
