using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CamposDev.Tuya;

/// <summary>
/// Singleton que gerencia o ciclo de vida do token Tuya (obtenção e renovação).
/// Deve ser registrado como singleton no DI para preservar o token entre execuções.
/// </summary>
public class TuyaTokenService(TuyaApiOptions options, ILogger<TuyaTokenService> logger)
{
    private readonly HttpClient _http = new();
    private TuyaToken? _token;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public async Task<string> GetValidAccessTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_token is not null && _token.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
                return _token.AccessToken;

            if (_token?.RefreshToken is not null)
            {
                logger.LogInformation("Tuya: renovando access token via refresh_token...");
                await RefreshTokenAsync(_token.RefreshToken, ct);
            }
            else
            {
                logger.LogInformation("Tuya: obtendo novo access token...");
                await FetchNewTokenAsync(ct);
            }

            return _token!.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Token fetch / refresh
    // -------------------------------------------------------------------------

    private async Task FetchNewTokenAsync(CancellationToken ct)
    {
        const string path = "/v1.0/token?grant_type=1";
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sign = ComputeTokenSign(t, path);

        var request = new HttpRequestMessage(HttpMethod.Get, options.Endpoint + path);
        AddHeaders(request, t, sign, accessToken: "");

        var response = await _http.SendAsync(request, ct);
        _token = await ParseTokenAsync(response, ct);
    }

    private async Task RefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        var path = $"/v1.0/token/{refreshToken}";
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sign = ComputeTokenSign(t, path);

        var request = new HttpRequestMessage(HttpMethod.Get, options.Endpoint + path);
        AddHeaders(request, t, sign, accessToken: "");

        var response = await _http.SendAsync(request, ct);
        _token = await ParseTokenAsync(response, ct);
    }

    private async Task<TuyaToken> ParseTokenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.GetProperty("success").GetBoolean())
        {
            var msg = doc.RootElement.GetProperty("msg").GetString();
            throw new InvalidOperationException($"Tuya token error: {msg}");
        }

        var result = doc.RootElement.GetProperty("result");
        return new TuyaToken(
            AccessToken:  result.GetProperty("access_token").GetString()!,
            RefreshToken: result.GetProperty("refresh_token").GetString()!,
            ExpiresAt:    DateTimeOffset.UtcNow.AddSeconds(
                              result.GetProperty("expire_time").GetInt64()));
    }

    // -------------------------------------------------------------------------
    // Signature helpers (públicos para uso nos jobs)
    // -------------------------------------------------------------------------

    public string ComputeRequestSign(HttpMethod method, string path, string accessToken, long t)
    {
        const string emptySha256 = "e3b0c44298fc1c149afbf4c8996fb924" +
                                   "27ae41e4649b934ca495991b7852b855";
        var stringToSign = $"{method.Method.ToUpper()}\n{emptySha256}\n\n{path}";
        var str = $"{options.AccessId}{accessToken}{t}{stringToSign}";
        return HmacSha256(str, options.AccessSecret);
    }

    public void AddHeaders(HttpRequestMessage request, long t, string sign, string accessToken)
    {
        request.Headers.TryAddWithoutValidation("client_id", options.AccessId);
        request.Headers.TryAddWithoutValidation("sign", sign);
        request.Headers.TryAddWithoutValidation("sign_method", "HMAC-SHA256");
        request.Headers.TryAddWithoutValidation("t", t.ToString());

        if (!string.IsNullOrEmpty(accessToken))
            request.Headers.TryAddWithoutValidation("access_token", accessToken);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private string ComputeTokenSign(long t, string path)
    {
        const string emptySha256 = "e3b0c44298fc1c149afbf4c8996fb924" +
                                   "27ae41e4649b934ca495991b7852b855";
        var stringToSign = $"GET\n{emptySha256}\n\n{path}";
        var str = $"{options.AccessId}{t}{stringToSign}";
        return HmacSha256(str, options.AccessSecret);
    }

    private static string HmacSha256(string data, string secret)
    {
        var keyBytes  = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes(data);
        return Convert.ToHexString(HMACSHA256.HashData(keyBytes, dataBytes));
    }
}
