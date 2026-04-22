namespace PolymtkCp.Services.Secrets;

/// <summary>
/// Status of the active credentials row for the current Follower. Returned
/// by the store so UI can render "Configured (v N, set &lt;date&gt;)" and
/// reason about Real-mode readiness without ever exposing decrypted material
/// to the page.
/// </summary>
/// <param name="HasPrivateKey">
/// True only when the active row decrypts to a credentials record that includes
/// a non-empty <see cref="PolymarketCredentials.PrivateKey"/>. The Real mode
/// of a CopyPlan requires this. Rows saved before the wallet key was collected
/// are <c>HasActive = true</c> but <c>HasPrivateKey = false</c>.
/// </param>
public sealed record FollowerSecretStatus(
    bool HasActive,
    int? Version,
    DateTime? UpdatedAt,
    bool HasPrivateKey);

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
    /// Executor only — UI should never call this.
    /// </summary>
    Task<PolymarketCredentials?> GetActiveAsync(Guid followerId, CancellationToken ct = default);

    /// <summary>Deactivates the currently active row. Idempotent.</summary>
    Task ClearAsync(Guid followerId, CancellationToken ct = default);
}
