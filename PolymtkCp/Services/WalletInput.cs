using System.Text.RegularExpressions;

namespace PolymtkCp.Services;

/// <summary>
/// Helpers for accepting either a raw 0x wallet address or a Polymarket
/// profile URL like https://polymarket.com/profile/0xabc...
/// </summary>
public static partial class WalletInput
{
    [GeneratedRegex(@"0x[a-fA-F0-9]{40}", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRegex();

    /// <summary>
    /// Extracts the 0x address from any input string, or returns null if none found.
    /// Always returns the address lowercased.
    /// </summary>
    public static string? TryExtractAddress(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var match = AddressRegex().Match(input);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }
}
