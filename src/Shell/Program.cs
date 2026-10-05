using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Photino.NET;
using PrecisionScoresDesktop.Host;
using Serilog;
using Serilog.Extensions.Logging;

namespace PrecisionScoresDesktop.Shell;

// Entry point for the desktop app. In Phase A this just proves the
// Photino window opens on both macOS and Windows. Later phases layer in
// the Kestrel host, scanner sidecar, connectivity monitor, and status
// strip above the WebView.
internal static class Program
{
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
