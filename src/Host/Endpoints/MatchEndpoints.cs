using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrecisionScoresDesktop.Host.Data;
using PrecisionScoresDesktop.Host.Data.Entities;

namespace PrecisionScoresDesktop.Host.Endpoints;

// The subset of match endpoints the React UI hits during match-day
// scoring. Each endpoint matches the cloud contract 1-for-1 so the
// frontend doesn't know (or care) which backend it's talking to.
//
// Convention: GET endpoints that return cached data pass through
// DataJson verbatim via Results.Content — we never re-parse-and-serialise
// because the shape evolves cloud-side and we don't track it.
public static class MatchEndpoints
{
    public static IEndpointRouteBuilder MapMatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/match/GetAllMatches", ListAllMatches);
        app.MapGet("/match/GetMatch", GetMatch);
        app.MapGet("/match/GetRegisteredShooters", GetRegisteredShooters);
        app.MapPost("/match/SaveScoreCard", SaveScoreCard);
        app.MapPut("/match/EditScoreCard", EditScoreCard);
        app.MapPost("/match/scan-multi", ScanMulti);
        return app;
    }

    // Returns the array of MatchModel JSON blobs for every downloaded
    // match. Order newest-first (match date desc) so the UI's default
    // list matches the online experience.
    private static async Task<IResult> ListAllMatches(OfflineDbContext db, CancellationToken ct)
    {
        var jsonBlobs = await db.CachedMatches
            .OrderByDescending(m => m.MatchDate)
            .Select(m => m.DataJson)
            .ToListAsync(ct);

        // Each DataJson is one serialised match. Concatenate into a JSON
        // array by writing them raw inside brackets.
        if (jsonBlobs.Count == 0)
        {
            return Results.Content("[]", "application/json");
        }
        var combined = "[" + string.Join(",", jsonBlobs) + "]";
        return Results.Content(combined, "application/json");
    }

    private static async Task<IResult> GetMatch(
        [AsParameters] GetMatchQuery q,
        OfflineDbContext db,
        CancellationToken ct)
    {
        if (q.Id == Guid.Empty) return Results.BadRequest(new { error = "id required" });
        var json = await db.CachedMatches
            .Where(m => m.MatchId == q.Id)
            .Select(m => m.DataJson)
            .FirstOrDefaultAsync(ct);
        return json is null
            ? Results.NotFound(new { error = "match not downloaded for offline use" })
            : Results.Content(json, "application/json");
    }

    private static async Task<IResult> GetRegisteredShooters(
        [AsParameters] GetShootersQuery q,
        OfflineDbContext db,
        CancellationToken ct)
    {
        if (q.MatchId == Guid.Empty) return Results.BadRequest(new { error = "matchId required" });
        var jsonBlobs = await db.CachedShooters
            .Where(s => s.MatchId == q.MatchId)
            .Select(s => s.DataJson)
            .ToListAsync(ct);
        var combined = jsonBlobs.Count == 0 ? "[]" : "[" + string.Join(",", jsonBlobs) + "]";
        return Results.Content(combined, "application/json");
    }

    // Insert a new scored card. The frontend doesn't send WrittenTotal /
    // BankSubtotals in the save payload — those are UI-only for mismatch
    // display — so we don't persist them here either.
    private static async Task<IResult> SaveScoreCard(
        ScoreCardPost dto,
        OfflineDbContext db,
        CancellationToken ct)
    {
        if (dto.EventId == Guid.Empty || dto.ShooterId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "eventId and shooterId required" });
        }

        // Natural key collision → tell the frontend to use the PUT path,
        // mirroring the cloud's 409 behaviour from MatchesEndpoints.cs:234.
        var existing = await db.OfflineScorecards.FirstOrDefaultAsync(
            s => s.EventId == dto.EventId && s.ShooterId == dto.ShooterId
                 && s.TargetId == dto.TargetId && s.CardNumber == dto.CardNumber,
            ct);
        if (existing is not null)
        {
            return Results.Conflict(new { error = "card already exists; use PUT /match/EditScoreCard" });
        }

        // Resolve MatchId from the cached match collection (the frontend
        // doesn't send it, but we need it for upload sync to know which
        // match this card belongs to).
        var matchId = await ResolveMatchIdAsync(db, dto.EventId, ct);
        if (matchId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "no cached match contains this eventId" });
        }

        var row = new OfflineScorecard
        {
            Id = Guid.NewGuid(),
            MatchId = matchId,
            EventId = dto.EventId,
            ShooterId = dto.ShooterId,
            TargetId = dto.TargetId,
            CardNumber = dto.CardNumber,
            TotalScore = dto.TotalScore,
            CardString = dto.CardString ?? "",
            CreatedAt = DateTime.UtcNow,
            SyncedAt = null,
        };
        db.OfflineScorecards.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = row.Id });
    }

    private static async Task<IResult> EditScoreCard(
        ScoreCardPost dto,
        OfflineDbContext db,
        CancellationToken ct)
    {
        var row = await db.OfflineScorecards.FirstOrDefaultAsync(
            s => s.EventId == dto.EventId && s.ShooterId == dto.ShooterId
                 && s.TargetId == dto.TargetId && s.CardNumber == dto.CardNumber,
            ct);

        if (row is null)
        {
            // Upsert — defer to the SaveScoreCard path.
            return await SaveScoreCard(dto, db, ct);
        }

        row.TotalScore = dto.TotalScore;
        row.CardString = dto.CardString ?? row.CardString;
        row.SyncedAt = null;  // edited row needs to re-sync
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = row.Id });
    }

    // Proxies the browser's multipart upload through to the bundled
    // scanner sidecar (ScannerProxy owns the HttpClient + host details
    // registered by DI). On any failure returns 503 so the browser's
    // opencv.js fallback runs.
    private static async Task<IResult> ScanMulti(
        HttpContext http,
        ScannerProxy proxy,
        CancellationToken ct)
    {
        if (!proxy.IsAvailable)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            return await proxy.ForwardAsync(http.Request, ct);
        }
        catch
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<Guid> ResolveMatchIdAsync(
        OfflineDbContext db, Guid eventId, CancellationToken ct)
    {
        // The MatchModel JSON contains matchDisciplines with eventId.
        // Rather than JSON-parse every cached match on every save, scan
        // the small set and look for the eventId substring in DataJson.
        // Collisions on 36-char GUID substrings are vanishingly rare.
        var needle = eventId.ToString();
        var matches = await db.CachedMatches
            .Where(m => m.DataJson.Contains(needle))
            .Select(m => m.MatchId)
            .ToListAsync(ct);
        return matches.Count == 1 ? matches[0] : Guid.Empty;
    }

    // Request DTOs — isolated here so they don't pollute Entities/.
    public sealed record GetMatchQuery(Guid Id);
    public sealed record GetShootersQuery(Guid MatchId);

    public sealed record ScoreCardPost(
        Guid EventId,
        Guid ShooterId,
        short TargetId,
        short CardNumber,
        short TotalScore,
        string? CardString);
}
