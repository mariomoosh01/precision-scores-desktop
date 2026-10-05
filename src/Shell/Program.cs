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
            host = OfflineHost.StartAsync(new OfflineHost.Options(
                DatabasePath: AppDataPaths.DatabaseFile(),
                LoggerProvider: new SerilogLoggerProvider(Log.Logger, dispose: false))
            ).GetAwaiter().GetResult();
            Log.Information("Local host listening on {Url}; db at {Db}", OfflineHost.BaseUrl, AppDataPaths.DatabaseFile());

            var window = new PhotinoWindow()
                .SetTitle("Precision Scores")
                .SetUseOsDefaultSize(false)
                .SetSize(1280, 800)
                .Center()
                .SetResizable(true)
                // Phase B placeholder — the embedded React build gets wired in Phase C.
                .LoadRawString("""
                    <!doctype html>
                    <html>
                      <head>
                        <meta charset="utf-8" />
                        <title>Precision Scores Desktop</title>
                        <style>
                          body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
                                 background: #0b1220; color: #e6e8ef; margin: 0; padding: 48px;
                                 display: flex; align-items: center; justify-content: center;
                                 height: 100vh; box-sizing: border-box; }
                          .card { max-width: 560px; }
                          h1 { font-size: 28px; margin: 0 0 12px; }
                          p { color: #9aa3b2; line-height: 1.5; }
                          code { background: #1a2237; padding: 2px 6px; border-radius: 4px; }
                        </style>
                      </head>
                      <body>
                        <div class="card">
                          <h1>Precision Scores Desktop</h1>
                          <p>Phase A scaffold. The React UI, local Kestrel, and scanner sidecar
                             will be wired in by Phase C.</p>
                          <p>Logs: <code>%LOCALAPPDATA%\PrecisionScoresDesktop\logs\</code>
                             (Windows) or <code>~/Library/Application Support/PrecisionScoresDesktop/logs/</code>
                             (macOS).</p>
                        </div>
                      </body>
                    </html>
                    """);

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
