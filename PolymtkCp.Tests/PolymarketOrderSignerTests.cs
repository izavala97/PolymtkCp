using System.Numerics;
using Nethereum.Signer;
using PolymtkCp.Services.Executor;

namespace PolymtkCp.Tests;

/// <summary>
/// Verifies the EIP-712 signing implementation by:
/// <list type="number">
///   <item>Signing a deterministic order with a known private key.</item>
///   <item>Recovering the signer address from the digest + signature.</item>
///   <item>Asserting it matches the address derived from the same private key.</item>
/// </list>
/// This proves the digest construction (domain + struct hash) and the secp256k1
/// signing path are internally consistent without needing an external golden vector.
/// </summary>
public class PolymarketOrderSignerTests
{
    // Well-known dev key from Hardhat / Anvil. NOT a real wallet; safe to commit.
    private const string TestPrivateKey =
        "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";

    private static SignedOrderData SampleOrder() => new(
        Salt: BigInteger.Parse("123456789"),
        Maker: "0xAbCdEf0123456789AbCdEf0123456789AbCdEf01",
        Signer: new EthECKey(TestPrivateKey).GetPublicAddress(),
        Taker: "0x0000000000000000000000000000000000000000",
        TokenId: BigInteger.Parse("71321045679252212594626385532706912750332728571942134274898066861057779470484"),
        MakerAmount: new BigInteger(5_500_000),
        TakerAmount: new BigInteger(10_000_000),
        Expiration: 0,
        Nonce: 0,
        FeeRateBps: 0,
        Side: 0,        // BUY
        SignatureType: 1); // POLY_PROXY

    [Fact]
    public void Sign_then_recover_returns_signer_address()
    {
        var order = SampleOrder();
        var signatureHex = PolymarketOrderSigner.Sign(order, negRisk: false, chainId: 137, TestPrivateKey);
        var digest = PolymarketOrderSigner.ComputeDigest(order, negRisk: false, chainId: 137);

        var sigBytes = Convert.FromHexString(signatureHex.Substring(2));
        Assert.Equal(65, sigBytes.Length);

        var ecdsa = EthECDSASignatureFactory.ExtractECDSASignature(signatureHex);
        var recoveredAddress = EthECKey.RecoverFromSignature(ecdsa, digest).GetPublicAddress();

        Assert.Equal(order.Signer.ToLowerInvariant(), recoveredAddress.ToLowerInvariant());
    }

    [Fact]
    public void Sign_is_deterministic_for_same_inputs()
    {
        // Nethereum's signer uses RFC 6979 deterministic k, so identical inputs
        // must produce identical signatures.
        var order = SampleOrder();
        var s1 = PolymarketOrderSigner.Sign(order, negRisk: false, chainId: 137, TestPrivateKey);
        var s2 = PolymarketOrderSigner.Sign(order, negRisk: false, chainId: 137, TestPrivateKey);
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void Negrisk_uses_a_different_domain_than_regular()
    {
        // Different verifyingContract + name -> different digest -> different signature.
        var order = SampleOrder();
        var d1 = PolymarketOrderSigner.ComputeDigest(order, negRisk: false, chainId: 137);
        var d2 = PolymarketOrderSigner.ComputeDigest(order, negRisk: true,  chainId: 137);
        Assert.NotEqual(Convert.ToHexString(d1), Convert.ToHexString(d2));
    }

    [Fact]
    public void Different_chain_id_produces_different_digest()
    {
        var order = SampleOrder();
        var mainnet = PolymarketOrderSigner.ComputeDigest(order, negRisk: false, chainId: 137);
        var mumbai  = PolymarketOrderSigner.ComputeDigest(order, negRisk: false, chainId: 80001);
        Assert.NotEqual(Convert.ToHexString(mainnet), Convert.ToHexString(mumbai));
    }

    [Fact]
    public void NewSalt_returns_positive_value_with_high_entropy()
    {
        var s1 = PolymarketOrderSigner.NewSalt();
        var s2 = PolymarketOrderSigner.NewSalt();
        Assert.True(s1.Sign >= 0);
        Assert.True(s2.Sign >= 0);
        Assert.NotEqual(s1, s2); // 256-bit collision is astronomically unlikely
    }
}
