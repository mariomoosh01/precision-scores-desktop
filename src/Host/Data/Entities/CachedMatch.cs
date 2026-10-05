namespace PrecisionScoresDesktop.Host.Data.Entities;

// A match downloaded from the cloud for offline scoring. The full
// MatchModel JSON blob is stored verbatim in DataJson so the Kestrel
// can hand it back to the React UI without re-mapping — avoids drift
// between the cloud entity shape and our local copy.
public sealed class CachedMatch
{
    public Guid MatchId { get; set; }
    public string Name { get; set; } = "";
    public DateTime MatchDate { get; set; }
    public string DataJson { get; set; } = "";
    public DateTime SyncedAt { get; set; }
}
