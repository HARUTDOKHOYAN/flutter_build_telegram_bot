using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BuildChatBot.GooglePlay;

/// <summary>
/// OAuth 2.0 access tokens for a Google service account (the JSON key downloaded from Google Cloud),
/// using the JWT-bearer grant: an RS256-signed assertion is exchanged at the key's <c>token_uri</c>.
/// Done by hand instead of pulling in Google.Apis.* — two HTTP calls do not justify the dependency tree.
/// Tokens are cached until 5 minutes before expiry. Neither the key nor the token is ever logged.
/// </summary>
internal sealed class GoogleServiceAccountToken(string keyPath, string scope, HttpClient http)
{
    private static readonly TimeSpan AssertionLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ServiceAccountKey? _key;
    private string? _cached;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    /// <summary>The service account's e-mail — what must be invited in Play Console.</summary>
    public string ClientEmail => Key.ClientEmail;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow < _expiresAt - RefreshMargin) return _cached;

            var key = Key;
            using var request = new HttpRequestMessage(HttpMethod.Post, key.TokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = MintAssertion(key),
                }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // e.g. {"error":"invalid_grant","error_description":"Invalid JWT Signature."} — a revoked key.
                string detail;
                try
                {
                    var node = JsonNode.Parse(text);
                    detail = $"{node?["error"]}: {node?["error_description"]}";
                }
                catch
                {
                    detail = text;
                }
                throw new GooglePlayException((int)response.StatusCode,
                    $"Google refused the service account key ({detail}). The key may be deleted or disabled " +
                    "in Google Cloud → IAM → Service accounts → Keys; create a new JSON key.");
            }

            var root = JsonNode.Parse(text);
            var token = root?["access_token"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(token))
                throw new GooglePlayException((int)response.StatusCode, "The token endpoint returned no access_token.");

            var expiresIn = root?["expires_in"]?.GetValue<int>() ?? 3600;
            _cached = token;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ServiceAccountKey Key => _key ??= ServiceAccountKey.Load(keyPath);

    private string MintAssertion(ServiceAccountKey key)
    {
        var now = DateTimeOffset.UtcNow;
        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["kid"] = key.PrivateKeyId,
        });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = key.ClientEmail,
            ["scope"] = scope,
            ["aud"] = key.TokenUri,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + AssertionLifetime).ToUnixTimeSeconds(),
        });

        var signingInput = $"{Base64Url(header)}.{Base64Url(payload)}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKeyPem);
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record ServiceAccountKey(string ClientEmail, string PrivateKeyId, string PrivateKeyPem, string TokenUri)
    {
        public static ServiceAccountKey Load(string path)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Cannot read the Google service account key at '{path}': {ex.Message}", ex);
            }

            string? Field(string name) => root?[name]?.GetValue<string>();

            if (Field("type") != "service_account")
                throw new InvalidOperationException(
                    $"'{path}' is not a service account key (expected \"type\": \"service_account\"). " +
                    "Download it from Google Cloud → IAM → Service accounts → Keys → Add key → JSON.");

            var email = Field("client_email");
            var pem = Field("private_key");
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pem))
                throw new InvalidOperationException($"'{path}' has no client_email or private_key.");

            return new ServiceAccountKey(email, Field("private_key_id") ?? "", pem,
                Field("token_uri") ?? "https://oauth2.googleapis.com/token");
        }
    }
}
