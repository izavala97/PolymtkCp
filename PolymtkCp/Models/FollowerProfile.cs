using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace PolymtkCp.Models;

/// <summary>
/// A Follower's per-account settings — primarily their Polymarket wallet
/// address used for read-only balance display. One row per Follower.
/// </summary>
[Table("follower_profiles")]
public class FollowerProfile : BaseModel
{
    /// <summary>Supabase auth user id. Primary key.</summary>
    [PrimaryKey("follower_id", true)]
    public Guid FollowerId { get; set; }

    /// <summary>Public Polymarket proxy wallet address (lowercase, with 0x).</summary>
    [Column("polymarket_wallet_address")]
    public string? PolymarketWalletAddress { get; set; }

    /// <summary>Reserved for phase 2 trade execution. Always null today.</summary>
    [Column("encrypted_api_key")]
    public string? EncryptedApiKey { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
