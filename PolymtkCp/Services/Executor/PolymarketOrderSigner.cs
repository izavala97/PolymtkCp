using System.Numerics;
using System.Security.Cryptography;
using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;

namespace PolymtkCp.Services.Executor;

/// <summary>
/// EIP-712 signer for Polymarket CTF Exchange <c>Order</c> structs.
/// Mirrors the reference implementations in
/// <see href="https://github.com/Polymarket/py-order-utils">py-order-utils</see>
/// and the TypeScript order builder used by the official py-clob-client / clob-client.
///
/// <para>Domain (Polygon mainnet):
/// <list type="bullet">
///   <item>name = "Polymarket CTF Exchange"</item>
///   <item>version = "1"</item>
///   <item>chainId = 137</item>
///   <item>verifyingContract = the chosen Exchange contract (regular or NegRisk)</item>
/// </list></para>
///
/// <para>Order type hash:
/// <c>Order(uint256 salt,address maker,address signer,address taker,uint256 tokenId,
/// uint256 makerAmount,uint256 takerAmount,uint256 expiration,uint256 nonce,
/// uint256 feeRateBps,uint8 side,uint8 signatureType)</c></para>
/// </summary>
public static class PolymarketOrderSigner
{
    public const string DomainNameRegular = "Polymarket CTF Exchange";
    public const string DomainNameNegRisk = "Polymarket Neg Risk CTF Exchange";
    public const string DomainVersion = "1";

    /// <summary>Polygon mainnet chain id (Polymarket's home network).</summary>
    public const int PolygonChainId = 137;

    /// <summary>Regular CTF Exchange (binary markets).</summary>
    public const string ExchangeAddressRegular = "0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E";

    /// <summary>Neg-Risk CTF Exchange (multi-outcome markets).</summary>
    public const string ExchangeAddressNegRisk = "0xC5d563A36AE78145C45a50134d48A1215220f80a";

    private static readonly Sha3Keccack Keccak = Sha3Keccack.Current;

    // keccak256("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)")
    private static readonly byte[] DomainTypeHash = Keccak.CalculateHash(
        System.Text.Encoding.UTF8.GetBytes(
            "EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"));

    // keccak256(<order type string>)
    private static readonly byte[] OrderTypeHash = Keccak.CalculateHash(
        System.Text.Encoding.UTF8.GetBytes(
            "Order(uint256 salt,address maker,address signer,address taker,uint256 tokenId,"
          + "uint256 makerAmount,uint256 takerAmount,uint256 expiration,uint256 nonce,"
          + "uint256 feeRateBps,uint8 side,uint8 signatureType)"));

    /// <summary>
    /// Compute the EIP-712 digest for the given order under the given domain, sign it
    /// with the EOA private key, and return the 65-byte (r,s,v) signature as 0x-hex.
    /// </summary>
    public static string Sign(SignedOrderData order, bool negRisk, int chainId, string privateKeyHex)
    {
        var digest = ComputeDigest(order, negRisk, chainId);
        var signer = new EthECKey(privateKeyHex);
        var sig = signer.SignAndCalculateV(digest);

        // Canonical 0x-hex of r || s || v (v is 27 or 28).
        var signature = new byte[65];
        Array.Copy(PadTo32(sig.R), 0, signature, 0, 32);
        Array.Copy(PadTo32(sig.S), 0, signature, 32, 32);
        signature[64] = sig.V[0];
        return "0x" + Convert.ToHexString(signature).ToLowerInvariant();
    }

    /// <summary>
    /// Compute just the EIP-712 digest (without signing) for the given order under
    /// the given domain. Exposed publicly for unit tests that want to verify the
    /// signature recovers the expected signer address.
    /// </summary>
    public static byte[] ComputeDigest(SignedOrderData order, bool negRisk, int chainId)
    {
        var verifyingContract = negRisk ? ExchangeAddressNegRisk : ExchangeAddressRegular;
        var domainName = negRisk ? DomainNameNegRisk : DomainNameRegular;

        var domainSeparator = ComputeDomainSeparator(domainName, DomainVersion, chainId, verifyingContract);
        var orderHash = ComputeOrderHash(order);

        // EIP-712 final digest: keccak256(0x1901 || domainSeparator || hashStruct(order))
        var prefix = new byte[] { 0x19, 0x01 };
        return Keccak.CalculateHash(Concat(prefix, domainSeparator, orderHash));
    }

    /// <summary>Generate a fresh 256-bit random salt as a positive BigInteger.</summary>
    public static BigInteger NewSalt()
    {
        // Match py-order-utils: 256-bit random integer. Use cryptographic RNG.
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        // Force positive by clearing the high bit so BigInteger interprets as non-negative.
        buf[0] &= 0x7F;
        return new BigInteger(buf, isUnsigned: true, isBigEndian: true);
    }

    private static byte[] ComputeDomainSeparator(string name, string version, int chainId, string verifyingContract)
    {
        // hashStruct(EIP712Domain) = keccak256(typeHash || keccak256(name) || keccak256(version) || chainId(32) || addr(32))
        var nameHash = Keccak.CalculateHash(System.Text.Encoding.UTF8.GetBytes(name));
        var versionHash = Keccak.CalculateHash(System.Text.Encoding.UTF8.GetBytes(version));
        var chainIdEnc = EncodeUint256(new BigInteger(chainId));
        var addrEnc = EncodeAddress(verifyingContract);
        return Keccak.CalculateHash(Concat(DomainTypeHash, nameHash, versionHash, chainIdEnc, addrEnc));
    }

    private static byte[] ComputeOrderHash(SignedOrderData o)
    {
        // hashStruct(Order) — encode each field per EIP-712 v4. All ints/uints become 32-byte big-endian.
        return Keccak.CalculateHash(Concat(
            OrderTypeHash,
            EncodeUint256(o.Salt),
            EncodeAddress(o.Maker),
            EncodeAddress(o.Signer),
            EncodeAddress(o.Taker),
            EncodeUint256(o.TokenId),
            EncodeUint256(o.MakerAmount),
            EncodeUint256(o.TakerAmount),
            EncodeUint256(o.Expiration),
            EncodeUint256(o.Nonce),
            EncodeUint256(o.FeeRateBps),
            EncodeUint8(o.Side),
            EncodeUint8(o.SignatureType)));
    }

    private static byte[] EncodeUint256(BigInteger value)
    {
        // EIP-712: uint256 encoded as 32 bytes, big-endian, unsigned.
        if (value.Sign < 0) throw new ArgumentException("uint256 cannot be negative", nameof(value));
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        return PadTo32(bytes);
    }

    private static byte[] EncodeUint8(byte value)
    {
        // EIP-712: uint8 also left-padded to 32 bytes.
        var buf = new byte[32];
        buf[31] = value;
        return buf;
    }

    private static byte[] EncodeAddress(string address)
    {
        // Address: 20 bytes, left-padded to 32. Strip 0x, validate length.
        var hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        if (hex.Length != 40)
            throw new ArgumentException($"Invalid Ethereum address: {address}", nameof(address));
        var bytes = Convert.FromHexString(hex);
        return PadTo32(bytes);
    }

    private static byte[] PadTo32(byte[] bytes)
    {
        if (bytes.Length == 32) return bytes;
        if (bytes.Length > 32) throw new ArgumentException("Value exceeds 32 bytes.");
        var padded = new byte[32];
        Array.Copy(bytes, 0, padded, 32 - bytes.Length, bytes.Length);
        return padded;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = 0;
        foreach (var a in arrays) total += a.Length;
        var result = new byte[total];
        var offset = 0;
        foreach (var a in arrays)
        {
            Buffer.BlockCopy(a, 0, result, offset, a.Length);
            offset += a.Length;
        }
        return result;
    }
}

/// <summary>Order struct in the exact field set / encoding expected by the CTF Exchange.</summary>
public sealed record SignedOrderData(
    BigInteger Salt,
    string Maker,            // funder address
    string Signer,           // EOA derived from private key
    string Taker,            // ZERO_ADDRESS for public orders
    BigInteger TokenId,
    BigInteger MakerAmount,  // 6-decimal token units (USDC has 6 decimals)
    BigInteger TakerAmount,
    BigInteger Expiration,   // UNIX seconds; 0 = no expiration
    BigInteger Nonce,
    BigInteger FeeRateBps,
    byte Side,               // 0 = BUY, 1 = SELL
    byte SignatureType);     // 0 = EOA, 1 = POLY_PROXY, 2 = POLY_GNOSIS_SAFE
