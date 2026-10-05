using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photino.NET;
using PrecisionScoresDesktop.Host;
using PrecisionScoresDesktop.Host.Data;
using PrecisionScoresDesktop.Sync;
using Serilog;
using Serilog.Extensions.Logging;

namespace PrecisionScoresDesktop.Shell;

// Entry point for the desktop app. In Phase A this just proves the
// Photino window opens on both macOS and Windows. Later phases layer in
// the Kestrel host, scanner sidecar, connectivity monitor, and status
// strip above the WebView.
internal static class Program
{
    // Cloud API roots. Both overridable via env var for staging builds.
    private static string CloudBaseUrl =>
        Environment.GetEnvironmentVariable("PS_CLOUD_BASE_URL")
        ?? "https://mariomoosh-004-site1.anytempurl.com";
    private static string CloudHealthUrl => CloudBaseUrl.TrimEnd('/') + "/healthz";

    [STAThread]
    private static int Main(string[] args)
    {
        var logDir = AppDataPaths.LogsDirectory();
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(logDir, "shell-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        WebApplication? host = null;
        ConnectivityMonitor? connectivity = null;
        try
        {
            Log.Information("Precision Scores Desktop starting. Data dir: {Dir}", AppDataPaths.Root());

            // Start Kestrel on 127.0.0.1:34567 BEFORE opening the window
            // so the React UI's first XHR against the base URL doesn't
            // race the server coming up.
            Directory.CreateDirectory(AppDataPaths.Root());
            var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            host = OfflineHost.StartAsync(new OfflineHost.Options(
                DatabasePath: AppDataPaths.DatabaseFile(),
                WebRootPath: webRoot,
                LoggerProvider: new SerilogLoggerProvider(Log.Logger, dispose: false))
            ).GetAwaiter().GetResult();
            Log.Information("Local host listening on {Url}; db at {Db}; wwwroot {WebRoot} (exists={Exists})",
                OfflineHost.BaseUrl, AppDataPaths.DatabaseFile(), webRoot, Directory.Exists(webRoot));

            // The Photino window loads the React UI from the local
            // Kestrel. Static files (if built), API endpoints, SPA
            // fallback — all served from one origin, no CORS.
            var window = new PhotinoWindow()
                .SetTitle("Precision Scores")
                .SetUseOsDefaultSize(false)
                .SetSize(1280, 800)
                .Center()
                .SetResizable(true)
                .Load(new Uri(OfflineHost.BaseUrl + "/"));

            var strip = new StripChannel(window);
            var hostCapture = host!;  // local for closures

            async Task<(bool Online, int Pending, DateTime? LastSync)> SnapshotStateAsync()
            {
                await using var scope = hostCapture.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<OfflineDbContext>();
                var pending = await db.OfflineScorecards.CountAsync(s => s.SyncedAt == null);
                var lastSync = await db.OfflineScorecards
                    .Where(s => s.SyncedAt != null)
                    .OrderByDescending(s => s.SyncedAt)
                    .Select(s => s.SyncedAt)
                    .FirstOrDefaultAsync();
                return (connectivity?.Online ?? false, pending, lastSync);
            }

            async Task PushStateAsync(bool syncing = false)
            {
                var (online, pending, lastSync) = await SnapshotStateAsync();
                strip.SendState(online, syncing, pending, lastSync);
            }

            strip.OnDownloadRequested = async () =>
            {
                try
                {
                    await using var scope = hostCapture.Services.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<OfflineDbContext>();
                    var profile = await db.CachedProfiles.FirstOrDefaultAsync();
                    if (profile is null || string.IsNullOrWhiteSpace(profile.JwtEncrypted))
                    {
                        strip.SendToast("error",
                            "No cached session. Seed CachedProfiles with a JWT first (login flow lands later).");
                        return;
                    }
                    using var cloud = new CloudClient(CloudBaseUrl, profile.JwtEncrypted);
                    var job = new DownloadMatchJob(cloud, db);
                    var matches = await job.ListAvailableMatchesAsync(CancellationToken.None);
                    strip.SendMatches(matches.Select(m =>
                        new StripChannel.MatchSummary(m.Id, m.Name, m.Date)));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "ListAvailableMatchesAsync failed");
                    strip.SendToast("error", "Could not fetch matches: " + ex.Message);
                }
            };

            strip.OnMatchChosen = async matchId =>
            {
                await PushStateAsync(syncing: true);
                try
                {
                    await using var scope = hostCapture.Services.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<OfflineDbContext>();
                    var profile = await db.CachedProfiles.FirstOrDefaultAsync();
                    if (profile is null) return;
                    using var cloud = new CloudClient(CloudBaseUrl, profile.JwtEncrypted);
                    var job = new DownloadMatchJob(cloud, db);
                    await job.DownloadAsync(matchId, CancellationToken.None);
                    strip.SendToast("info", "Match downloaded for offline use.");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "DownloadAsync failed for {MatchId}", matchId);
                    strip.SendToast("error", "Download failed: " + ex.Message);
                }
                finally
                {
                    await PushStateAsync(syncing: false);
                }
            };

            async Task RunUploadAsync(string trigger)
            {
                await PushStateAsync(syncing: true);
                try
                {
                    await using var scope = hostCapture.Services.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<OfflineDbContext>();
                    var profile = await db.CachedProfiles.FirstOrDefaultAsync();
                    if (profile is null || string.IsNullOrWhiteSpace(profile.JwtEncrypted))
                    {
                        strip.SendToast("error", "No cached session — can't upload.");
                        return;
                    }
                    using var cloud = new CloudClient(CloudBaseUrl, profile.JwtEncrypted);
                    var job = new UploadScorecardsJob(cloud, db);
                    Log.Information("Upload starting ({Trigger})", trigger);
                    var report = await job.RunAsync();
                    Log.Information("Upload done: {Uploaded}/{Total} ok, {Failed} failed ({Trigger})",
                        report.Uploaded, report.Total, report.Failed, trigger);
                    if (report.Total == 0)
                    {
                        strip.SendToast("info", "Nothing to upload.");
                    }
                    else if (report.Failed == 0)
                    {
                        strip.SendToast("info", $"Uploaded {report.Uploaded} card{(report.Uploaded == 1 ? "" : "s")}.");
                    }
                    else
                    {
                        strip.SendToast("error",
                            $"Uploaded {report.Uploaded} of {report.Total}; {report.Failed} still pending.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Upload failed ({Trigger})", trigger);
                    strip.SendToast("error", "Upload failed: " + ex.Message);
                }
                finally
                {
                    await PushStateAsync(syncing: false);
                }
            }

            strip.OnUploadRequested = () => _ = RunUploadAsync("manual");

            connectivity = new ConnectivityMonitor(CloudHealthUrl);
            connectivity.OnChanged += online =>
            {
                _ = PushStateAsync();
                // Auto-trigger upload when connectivity returns AND we
                // have pending rows. Fire-and-forget; RunUploadAsync
                // handles its own errors + state updates.
                if (!online) return;
                _ = Task.Run(async () =>
                {
                    var (_, pending, _) = await SnapshotStateAsync();
                    if (pending > 0) await RunUploadAsync("auto-reconnect");
                });
            };
            connectivity.Start();
            // Seed the strip immediately — don't wait for the first probe.
            _ = PushStateAsync();

            window.WaitForClose();
            Log.Information("Window closed; shutting down.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Shell terminated unexpectedly.");
            return 1;
        }
        finally
        {
            if (connectivity is not null)
            {
                connectivity.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            if (host is not null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    host.StopAsync(cts.Token).GetAwaiter().GetResult();
                }
                catch (Exception ex) { Log.Warning(ex, "Error stopping local host"); }
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Log.CloseAndFlush();
        }
    }
}
