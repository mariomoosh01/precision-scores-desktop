using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace PrecisionScoresDesktop.Host;

internal static class RoutingSetup
{
    public static IServiceCollection AddRoutingAndEndpoints(this IServiceCollection services)
    {
        services.AddRouting();
        return services;
    }

    public static WebApplication UseRoutingAndEndpoints(this WebApplication app)
    {
        // 1. /healthz comes before static files so a hung static-file
        //    middleware can't block the probe.
        app.MapGet("/healthz", () => Results.Ok(new { ok = true, service = "offline-host" }));

        // 2. Phase D endpoints get mapped here (next commit).

        // 3. Static-file serving for the embedded React build. If
        //    wwwroot doesn't exist (first-time dev checkout without
        //    `build/web.sh` run), we skip the middleware entirely and
        //    expose a placeholder page on /.
        var webRoot = app.Environment.WebRootPath;
        var hasWebBuild = !string.IsNullOrEmpty(webRoot) && Directory.Exists(webRoot)
            && File.Exists(Path.Combine(webRoot, "index.html"));

        if (hasWebBuild)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
            // SPA fallback: anything that isn't an API path AND isn't a
            // real static file falls back to /index.html so client-side
            // routing works for /director/match/dashboard/<guid>, etc.
            app.MapFallback(async context =>
            {
                var fileProvider = app.Environment.WebRootFileProvider;
                var indexInfo = fileProvider.GetFileInfo("index.html");
                if (!indexInfo.Exists)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                context.Response.ContentType = "text/html";
                await using var stream = indexInfo.CreateReadStream();
                await stream.CopyToAsync(context.Response.Body);
            });
        }
        else
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("RoutingSetup");
            logger.LogWarning(
                "No React build at {WebRoot} — serving placeholder on /. " +
                "Run build/web.sh (or build/web.ps1) to populate it.", webRoot);
            app.MapGet("/", () => Results.Content(PlaceholderHtml, "text/html"));
        }

        return app;
    }

    private const string PlaceholderHtml = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Precision Scores Desktop</title>
        <style>body{font-family:system-ui;background:#0b1220;color:#e6e8ef;
          display:flex;align-items:center;justify-content:center;height:100vh;margin:0;padding:48px;box-sizing:border-box}
          .card{max-width:560px}h1{font-size:28px;margin:0 0 12px}p{color:#9aa3b2;line-height:1.5}
          code{background:#1a2237;padding:2px 6px;border-radius:4px}</style></head>
        <body><div class="card">
          <h1>Precision Scores Desktop</h1>
          <p>The React UI hasn't been built into this install yet.</p>
          <p>Run <code>build/web.sh</code> (or <code>build/web.ps1</code>) at the repo root to clone + build the pinned frontend commit, then relaunch.</p>
        </div></body></html>
        """;
}
