using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PrecisionScoresDesktop.Host.ShellIntegration;

// Serves the Photino shell's own chrome assets (strip.css + strip.js)
// from embedded resources, under /_shell/*. These are injected into
// index.html by IndexHtmlInjector so they load before the React app
// hydrates — the strip appears instantly, no flash of unstyled chrome.
public static class StripAssets
{
    public static IEndpointRouteBuilder MapShellStripAssets(this IEndpointRouteBuilder app)
    {
        app.MapGet("/_shell/strip.css", async (HttpContext ctx) =>
        {
            await WriteEmbeddedAsync(ctx, "strip.css", "text/css");
        });
        app.MapGet("/_shell/strip.js", async (HttpContext ctx) =>
        {
            await WriteEmbeddedAsync(ctx, "strip.js", "application/javascript");
        });
        return app;
    }

    public static string ReadEmbeddedIndexInjection()
    {
        // Produces the exact HTML fragment IndexHtmlInjector splices
        // into the real index.html, just before </head>.
        //
        // Script is INLINED (not <script src=/_shell/strip.js>) so the
        // WebView doesn't have to wait on an extra round-trip and we
        // sidestep script-ordering edge cases with the React module
        // script that's already in <head>.
        var js = ReadEmbedded("strip.js") ?? "/* strip.js missing */";
        return """
            <link rel="stylesheet" href="/_shell/strip.css" />
            <script>
            """
            + js + """
            </script>
            """;
    }

    private static string? ReadEmbedded(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = $"PrecisionScoresDesktop.Host.ShellIntegration.Assets.{name}";
        using var stream = asm.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task WriteEmbeddedAsync(HttpContext ctx, string name, string contentType)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = $"PrecisionScoresDesktop.Host.ShellIntegration.Assets.{name}";
        await using var stream = asm.GetManifestResourceStream(resource);
        if (stream is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync($"missing embedded resource: {resource}");
            return;
        }
        ctx.Response.ContentType = contentType;
        // Cache for a day inside the running app; the file is baked into
        // the DLL so revisions ship with new builds.
        ctx.Response.Headers.CacheControl = "public, max-age=86400";
        await stream.CopyToAsync(ctx.Response.Body);
    }
}
