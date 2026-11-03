namespace Ekko.Brain.Tests;

public class ApiKeyTests
{
    private static readonly Dictionary<string, string> Keys = new() { ["deck"] = "deck-secret", ["telegram"] = "tg-secret", ["revoked"] = "" };

    [Theory]
    [InlineData("deck-secret", "deck")]
    [InlineData("tg-secret", "telegram")]
    [InlineData("Deck-secret", null)]
    [InlineData("deck-secret ", null)]
    [InlineData("", null)] // an emptied key revokes its client; it never matches an empty header
    [InlineData(null, null)]
    public void Each_client_has_its_own_exact_key(string? supplied, string? client) =>
        Assert.Equal(client, ApiKey.ClientFor(Keys, supplied));
}
