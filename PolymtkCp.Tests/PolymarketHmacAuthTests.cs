using System.Security.Cryptography;
using System.Text;
using PolymtkCp.Services.Executor;

namespace PolymtkCp.Tests;

/// <summary>
/// Tests the HMAC-SHA256 signing path against an in-test reference computation,
/// plus the urlsafe-base64 helpers and the Polymarket-specific body normalization
/// (single quotes -> double quotes) that mirrors py-clob-client's hmac.py.
/// </summary>
public class PolymarketHmacAuthTests
{
    private const string ApiKey      = "api-key-1";
    private const string Passphrase  = "pass-1";
    private const string FunderAddr  = "0x1111111111111111111111111111111111111111";
    // base64-urlsafe encoding of the bytes 1..10. Picking a value that round-trips
    // through the urlsafe variant keeps the test honest about decoding.
    private const string SecretB64Url = "AQIDBAUGBwgJCg==";

    [Fact]
    public void Headers_contain_all_five_poly_fields()
    {
        var h = PolymarketHmacAuth.Build(
            ApiKey, SecretB64Url, Passphrase, FunderAddr,
            "POST", "/order", "{\"x\":1}",
            timestampOverride: 1_700_000_000);

        Assert.Equal(FunderAddr, h.Address);
        Assert.Equal(ApiKey, h.ApiKey);
        Assert.Equal(Passphrase, h.Passphrase);
        Assert.Equal("1700000000", h.Timestamp);
        Assert.False(string.IsNullOrEmpty(h.Signature));
    }

    [Fact]
    public void Signature_matches_reference_hmac_sha256()
    {
        // Re-implement the Polymarket signing recipe inline as the source of truth,
        // then assert PolymarketHmacAuth produces the same bytes. This catches any
        // regression in message assembly (e.g. swapped order, missing field).
        const long ts = 1_700_000_000;
        const string method = "POST";
        const string path = "/order";
        const string body = "{\"x\":1}";

        var key = PolymarketHmacAuth.Base64UrlDecode(SecretB64Url);
        var msg = ts + method + path + body;
        using var hmac = new HMACSHA256(key);
        var expected = PolymarketHmacAuth.Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(msg)));

        var actual = PolymarketHmacAuth.Build(
            ApiKey, SecretB64Url, Passphrase, FunderAddr, method, path, body, ts).Signature;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Body_single_quotes_are_normalized_to_double_quotes_before_signing()
    {
        // py-clob-client does `str(body).replace("'", '"')`. A body containing
        // single quotes must produce the SAME signature as one that already uses
        // double quotes. If we forget to normalize, server-side verification fails.
        var hSingle = PolymarketHmacAuth.Build(
            ApiKey, SecretB64Url, Passphrase, FunderAddr,
            "POST", "/order", "{'a':'b'}", timestampOverride: 42);
        var hDouble = PolymarketHmacAuth.Build(
            ApiKey, SecretB64Url, Passphrase, FunderAddr,
            "POST", "/order", "{\"a\":\"b\"}", timestampOverride: 42);

        Assert.Equal(hDouble.Signature, hSingle.Signature);
    }

    [Fact]
    public void Build_is_deterministic_for_same_inputs()
    {
        var a = PolymarketHmacAuth.Build(ApiKey, SecretB64Url, Passphrase, FunderAddr,
            "GET", "/health", "", timestampOverride: 1);
        var b = PolymarketHmacAuth.Build(ApiKey, SecretB64Url, Passphrase, FunderAddr,
            "GET", "/health", "", timestampOverride: 1);
        Assert.Equal(a.Signature, b.Signature);
    }

    [Theory]
    [InlineData("AQIDBAUGBwgJCg==", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    [InlineData("AQIDBAUGBwgJCg",   new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]  // no padding
    [InlineData("-_8=",             new byte[] { 0xFB, 0xFF })]                     // urlsafe alphabet
    public void Base64UrlDecode_round_trip(string encoded, byte[] expected)
    {
        var decoded = PolymarketHmacAuth.Base64UrlDecode(encoded);
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void Base64UrlEncode_uses_urlsafe_alphabet_no_plus_or_slash()
    {
        // 0xFB 0xFF -> standard base64 "+/8=" -> urlsafe "-_8="
        var s = PolymarketHmacAuth.Base64UrlEncode(new byte[] { 0xFB, 0xFF });
        Assert.DoesNotContain('+', s);
        Assert.DoesNotContain('/', s);
        Assert.Equal("-_8=", s);
    }
}
