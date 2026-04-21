using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PolymtkCp.Models;
using PolymtkCp.Services.Secrets;

namespace PolymtkCp.Services.Executor;

/// <summary>
/// Service-role-flavored read path for <c>follower_secrets</c>. The HTTP-bound
/// <see cref="FollowerSecretStore"/> uses the per-request Supabase.Client (RLS-bound
/// to the logged-in Follower); the executor runs out of band and needs to decrypt
/// rows for arbitrary followers, so it uses the SERVICE-ROLE client instead.
///
/// Encryption purpose string MUST match <see cref="FollowerSecretStore.ProtectorPurpose"/> —
/// otherwise rows written by the UI cannot be decrypted by the executor and vice versa.
/// </summary>
public sealed class ExecutorSecretReader
{
    private readonly Supabase.Client _supabase;
    private readonly IDataProtector _protector;
    private readonly ILogger<ExecutorSecretReader> _logger;

    public ExecutorSecretReader(
        Supabase.Client supabase,
        IDataProtectionProvider provider,
        ILogger<ExecutorSecretReader> logger)
    {
        _supabase = supabase;
        _protector = provider.CreateProtector(FollowerSecretStore.ProtectorPurpose);
        _logger = logger;
    }

    public async Task<PolymarketCredentials?> GetActiveAsync(Guid followerId, CancellationToken ct = default)
    {
        // See FollowerSecretStore.GetStatusAsync: typed Where on the bool
        // column avoids the SDK boolean-filter quirk that causes Single()
        // to silently return null when the filter doesn't apply.
        var rows = await _supabase
            .From<FollowerSecret>()
            .Where(s => s.FollowerId == followerId && s.IsActive)
            .Limit(1)
            .Get(ct);

        var active = rows.Models.FirstOrDefault();
        if (active is null) return null;

        try
        {
            var protectedBytes = Convert.FromBase64String(active.Ciphertext);
            var json = _protector.Unprotect(protectedBytes);
            return JsonSerializer.Deserialize<PolymarketCredentials>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ExecutorSecretReader: could not decrypt follower_secrets row {Id} for {FollowerId}.",
                active.Id, followerId);
            return null;
        }
    }
}
