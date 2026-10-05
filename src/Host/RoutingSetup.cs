using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace PrecisionScoresDesktop.Host;

// Phase B stub: wires routing but no routes yet beyond /healthz. The
// 8 offline endpoints land here in Phase D. Keeps OfflineHost.cs free
// of route-definition noise so route additions stay localised.
internal static class RoutingSetup
{
    public static IServiceCollection AddRoutingAndEndpoints(this IServiceCollection services)
    {
        services.AddRouting();
        return services;
    }

    public static WebApplication UseRoutingAndEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { ok = true, service = "offline-host" }));
        return app;
    }
}
