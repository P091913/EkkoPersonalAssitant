using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ekko.Budget;

/// <summary>One key per client ("brain", "deck"), so a client can be revoked alone and the log shows who did what.</summary>
public static class ApiKeys
{
    private const string ClientItem = "ekko.client";

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

    /// <summary>Which client's key this request carried, for the log.</summary>
    public static string Client(HttpContext context) => context.Items[ClientItem] as string ?? "unknown";

    /// <summary>
    /// CORS, then the key check, in that order: a browser's preflight never carries the key (DR-45). Then what the key may
    /// do (DR-46): a client in <paramref name="readOnly"/> only reads. A client in <paramref name="writes"/> reads and
    /// makes only the writes listed for it ("POST /budget/bills/paid"), never with an amount. Any other client does
    /// anything. A write that isn't listed is refused, so a route added later starts closed.
    /// </summary>
    public static void UseClientKeys(this WebApplication app, IReadOnlyDictionary<string, string> keys, string[] readOnly,
        IReadOnlyDictionary<string, string[]> writes)
    {
        // Any origin: the glasses' WebView has no fixed one. The key is a header, not a cookie, so it stays the guard.
        app.UseCors(p => p.AllowAnyOrigin().WithMethods("GET", "POST").WithHeaders("X-Api-Key", "Content-Type"));
        app.Use(async (context, next) =>
        {
            var client = keys.Count == 0 ? "dev" : ClientFor(keys, context.Request.Headers["X-Api-Key"]);
            if (client is null)
            {
                await Refuse(context, StatusCodes.Status401Unauthorized, "Missing or wrong X-Api-Key.");
                return;
            }
            context.Items[ClientItem] = client;

            var request = context.Request;
            if ((readOnly.Contains(client) || writes.ContainsKey(client)) && !HttpMethods.IsGet(request.Method))
            {
                // As sent, ordinal: another spelling of the path is refused, never let through.
                if (!writes.TryGetValue(client, out var allowed) || !allowed.Contains($"{request.Method} {request.Path}"))
                {
                    await Refuse(context, StatusCodes.Status403Forbidden,
                        allowed is null ? "This key can only read the budget." : "This key isn't allowed to make that change.");
                    return;
                }
                // Loosely matched on purpose: whatever reaches the endpoint gets this check.
                if (HttpMethods.IsPost(request.Method) && request.Path.StartsWithSegments("/budget/bills/paid", StringComparison.OrdinalIgnoreCase)
                    && await SendsAmountAsync(request))
                {
                    await Refuse(context, StatusCodes.Status403Forbidden, "This key can check a bill off, but not change what it came to. Ask Ekko for that.");
                    return;
                }
            }
            await next();
        });
    }

    private static async Task Refuse(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new Answer("error", message));
    }

    /// <summary>
    /// Whether a /bills/paid body sets an amount, read the way the endpoint binds it (same type, same JSON options), so
    /// "Amount" counts too. A body that can't be read here can't be bound there either, and gets the endpoint's 400.
    /// </summary>
    private static async Task<bool> SendsAmountAsync(HttpRequest request)
    {
        request.EnableBuffering();
        try
        {
            return (await request.ReadFromJsonAsync<PaidRequest>(request.HttpContext.RequestAborted))?.Amount is not null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }
}
