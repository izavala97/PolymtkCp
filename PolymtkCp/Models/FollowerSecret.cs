using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace PolymtkCp.Models;

/// <summary>
/// Encrypted Polymarket L2 API credentials for one Follower. Append-only:
/// every Set creates a new row with version+1 and flips the previous active
/// row's <see cref="IsActive"/> to false. The store enforces "exactly one
/// active row per follower" both in code and via a partial unique index.
/// </summary>
[Table("follower_secrets")]
public class FollowerSecret : BaseModel
{
    [PrimaryKey("id", false)]
    public Guid Id { get; set; }

    [Column("follower_id")]
    public Guid FollowerId { get; set; }

    [Column("version")]
    public int Version { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    /// <summary>
    /// ASP.NET Data Protection output (base64-encoded) of the JSON-serialized
    /// <c>PolymarketCredentials</c>. KEK lives in Azure Key Vault.
    /// </summary>
    [Column("ciphertext")]
    public string Ciphertext { get; set; } = string.Empty;

    [Column("created_at", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at", ignoreOnInsert: true, ignoreOnUpdate: true)]
    public DateTime UpdatedAt { get; set; }
}
