using PolymtkCp.Services;

namespace PolymtkCp.Tests;

public class WalletInputTests
{
    [Theory]
    [InlineData("0xAbCdEf0123456789aBcDeF0123456789AbCdEf01", "0xabcdef0123456789abcdef0123456789abcdef01")]
    [InlineData("https://polymarket.com/profile/0x1111111111111111111111111111111111111111",
                "0x1111111111111111111111111111111111111111")]
    [InlineData("  some prefix 0xDEADBEEFcafeBABEdeadbeefCAFEBABEdeadbeef trailing  ",
                "0xdeadbeefcafebabedeadbeefcafebabedeadbeef")]
    public void Extracts_and_lowercases(string input, string expected) =>
        Assert.Equal(expected, WalletInput.TryExtractAddress(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address")]
    [InlineData("0x123")] // too short
    public void Returns_null_when_no_address(string? input) =>
        Assert.Null(WalletInput.TryExtractAddress(input));
}
