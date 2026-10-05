namespace PrecisionScoresDesktop.Host.Data.Entities;

// A scorecard the MD saved while offline. SyncedAt = null means it
// still needs to be pushed to the cloud. The CardString + score shape
// mirrors what the cloud's /match/SaveScoreCard endpoint expects, so
// the upload sync can POST this row almost verbatim.
public sealed class OfflineScorecard
{
    public Guid Id { get; set; }
    public Guid MatchId { get; set; }
    public Guid EventId { get; set; }
    public Guid ShooterId { get; set; }
    public short TargetId { get; set; }
    public short CardNumber { get; set; }
    public short TotalScore { get; set; }
    public string CardString { get; set; } = "";
    public int? WrittenTotal { get; set; }
    public string BankSubtotalsJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? SyncedAt { get; set; }
}
