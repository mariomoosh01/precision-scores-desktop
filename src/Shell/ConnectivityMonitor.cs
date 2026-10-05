using System.Net.Http;
using System.Net.NetworkInformation;
using Serilog;

namespace PrecisionScoresDesktop.Shell;

// Watches OS-level connectivity and reconciles it with an actual TCP
// probe against the cloud API. The OS flag alone lies: ISP captive
// portals, bad DNS, or Wi-Fi connected but no route can all report
// "online" when the cloud is unreachable. The reconciler runs a cheap
// HEAD / GET against the cloud's /healthz every 15s whenever the OS
// says we're online.
//
// Reports changes via OnChanged(bool online). The last reported value
// is cached in Online so callers can push the initial state to the
// strip at startup without racing the first event.
internal sealed class ConnectivityMonitor : IAsyncDisposable
{
    private readonly string _cloudHealthUrl;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _probeInterval = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(5);
    private Task? _probeLoop;

    public ConnectivityMonitor(string cloudHealthUrl)
    {
        _cloudHealthUrl = cloudHealthUrl;
        _http = new HttpClient { Timeout = _probeTimeout };
    }

    public bool Online { get; private set; }

    public event Action<bool>? OnChanged;

    public void Start()
    {
        NetworkChange.NetworkAvailabilityChanged += OnOsEvent;
        _probeLoop = Task.Run(ProbeLoopAsync);
        // Seed state immediately so the UI reflects reality before the
        // first probe tick.
        _ = Task.Run(() => ReconcileAsync(CancellationToken.None));
    }

    private void OnOsEvent(object? sender, NetworkAvailabilityEventArgs e)
    {
        Log.Debug("OS reported network availability={Avail}; probing cloud", e.IsAvailable);
        _ = Task.Run(() => ReconcileAsync(_cts.Token));
    }

    private async Task ProbeLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(_probeInterval, _cts.Token); }
            catch (OperationCanceledException) { break; }
            await ReconcileAsync(_cts.Token);
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        bool online;
        try
        {
            // We don't check NetworkInterface.GetIsNetworkAvailable()
            // because it's noisy on macOS (returns true for VPN-only
            // adapters) — the TCP probe is the source of truth.
            using var resp = await _http.GetAsync(_cloudHealthUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            online = resp.IsSuccessStatusCode;
        }
        catch
        {
            online = false;
        }

        if (online != Online)
        {
            Online = online;
            Log.Information("Cloud reachability changed: {State}", online ? "online" : "offline");
            try { OnChanged?.Invoke(online); }
            catch (Exception ex) { Log.Warning(ex, "OnChanged handler threw"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnOsEvent;
        _cts.Cancel();
        if (_probeLoop is not null)
        {
            try { await _probeLoop; }
            catch { /* already cancelling */ }
        }
        _http.Dispose();
        _cts.Dispose();
    }
}
