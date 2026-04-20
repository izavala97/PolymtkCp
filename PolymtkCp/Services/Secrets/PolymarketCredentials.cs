namespace PolymtkCp.Services.Secrets;

/// <summary>
/// Polymarket credentials supplied by a Follower for the phase-2 executor.
///
/// Three pieces are required to place an order on the Polymarket CLOB:
/// <list type="bullet">
///   <item>L2 HMAC triple (<c>ApiKey</c> / <c>Secret</c> / <c>Passphrase</c>) — authenticates the HTTP request.</item>
///   <item>Wallet L1 <c>PrivateKey</c> — EIP-712-signs the order struct embedded in the request body.</item>
/// </list>
///
/// Stored encrypted at rest via ASP.NET Data Protection (KEK in Azure Key Vault).
/// Never logged; never returned to the browser.
///
/// <para><c>PrivateKey</c> is nullable so rows saved before phase-2 still
/// deserialize. Such rows can sign read-only CLOB calls but the executor
/// surfaces them as <c>failed</c> with a "private key missing" reason.</para>
/// </summary>
public sealed record PolymarketCredentials(
    string ApiKey,
    string Secret,
    string Passphrase,
    string? PrivateKey = null);
