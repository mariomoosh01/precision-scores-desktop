namespace PrecisionScoresDesktop.Host.Data.Entities;

// A shooter registered for a specific downloaded match. Composite PK
// (MatchId, ShooterId) — a shooter may show up across many downloaded
// matches, and we want to be able to scope roster queries per match
// without a join.
public sealed class CachedShooter
{
    public Guid MatchId { get; set; }
    public Guid ShooterId { get; set; }
    public string DataJson { get; set; } = "";
}
