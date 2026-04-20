namespace PolymtkCp.Services.Secrets;

/// <summary>
/// Polymarket L2 (CLOB) API credentials supplied by a Follower. Used by the
/// phase-2 executor to sign order requests. Never logged; never returned to
/// the browser; encrypted at rest via ASP.NET Data Protection.
/// </summary>
public sealed record PolymarketCredentials(string ApiKey, string Secret, string Passphrase);
