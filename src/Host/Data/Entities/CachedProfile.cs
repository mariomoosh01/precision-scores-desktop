namespace PrecisionScoresDesktop.Host.Data.Entities;

// Cached auth material. Only ever one row (the current user), keyed by
// email for easy upsert. JwtEncrypted is the last-issued cloud JWT,
// protected at rest with DPAPI (Windows) / Keychain (macOS); handled by
// a wrapper service, not EF directly.
public sealed class CachedProfile
{
    public string Email { get; set; } = "";
    public Guid ProfileId { get; set; }
    public string DataJson { get; set; } = "";
    public string JwtEncrypted { get; set; } = "";
    public DateTime JwtIssuedAt { get; set; }
}
