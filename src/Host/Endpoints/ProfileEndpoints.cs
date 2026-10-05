using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PrecisionScoresDesktop.Host.Data;

namespace PrecisionScoresDesktop.Host.Endpoints;

// Minimum auth surface for offline mode:
// - /profiles/authenticate: offline-only check against the last-logged
//   cloud session. We don't verify a password here — the only user with
//   access to this laptop is the one who ran it, and the cached JWT
//   lets them read their own cloud profile. Real password verification
//   happens online during initial sign-in / sync; offline just hands
//   back the cached session.
// - /profiles/me: returns the cached profile JSON blob the frontend
//   stores to decide if the user is a match director.
public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/profiles/authenticate", Authenticate);
        app.MapGet("/profiles/me", GetMe);
        return app;
    }

    // Default profile returned when no CachedProfiles row exists. Keeps
    // the React app's auth guard happy without a real cloud login — the
    // desktop app treats the local machine as single-user and trusted.
    // Cloud auth is a *separate* concern owned by the Photino shell (for
    // the sync agent's /match/GetAllMatches + SaveScoreCard calls).
    //
    // Fields match what the frontend's JWTAuth reader splats onto the
    // user context: profileId, email, firstName, lastName, isMatchDirector.
    // ProfileId is deterministic (zero-guid) so repeat launches look
    // like the same user to any UI code that keys on it.
    private const string DefaultOfflineProfileJson =
        "{\"profileId\":\"00000000-0000-0000-0000-000000000000\"," +
        "\"email\":\"offline@local\"," +
        "\"firstName\":\"Offline\"," +
        "\"lastName\":\"User\"," +
        "\"isMatchDirector\":true}";

    private const string DefaultOfflineJwt = "offline-no-cloud-jwt";

    private static async Task<IResult> Authenticate(
        AuthenticateRequest req,
        OfflineDbContext db,
        CancellationToken ct)
    {
        // If a real cloud session is cached (user signed in via the
        // shell), prefer that. Otherwise accept the login attempt
        // unconditionally and hand back the default offline profile —
        // the local machine is single-user, so password verification
        // isn't meaningful here.
        var profile = string.IsNullOrWhiteSpace(req.Email)
            ? await db.CachedProfiles.FirstOrDefaultAsync(ct)
            : await db.CachedProfiles.FirstOrDefaultAsync(p => p.Email == req.Email, ct);

        var (jwt, profileJson) = profile is null
            ? (DefaultOfflineJwt, DefaultOfflineProfileJson)
            : (profile.JwtEncrypted, profile.DataJson);

        return Results.Content(BuildAuthResponse(jwt, profileJson), "application/json");
    }

    private static async Task<IResult> GetMe(OfflineDbContext db, CancellationToken ct)
    {
        var profile = await db.CachedProfiles.FirstOrDefaultAsync(ct);
        return Results.Content(
            profile?.DataJson ?? DefaultOfflineProfileJson,
            "application/json");
    }

    private static string BuildAuthResponse(string jwt, string profileJson)
    {
        // Splice the token into the cached profile JSON. The frontend's
        // login handler reads { token, email, firstName, ... } — adding
        // "token" at the start of the object is a cheap merge.
        var trimmed = profileJson.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            // Pathological cache — surface as object with just the token.
            return $"{{\"token\":\"{System.Text.Json.JsonEncodedText.Encode(jwt)}\"}}";
        }
        // Insert token as the first property; the object already starts with {.
        return "{\"token\":\""
            + System.Text.Json.JsonEncodedText.Encode(jwt)
            + "\","
            + trimmed[1..];
    }

    public sealed record AuthenticateRequest(string Email, string? Password);
}
