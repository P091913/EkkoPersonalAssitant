using System.Security.Cryptography;
using System.Text;

namespace Ekko.Brain;

/// <summary>Shared-secret check for every request. Program refuses to start without a key outside Development.</summary>
public static class ApiKey
{
    public const string Header = "X-Api-Key";

    /// <summary>No configured key means open - Development only.</summary>
    public static bool Allows(string? configured, string? supplied) =>
        string.IsNullOrEmpty(configured)
        || supplied is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(configured), Encoding.UTF8.GetBytes(supplied));
}
