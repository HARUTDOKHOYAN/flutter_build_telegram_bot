using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BuildChatBot.Apple;

/// <summary>
/// Mints the ES256 JSON Web Tokens App Store Connect expects
/// (<c>Authorization: Bearer &lt;jwt&gt;</c>), signed with the <c>AuthKey_XXXX.p8</c>
/// private key downloaded from App Store Connect.
/// Tokens live 15 minutes (Apple's maximum is 20) and are cached until 2 minutes before expiry.
/// Neither the key material nor the token is ever logged.
/// </summary>
internal sealed class AscJwtFactory(string keyId, string issuerId, string keyPath)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private string? _cached;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    /// <summary>A valid bearer token, reusing the cached one while more than 2 minutes remain.</summary>
    public string GetToken()
    {
        lock (_gate)
        {
            if (_cached is not null && DateTimeOffset.UtcNow < _expiresAt - RefreshMargin)
                return _cached;

            var issuedAt = DateTimeOffset.UtcNow;
            var expiresAt = issuedAt + Lifetime;
            _cached = Mint(issuedAt, expiresAt);
            _expiresAt = expiresAt;
            return _cached;
        }
    }

    private string Mint(DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = "ES256",
            ["kid"] = keyId,
            ["typ"] = "JWT",
        });

        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = issuerId,
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["aud"] = "appstoreconnect-v1",
        });

        var signingInput = $"{Base64Url(header)}.{Base64Url(payload)}";

        using var ecdsa = LoadKey();
        // SignData's default signature format is IeeeP1363FixedFieldConcatenation (r||s),
        // which is exactly what JWS ES256 requires — no DER unwrapping needed.
        var signature = ecdsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private ECDsa LoadKey()
    {
        string pem;
        try
        {
            pem = File.ReadAllText(keyPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot read the App Store Connect private key at '{keyPath}': {ex.Message}", ex);
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
        }
        catch (Exception ex)
        {
            ecdsa.Dispose();
            throw new InvalidOperationException(
                $"'{keyPath}' is not a valid PKCS#8 PEM private key (expected the AuthKey_XXXX.p8 file " +
                $"downloaded from App Store Connect): {ex.Message}", ex);
        }

        return ecdsa;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
