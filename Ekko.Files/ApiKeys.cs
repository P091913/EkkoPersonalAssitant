using System.Security.Cryptography;
using System.Text;

namespace Ekko.Files;

/// <summary>One key per client ("telegram", "deck", "desk"), so a client can be revoked alone and each file records who sent it.</summary>
public static class ApiKeys
{
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
