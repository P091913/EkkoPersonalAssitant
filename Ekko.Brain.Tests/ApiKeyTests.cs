namespace Ekko.Brain.Tests;

public class ApiKeyTests
{
    [Theory]
    [InlineData("secret", "secret", true)]
    [InlineData("secret", "Secret", false)]
    [InlineData("secret", "secret ", false)]
    [InlineData("secret", null, false)]
    [InlineData("secret", "", false)]
    [InlineData(null, null, true)] // no key configured: open (Development only - Program refuses to start otherwise)
    [InlineData("", "anything", true)]
    public void Allows_only_the_exact_key(string? configured, string? supplied, bool allowed) =>
        Assert.Equal(allowed, ApiKey.Allows(configured, supplied));
}
