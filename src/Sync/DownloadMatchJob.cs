using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrecisionScoresDesktop.Host.Data;
using PrecisionScoresDesktop.Host.Data.Entities;

namespace PrecisionScoresDesktop.Sync;

// Pre-match sync. Pulls a match + its registered shooters from the
// cloud and writes JSON blobs into the local SQLite so the offline
// Kestrel can serve them to the React UI without any further network.
public sealed class DownloadMatchJob
{
    private readonly CloudClient _cloud;
    private readonly OfflineDbContext _db;

    public DownloadMatchJob(CloudClient cloud, OfflineDbContext db)
    {
        _cloud = cloud;
        _db = db;
    }

    // Returns a list of (matchId, name, matchDate) summaries for the UI
    // to present as a picker. Full MatchModel blobs are too heavy for
    // the strip's modal; the picker only needs these three fields.
    public async Task<List<MatchSummary>> ListAvailableMatchesAsync(CancellationToken ct)
    {
        var raw = await _cloud.GetMatchesRawAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        var out_ = new List<MatchSummary>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return out_;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            Guid id = default;
            string name = "";
            DateTime date = default;
            if (item.TryGetProperty("matchId", out var idEl) && idEl.TryGetGuid(out var g)) id = g;
            if (item.TryGetProperty("matchName", out var n)) name = n.GetString() ?? "";
            if (item.TryGetProperty("matchDate", out var d) && d.TryGetDateTime(out var dt)) date = dt;
            if (id != Guid.Empty) out_.Add(new MatchSummary(id, name, date));
        }
        // Newest first so the picker shows the most-likely-to-be-scored
        // match at the top.
        out_.Sort((a, b) => b.Date.CompareTo(a.Date));
        return out_;
    }

    // Downloads a specific match and its shooter roster, writes both to
    // SQLite as JSON-blob rows the local Kestrel can hand back verbatim.
    public async Task DownloadAsync(Guid matchId, CancellationToken ct)
    {
        var matchJson = await _cloud.GetMatchRawAsync(matchId, ct);
        var shootersJson = await _cloud.GetRegisteredShootersRawAsync(matchId, ct);

        // Parse the match JSON only enough to extract name + date for the
        // table's columns; the full blob stays untouched for passthrough.
        string name = "";
        DateTime date = default;
        using (var doc = JsonDocument.Parse(matchJson))
        {
            if (doc.RootElement.TryGetProperty("matchName", out var n)) name = n.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("matchDate", out var d) && d.TryGetDateTime(out var dt)) date = dt;
        }

        var existing = await _db.CachedMatches.FirstOrDefaultAsync(m => m.MatchId == matchId, ct);
        if (existing is null)
        {
            _db.CachedMatches.Add(new CachedMatch
            {
                MatchId = matchId,
                Name = name,
                MatchDate = date,
                DataJson = matchJson,
                SyncedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Name = name;
            existing.MatchDate = date;
            existing.DataJson = matchJson;
            existing.SyncedAt = DateTime.UtcNow;
        }

        // Clear + reseed the shooter list for this match. Simpler than
        // diffing; the list is small and comes from one authoritative
        // source.
        var oldShooters = await _db.CachedShooters.Where(s => s.MatchId == matchId).ToListAsync(ct);
        _db.CachedShooters.RemoveRange(oldShooters);
        using (var doc = JsonDocument.Parse(shootersJson))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("shooterId", out var sidEl)
                        || !sidEl.TryGetGuid(out var sid))
                    {
                        continue;
                    }
                    _db.CachedShooters.Add(new CachedShooter
                    {
                        MatchId = matchId,
                        ShooterId = sid,
                        DataJson = item.GetRawText(),
                    });
                }
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public sealed record MatchSummary(Guid Id, string Name, DateTime Date);
}
