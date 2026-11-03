using System.Security.Cryptography;
using System.Text;

namespace Ekko.Brain;

/// <summary>
/// One key per client ("deck", "telegram"), so a client can be revoked alone (DR-22, DR-37). Program refuses to start
/// without a key outside Development.
/// </summary>
public static class ApiKey
{
    public const string Header = "X-Api-Key";

    /// <summary>The client the key belongs to, or null. Every key is compared in constant time, even after a match.</summary>
    public static string? ClientFor(IReadOnlyDictionary<string, string> keys, string? supplied)
    {
        if (string.IsNullOrEmpty(supplied))
            return null;
        var bytes = Encoding.UTF8.GetBytes(supplied);
        string? client = null;
        foreach (var (name, key) in keys)
            if (key.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), bytes))
                client = name;
        return client;
    }
}
