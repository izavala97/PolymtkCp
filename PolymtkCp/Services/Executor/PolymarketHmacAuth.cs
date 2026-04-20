using System.Security.Cryptography;
using System.Text;

namespace PolymtkCp.Services.Executor;

/// <summary>
/// Builds the five POLY_* HMAC headers Polymarket requires on every authenticated
/// CLOB request. Mirrors <c>py-clob-client/signing/hmac.py</c>:
/// <code>
/// secret_bytes = base64-urlsafe-decode(creds.Secret)
/// message      = timestamp + method + requestPath + body  (single-quotes -> double)
/// signature    = base64-urlsafe-encode(HMAC-SHA256(secret_bytes, message))
/// </code>
///
/// Extracted from <see cref="PolymarketClobClient"/> so the byte-level signing logic
/// can be unit-tested without spinning up an HttpClient.
/// </summary>
public static class PolymarketHmacAuth
{
    public sealed record Headers(
        string Address,
        string Signature,
        string Timestamp,
        string ApiKey,
        string Passphrase);

    /// <summary>
    /// Compute the headers for a request. <paramref name="timestampOverride"/> is for tests;
    /// production callers should leave it null so <see cref="DateTimeOffset.UtcNow"/> is used.
    /// </summary>
    public static Headers Build(
        string apiKey, string secret, string passphrase,
        string funderAddress, string method, string path, string body,
        long? timestampOverride = null)
    {
        var timestamp = (timestampOverride ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var normalizedBody = body.Replace("'", "\"");
        var message = timestamp + method + path + normalizedBody;

        var key = Base64UrlDecode(secret);
        using var hmac = new HMACSHA256(key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        var signature = Base64UrlEncode(digest);

        return new Headers(funderAddress, signature, timestamp, apiKey, passphrase);
    }

    public static byte[] Base64UrlDecode(string input)
    {
        // urlsafe variant uses '-' and '_' instead of '+' and '/'.
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
}
