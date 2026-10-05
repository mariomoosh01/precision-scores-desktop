using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrecisionScoresDesktop.Host.Data;

namespace PrecisionScoresDesktop.Host;

// Builds + starts the in-process Kestrel server that backs the
// React UI when offline. Returns the WebApplication so the caller
// (the Shell) can await .StopAsync() on shutdown. All DI happens here
// — the Shell knows only about this entry point.
public static class OfflineHost
{
    // Fixed port — matches the VITE_API_BASE_URL baked into the offline
    // React build at Phase C build time. If the port is in use, the app
    // refuses to start rather than silently binding something else.
    public const int Port = 34567;
    public const string BaseUrl = "http://127.0.0.1:34567";

    public sealed record Options(
        string DatabasePath,
        string? WebRootPath = null,
        ILoggerProvider? LoggerProvider = null);

    public static async Task<WebApplication> StartAsync(Options options, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            // The slim builder skips configuration/env loading we don't
            // need in a desktop context.
            ApplicationName = "PrecisionScoresDesktop.Host",
            WebRootPath = options.WebRootPath,
        });

        builder.WebHost.ConfigureKestrel(opt =>
        {
            opt.ListenLocalhost(Port);
        });

        if (options.LoggerProvider is not null)
        {
            builder.Logging.AddProvider(options.LoggerProvider);
        }

        builder.Services.AddDbContext<OfflineDbContext>(opt =>
            opt.UseSqlite($"Data Source={options.DatabasePath}"));

        // Routes get added in Phase D — Startup extension method.
        builder.Services.AddRoutingAndEndpoints();

        var app = builder.Build();

        // Auto-create the SQLite schema on first run. We don't ship
        // migrations because the schema is tiny and self-contained.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfflineDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
        }

        app.UseRoutingAndEndpoints();

        await app.StartAsync(ct);
        return app;
    }
}
