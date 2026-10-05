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

    private static async Task<IResult> Authenticate(
        AuthenticateRequest req,
        OfflineDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
        {
            return Results.BadRequest(new { error = "email required" });
        }

        var profile = await db.CachedProfiles
            .FirstOrDefaultAsync(p => p.Email == req.Email, ct);

        if (profile is null)
        {
            // No cached session for this email — the user needs to log
            // in online at least once before using the desktop app.
            return Results.Unauthorized();
        }

        // Return the shape the frontend's JWTAuth expects: a JWT token
        // + the profile fields. The profile JSON we cached includes
        // these fields already — the frontend splats them.
        return Results.Content(
            BuildAuthResponse(profile.JwtEncrypted, profile.DataJson),
            "application/json");
    }

    private static async Task<IResult> GetMe(OfflineDbContext db, CancellationToken ct)
    {
        var profile = await db.CachedProfiles.FirstOrDefaultAsync(ct);
        return profile is null
            ? Results.Unauthorized()
            : Results.Content(profile.DataJson, "application/json");
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
