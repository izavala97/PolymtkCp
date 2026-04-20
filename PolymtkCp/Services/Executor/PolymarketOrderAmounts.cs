using System.Numerics;

namespace PolymtkCp.Services.Executor;

/// <summary>
/// Converts (price, sizeShares, side) into the integer (makerAmount, takerAmount)
/// pair the CTF Exchange's Order struct expects, using USDC's 6-decimal scale and
/// the market's tick-size rounding rules.
///
/// <para>Mirrors <c>OrderBuilder.get_order_amounts</c> in py-clob-client. Rounding is
/// keyed by the market's tick size:
/// <code>
/// 0.1    => RoundConfig(price=1, size=2, amount=3)
/// 0.01   => RoundConfig(price=2, size=2, amount=4)
/// 0.001  => RoundConfig(price=3, size=2, amount=5)
/// 0.0001 => RoundConfig(price=4, size=2, amount=6)
/// </code>
/// </para>
///
/// <para>BUY: maker gives USDC, taker gives shares.
/// makerAmount = round(size * price), takerAmount = size, both * 1e6.</para>
/// <para>SELL: maker gives shares, taker gives USDC.
/// makerAmount = size, takerAmount = round(size * price), both * 1e6.</para>
/// </summary>
public static class PolymarketOrderAmounts
{
    private const int TokenDecimals = 6; // USDC

    public static (BigInteger MakerAmount, BigInteger TakerAmount) Compute(
        string side, decimal price, decimal sizeShares, string tickSize)
    {
        var cfg = RoundingFor(tickSize);

        var rawPrice = RoundHalfEven(price, cfg.Price);
        var rawSize = RoundDown(sizeShares, cfg.Size);

        if (side.Equals("BUY", StringComparison.OrdinalIgnoreCase))
        {
            // takerAmount = size (shares), makerAmount = size*price (USDC).
            var rawTaker = rawSize;
            var rawMaker = rawSize * rawPrice;
            // Two-step round: try a wider rounding first, then trim to amount precision.
            // Matches py-clob-client's behaviour to avoid silent precision loss.
            if (DecimalPlaces(rawMaker) > cfg.Amount)
            {
                rawMaker = RoundUp(rawMaker, cfg.Amount + 4);
                if (DecimalPlaces(rawMaker) > cfg.Amount)
                    rawMaker = RoundDown(rawMaker, cfg.Amount);
            }
            return (ToTokenUnits(rawMaker), ToTokenUnits(rawTaker));
        }
        if (side.Equals("SELL", StringComparison.OrdinalIgnoreCase))
        {
            var rawMaker = rawSize;
            var rawTaker = rawSize * rawPrice;
            if (DecimalPlaces(rawTaker) > cfg.Amount)
            {
                rawTaker = RoundUp(rawTaker, cfg.Amount + 4);
                if (DecimalPlaces(rawTaker) > cfg.Amount)
                    rawTaker = RoundDown(rawTaker, cfg.Amount);
            }
            return (ToTokenUnits(rawMaker), ToTokenUnits(rawTaker));
        }
        throw new ArgumentException($"Unknown side: {side}", nameof(side));
    }

    private record struct RoundCfg(int Price, int Size, int Amount);

    private static RoundCfg RoundingFor(string tickSize) => tickSize switch
    {
        "0.1"    => new(1, 2, 3),
        "0.01"   => new(2, 2, 4),
        "0.001"  => new(3, 2, 5),
        "0.0001" => new(4, 2, 6),
        _ => throw new ArgumentException($"Unsupported tick size: {tickSize}", nameof(tickSize)),
    };

    private static BigInteger ToTokenUnits(decimal value)
    {
        // value is rounded to <= TokenDecimals fractional digits — multiply by 10^TokenDecimals
        // and truncate any remaining sub-unit residue defensively.
        var scaled = value * 1_000_000m;
        return new BigInteger(decimal.Truncate(scaled));
    }

    private static decimal RoundHalfEven(decimal v, int digits) =>
        Math.Round(v, digits, MidpointRounding.ToEven);

    private static decimal RoundDown(decimal v, int digits)
    {
        var factor = Pow10(digits);
        return Math.Truncate(v * factor) / factor;
    }

    private static decimal RoundUp(decimal v, int digits)
    {
        var factor = Pow10(digits);
        return Math.Ceiling(v * factor) / factor;
    }

    private static decimal Pow10(int n)
    {
        decimal r = 1m;
        for (var i = 0; i < n; i++) r *= 10m;
        return r;
    }

    private static int DecimalPlaces(decimal v)
    {
        // Strip trailing zeros, then read the scale.
        v = v / 1.000000000000000000000000000000000m;
        var bits = decimal.GetBits(v);
        var scale = (bits[3] >> 16) & 0x7F;
        return scale;
    }
}
