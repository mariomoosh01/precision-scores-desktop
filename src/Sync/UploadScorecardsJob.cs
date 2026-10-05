using Microsoft.EntityFrameworkCore;
using PrecisionScoresDesktop.Host.Data;

namespace PrecisionScoresDesktop.Sync;

// Post-match sync. Reads every OfflineScorecard with SyncedAt IS NULL,
// POSTs it to the cloud (via CloudClient's upsert path), stamps the
// row with SyncedAt on success. Rows that fail three attempts stay
// unsynced; next trigger will retry them.
public sealed class UploadScorecardsJob
{
    private readonly CloudClient _cloud;
    private readonly OfflineDbContext _db;

    public UploadScorecardsJob(CloudClient cloud, OfflineDbContext db)
    {
        _cloud = cloud;
        _db = db;
    }

    public async Task<UploadReport> RunAsync(
        IProgress<UploadReport>? progress = null,
        CancellationToken ct = default)
    {
        var pending = await _db.OfflineScorecards
            .Where(s => s.SyncedAt == null)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        int uploaded = 0, failed = 0;
        var report = new UploadReport(pending.Count, 0, 0);

        foreach (var card in pending)
        {
            if (ct.IsCancellationRequested) break;
            var payload = new
            {
                eventId = card.EventId,
                shooterId = card.ShooterId,
                targetId = card.TargetId,
                cardNumber = card.CardNumber,
                totalScore = card.TotalScore,
                cardString = card.CardString,
            };
            bool ok = false;
            for (int attempt = 1; attempt <= 3 && !ok && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    await _cloud.SaveScoreCardAsync(payload, ct);
                    ok = true;
                }
                catch
                {
                    if (attempt < 3)
                    {
                        // Linear-ish backoff; keeps the loop responsive
                        // to Cancel but doesn't hammer a flaky link.
                        await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                    }
                }
            }
            if (ok)
            {
                card.SyncedAt = DateTime.UtcNow;
                uploaded++;
            }
            else
            {
                failed++;
            }
            report = new UploadReport(pending.Count, uploaded, failed);
            progress?.Report(report);
        }

        await _db.SaveChangesAsync(ct);
        return report;
    }

    public sealed record UploadReport(int Total, int Uploaded, int Failed);
}
