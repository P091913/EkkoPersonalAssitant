using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ekko.Budget;

public sealed class SheetOptions
{
    public string SpreadsheetId { get; init; } = "";

    /// <summary>The service account's JSON key. The sheet is shared with that account's email, so it can open nothing else.</summary>
    public string KeyFile { get; init; } = "";
}

/// <summary>A Google failure, already worded for the user.</summary>
public sealed class SheetsException(string message) : Exception(message);

/// <summary>
/// Google Sheets over raw HTTP. The service account signs its own short-lived token request (a JWT, RFC 7523), so
/// there's no browser sign-in and no Google SDK. Values are read and written as if typed.
/// </summary>
public sealed class GoogleSheets(IHttpClientFactory http, SheetOptions options, ILogger<GoogleSheets> log)
{
    private const string Api = "https://sheets.googleapis.com/v4/spreadsheets/";
    private const string Scope = "https://www.googleapis.com/auth/spreadsheets";
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private (string Value, DateTimeOffset Expires)? _token;

    public async Task<List<(string Title, long Id)>> TabsAsync(CancellationToken ct)
    {
        var body = await CallAsync(HttpMethod.Get, "?fields=sheets.properties(title,sheetId)", null, ct);
        // Google leaves zeros out of its answers: no sheetId means 0, the first tab.
        return (body?["sheets"] as JsonArray ?? [])
            .Select(s => (Title: s?["properties"]?["title"]?.GetValue<string>() ?? "", Id: s?["properties"]?["sheetId"]?.GetValue<long>() ?? 0))
            .Where(t => t.Title.Length > 0).ToList();
    }

    /// <summary>Numbers as numbers, checkboxes as true/false, dates as shown ("11/15/2026"); or each cell's formula.</summary>
    public async Task<List<List<JsonNode?>>> ReadAsync(string range, bool formulas, CancellationToken ct)
    {
        var render = formulas ? "FORMULA" : "UNFORMATTED_VALUE";
        var body = await CallAsync(HttpMethod.Get, $"/values/{Uri.EscapeDataString(range)}?valueRenderOption={render}&dateTimeRenderOption=FORMATTED_STRING", null, ct);
        return (body?["values"] as JsonArray ?? []).Select(row => (row as JsonArray ?? []).Select(c => c?.DeepClone()).ToList()).ToList();
    }

    /// <summary>Structural changes (insert a row, add a checkbox rule). Google applies all of them or none.</summary>
    public Task BatchUpdateAsync(IEnumerable<object> requests, CancellationToken ct) =>
        CallAsync(HttpMethod.Post, ":batchUpdate", new { requests = requests.ToArray() }, ct);

    public Task WriteAsync(IEnumerable<CellWrite> cells, CancellationToken ct) =>
        CallAsync(HttpMethod.Post, "/values:batchUpdate",
            new { valueInputOption = "USER_ENTERED", data = cells.Select(c => new { range = c.Range, values = new[] { new[] { c.Value } } }).ToArray() }, ct);

    private async Task<JsonNode?> CallAsync(HttpMethod method, string path, object? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Api + options.SpreadsheetId + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        if (json is not null)
            request.Content = JsonContent.Create(json);
        using var response = await Send(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return text.Length == 0 ? null : JsonNode.Parse(text);
        // Google's error body says what's wrong (not shared, API off) and holds nothing secret.
        log.LogWarning("Sheets {Method} {Path} -> {Status}: {Body}", method, path.Split('?')[0], (int)response.StatusCode, text.Length > 300 ? text[..300] : text);
        throw response.StatusCode switch
        {
            HttpStatusCode.Forbidden when text.Contains("SERVICE_DISABLED") => new SheetsException("The Google Sheets API is turned off for the service account's project."),
            HttpStatusCode.Forbidden => new SheetsException("The expenses sheet isn't shared with the service account."),
            HttpStatusCode.NotFound => new SheetsException("There's no spreadsheet with that ID."),
            _ => new SheetsException($"Google Sheets answered HTTP {(int)response.StatusCode}."),
        };
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is { } held && held.Expires > DateTimeOffset.UtcNow.AddMinutes(2))
                return held.Value;
            if (!File.Exists(options.KeyFile))
                throw new SheetsException("The service account key file is missing.");
            var key = JsonNode.Parse(await File.ReadAllTextAsync(options.KeyFile, ct))!;
            var tokenUri = key["token_uri"]?.GetValue<string>() ?? "https://oauth2.googleapis.com/token";
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var unsigned = $"{Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }))}." +
                           Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new
                           {
                               iss = key["client_email"]?.GetValue<string>(), scope = Scope, aud = tokenUri, iat = now, exp = now + 3600,
                           }));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(key["private_key"]?.GetValue<string>());
            var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = $"{unsigned}.{Base64Url.EncodeToString(signature)}",
                }),
            };
            using var response = await Send(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Google refused the service account's token request: HTTP {Status}", (int)response.StatusCode);
                throw new SheetsException("Google refused the service account's key. It may have been deleted.");
            }
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            _token = (body["access_token"]!.GetValue<string>(), DateTimeOffset.UtcNow.AddSeconds(body["expires_in"]?.GetValue<double>() ?? 3600));
            return _token.Value.Value;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await http.CreateClient("sheets").SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new SheetsException("I couldn't reach Google Sheets.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SheetsException("Google Sheets took too long to answer.");
        }
    }
}
