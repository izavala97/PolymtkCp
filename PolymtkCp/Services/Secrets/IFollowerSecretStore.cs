namespace PolymtkCp.Services.Secrets;

/// <summary>
/// Status of the active credentials row for the current Follower. Returned
/// by the store so UI can render "Configured (v N, set <date>)" without
/// ever decrypting the ciphertext.
/// </summary>
public sealed record FollowerSecretStatus(bool HasActive, int? Version, DateTime? UpdatedAt);

public interface IFollowerSecretStore
{
    Task<FollowerSecretStatus> GetStatusAsync(Guid followerId, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new active row with the next version number and deactivates
    /// the previous active row, if any. The row is encrypted with the current
    /// active Data Protection key.
    /// </summary>
    Task SetAsync(Guid followerId, PolymarketCredentials creds, CancellationToken ct = default);

    /// <summary>
    /// Decrypts the currently active row. Returns null if no active row.
    /// Phase-2 executor only — UI should never call this.
    /// </summary>
    Task<PolymarketCredentials?> GetActiveAsync(Guid followerId, CancellationToken ct = default);

    /// <summary>Deactivates the currently active row. Idempotent.</summary>
    Task ClearAsync(Guid followerId, CancellationToken ct = default);
}
