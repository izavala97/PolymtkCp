using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace PolymtkCp.Models;

/// <summary>
/// A Polymarket trader (wallet) being watched. Shared across all Followers —
/// the nickname is a cached display label, not a per-Follower preference.
/// One row per wallet address.
/// </summary>
[Table("traders")]
public class Trader : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    /// <summary>Polymarket wallet address (proxy wallet, lowercase, with 0x).</summary>
    [Column("wallet_address")]
    public string WalletAddress { get; set; } = string.Empty;

    /// <summary>Cached display name; refreshed when any Follower adds or re-validates the trader.</summary>
    [Column("nickname")]
    public string? Nickname { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
