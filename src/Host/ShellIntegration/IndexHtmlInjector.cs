using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PrecisionScoresDesktop.Host.ShellIntegration;

// Serves the React UI's index.html with the shell strip's CSS + JS
// splice before </head>. Replaces both the "/" default-document and
// the SPA fallback for client-side routes — static assets like JS
// bundles and images still go through UseStaticFiles untouched.
public static class IndexHtmlInjector
{
    public static WebApplication MapInjectedIndex(this WebApplication app)
    {
        var webRoot = app.Environment.WebRootPath;
        if (string.IsNullOrEmpty(webRoot)) return app;
        var indexPath = Path.Combine(webRoot, "index.html");
        if (!File.Exists(indexPath)) return app;

        // Cache the rendered HTML — the file doesn't change at runtime
        // and the injection is identical every time.
        var rendered = BuildInjected(File.ReadAllText(indexPath));

        app.MapGet("/", () => Results.Content(rendered, "text/html"));
        app.MapGet("/index.html", () => Results.Content(rendered, "text/html"));

        // SPA fallback that was in RoutingSetup moves here so it also
        // picks up the injection.
        app.MapFallback(() => Results.Content(rendered, "text/html"));

        return app;
    }

    private static string BuildInjected(string html)
    {
        var tags = StripAssets.ReadEmbeddedIndexInjection() ?? "";
        var idx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? html : html.Insert(idx, tags);
    }
}
