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

    [Column("created_at", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public DateTime UpdatedAt { get; set; }
}
