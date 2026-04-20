namespace PolymtkCp.Services.Secrets;

/// <summary>
/// Polymarket credentials supplied by a Follower for the phase-2 executor.
///
/// <list type="bullet">
///   <item>L2 HMAC triple (<c>ApiKey</c> / <c>Secret</c> / <c>Passphrase</c>) — authenticates the HTTP request.</item>
///   <item>Wallet L1 <c>PrivateKey</c> — EIP-712-signs the order struct embedded in the request body.</item>
///   <item><c>FunderAddress</c> (optional) — the address that holds the funds. For Magic.Link / Gnosis Safe
///     proxy wallets, this is the address Polymarket displays in the UI; the EOA derived from
///     <c>PrivateKey</c> is the signer but a different address holds the funds. If null, the executor
///     falls back to the wallet stored on the Follower's profile.</item>
///   <item><c>SignatureType</c> (optional) — 0 = EOA, 1 = POLY_PROXY (Magic.Link), 2 = POLY_GNOSIS_SAFE.
///     Defaults to <c>1</c> because the most common Polymarket onboarding flow is Magic.Link email login.</item>
/// </list>
///
/// Stored encrypted at rest via ASP.NET Data Protection (KEK in Azure Key Vault).
/// Never logged; never returned to the browser.
///
/// <para>All fields after <c>Passphrase</c> are nullable / defaulted so older rows
/// that were saved before phase-2 still deserialize cleanly.</para>
/// </summary>
public sealed record PolymarketCredentials(
    string ApiKey,
    string Secret,
    string Passphrase,
    string? PrivateKey = null,
    string? FunderAddress = null,
    int SignatureType = 1);
