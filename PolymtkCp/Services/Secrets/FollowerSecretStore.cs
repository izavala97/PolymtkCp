using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PolymtkCp.Models;
using Supabase.Postgrest;

namespace PolymtkCp.Services.Secrets;

/// <summary>
/// Encrypts/decrypts <see cref="PolymarketCredentials"/> via ASP.NET Data
/// Protection and persists ciphertext rows in the <c>follower_secrets</c>
/// Supabase table. Append-only: a Set creates a new row with version+1 and
/// flips the previous active row's <c>is_active=false</c>.
/// </summary>
public sealed class FollowerSecretStore : IFollowerSecretStore
{
    /// <summary>
    /// Data Protection purpose string. Treat as forever-stable: changing it
    /// would orphan every existing row's ciphertext.
    /// </summary>
    public const string ProtectorPurpose = "PolymtkCp.FollowerSecret.v1";

    private readonly Supabase.Client _supabase;
    private readonly IDataProtector _protector;
    private readonly ILogger<FollowerSecretStore> _logger;

    public FollowerSecretStore(
        Supabase.Client supabase,
        IDataProtectionProvider provider,
        ILogger<FollowerSecretStore> logger)
    {
        _supabase = supabase;
        _protector = provider.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    public async Task<FollowerSecretStatus> GetStatusAsync(Guid followerId, CancellationToken ct = default)
    {
        // Pull all rows for this follower and pick the active one in-memory.
        // Earlier attempts with server-side boolean filters (both string-based
        // Filter("is_active", Equals, true) and typed Where(s => s.IsActive))
        // silently returned 0 rows under PostgREST + this SDK version,
        // making the UI report "Not configured" even when an active row exists.
        // The set is tiny (one row per credential version), so client-side filter
        // is the simplest correct option.
        var rows = await _supabase
            .From<FollowerSecret>()
            .Where(s => s.FollowerId == followerId)
            .Order(s => s.Version, Constants.Ordering.Descending)
            .Limit(20)
            .Get(ct);

        var active = rows.Models.FirstOrDefault(r => r.IsActive);
        if (active is null)
            return new FollowerSecretStatus(false, null, null, false);

        // Decrypt to detect whether the row carries a wallet private key.
        // Cost is one Data Protection unprotect per call; the Profile page
        // calls this once per render, the Edit page once per render — both
        // negligible compared to the surrounding Supabase round-trips.
        bool hasPk = false;
        try
        {
            var creds = Decrypt(active.Ciphertext);
            hasPk = !string.IsNullOrEmpty(creds.PrivateKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GetStatusAsync: could not decrypt active row {Id} for follower {FollowerId}; " +
                "treating as no-private-key.",
                active.Id, followerId);
        }

        return new FollowerSecretStatus(true, active.Version, active.UpdatedAt, hasPk);
    }

    public async Task SetAsync(Guid followerId, PolymarketCredentials creds, CancellationToken ct = default)
    {
        var ciphertext = Encrypt(creds);

        // Find current active + max version (single round-trip — cap at the
        // latest few rows to be safe; in practice there's one active row).
        var rows = await _supabase
            .From<FollowerSecret>()
            .Where(s => s.FollowerId == followerId)
            .Order(s => s.Version, Constants.Ordering.Descending)
            .Limit(1)
            .Get();

        var latest = rows.Models.FirstOrDefault();
        var nextVersion = (latest?.Version ?? 0) + 1;

        // Deactivate the previous active row (if any) before inserting the
        // new active one. The partial unique index would otherwise reject
        // the insert. Two writes, no transaction — acceptable because the
        // worst-case interleaving leaves the user with no active creds,
        // which is recoverable by re-saving.
        if (latest is { IsActive: true })
        {
            await _supabase
                .From<FollowerSecret>()
                .Where(s => s.Id == latest.Id)
                .Set(s => s.IsActive, false)
                .Update();
        }

        await _supabase.From<FollowerSecret>().Insert(new FollowerSecret
        {
            FollowerId = followerId,
            Version = nextVersion,
            IsActive = true,
            Ciphertext = ciphertext,
        });

        _logger.LogInformation(
            "Stored Polymarket credentials for follower {FollowerId} as version {Version}.",
            followerId, nextVersion);
    }

    public async Task<PolymarketCredentials?> GetActiveAsync(Guid followerId, CancellationToken ct = default)
    {
        // Same pattern as GetStatusAsync — client-side filter to dodge SDK
        // boolean-serialization quirks.
        var rows = await _supabase
            .From<FollowerSecret>()
            .Where(s => s.FollowerId == followerId)
            .Order(s => s.Version, Constants.Ordering.Descending)
            .Limit(20)
            .Get(ct);

        var active = rows.Models.FirstOrDefault(r => r.IsActive);
        if (active is null) return null;

        try
        {
            return Decrypt(active.Ciphertext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to decrypt follower_secrets row {Id} for follower {FollowerId}. " +
                "The Data Protection key that protected this row is no longer available.",
                active.Id, followerId);
            return null;
        }
    }

    public async Task ClearAsync(Guid followerId, CancellationToken ct = default)
    {
        // Find active rows client-side, then update them by primary key.
        // Avoids the SDK boolean-filter quirk that has plagued GetStatusAsync.
        var rows = await _supabase
            .From<FollowerSecret>()
            .Where(s => s.FollowerId == followerId)
            .Order(s => s.Version, Constants.Ordering.Descending)
            .Limit(20)
            .Get(ct);

        foreach (var row in rows.Models.Where(r => r.IsActive))
        {
            await _supabase
                .From<FollowerSecret>()
                .Where(s => s.Id == row.Id)
                .Set(s => s.IsActive, false)
                .Update();
        }

        _logger.LogInformation("Cleared active Polymarket credentials for follower {FollowerId}.", followerId);
    }

    private string Encrypt(PolymarketCredentials creds)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(creds);
        var protectedBytes = _protector.Protect(json);
        return Convert.ToBase64String(protectedBytes);
    }

    private PolymarketCredentials Decrypt(string ciphertext)
    {
        var protectedBytes = Convert.FromBase64String(ciphertext);
        var json = _protector.Unprotect(protectedBytes);
        return JsonSerializer.Deserialize<PolymarketCredentials>(json)
            ?? throw new InvalidOperationException("Decrypted credentials JSON was empty.");
    }
}
