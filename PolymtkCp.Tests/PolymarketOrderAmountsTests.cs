using System.Numerics;
using PolymtkCp.Services.Executor;

namespace PolymtkCp.Tests;

/// <summary>
/// Verifies the (price, size) -> (makerAmount, takerAmount) math against
/// hand-computed expectations. USDC has 6 decimals throughout.
/// </summary>
public class PolymarketOrderAmountsTests
{
    [Fact]
    public void Buy_at_tick_0_01_scales_size_and_size_times_price_by_1e6()
    {
        // size=10 shares @ price=0.55 -> maker=5.5 USDC, taker=10 shares
        // In 6-decimal units: maker=5_500_000, taker=10_000_000
        var (maker, taker) = PolymarketOrderAmounts.Compute("BUY", price: 0.55m, sizeShares: 10m, tickSize: "0.01");
        Assert.Equal(new BigInteger(5_500_000), maker);
        Assert.Equal(new BigInteger(10_000_000), taker);
    }

    [Fact]
    public void Sell_swaps_maker_and_taker()
    {
        // SELL 10 @ 0.55 -> maker=10 shares, taker=5.5 USDC
        var (maker, taker) = PolymarketOrderAmounts.Compute("SELL", price: 0.55m, sizeShares: 10m, tickSize: "0.01");
        Assert.Equal(new BigInteger(10_000_000), maker);
        Assert.Equal(new BigInteger(5_500_000), taker);
    }

    [Fact]
    public void Tick_0_001_allows_three_decimal_price()
    {
        // size=100 @ 0.123 -> maker=12.3 USDC = 12_300_000
        var (maker, taker) = PolymarketOrderAmounts.Compute("BUY", 0.123m, 100m, "0.001");
        Assert.Equal(new BigInteger(12_300_000), maker);
        Assert.Equal(new BigInteger(100_000_000), taker);
    }

    [Fact]
    public void Size_is_floored_at_two_decimals()
    {
        // size=1.999 with size precision 2 -> floored to 1.99
        // maker amount = 1.99 * 0.50 = 0.995 USDC = 995_000
        var (maker, taker) = PolymarketOrderAmounts.Compute("BUY", 0.50m, 1.999m, "0.01");
        Assert.Equal(new BigInteger(995_000), maker);
        Assert.Equal(new BigInteger(1_990_000), taker);
    }

    [Fact]
    public void Case_insensitive_side()
    {
        var (m1, t1) = PolymarketOrderAmounts.Compute("buy", 0.5m, 2m, "0.01");
        var (m2, t2) = PolymarketOrderAmounts.Compute("BUY", 0.5m, 2m, "0.01");
        Assert.Equal(m1, m2);
        Assert.Equal(t1, t2);
    }

    [Fact]
    public void Unknown_side_throws() =>
        Assert.Throws<ArgumentException>(() =>
            PolymarketOrderAmounts.Compute("HOLD", 0.5m, 1m, "0.01"));

    [Fact]
    public void Unsupported_tick_size_throws() =>
        Assert.Throws<ArgumentException>(() =>
            PolymarketOrderAmounts.Compute("BUY", 0.5m, 1m, "0.5"));
}
