using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BuildChatBot.Config;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.GooglePlay;

/// <summary>
/// Google Play Developer API v3 client for the one thing the bot needs: put a new .aab on a testing
/// track. Registered as a singleton; the key file is read lazily, so the bot boots without Play keys.
/// </summary>
public sealed class GooglePlayClient : IGooglePlayClient
{
    private const string BaseUrl = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/";
    private const string UploadUrl = "https://androidpublisher.googleapis.com/upload/androidpublisher/v3/applications/";
    private const string Scope = "https://www.googleapis.com/auth/androidpublisher";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(90);

    private readonly BotConfig _config;
    private readonly ILogger<GooglePlayClient> _log;
    // No client-wide timeout: a bundle upload is bounded by GOOGLE_PLAY_UPLOAD_TIMEOUT_SECONDS,
    // every other call by CallTimeout, each through its own linked token.
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Lazy<GoogleServiceAccountToken> _token;

    public GooglePlayClient(BotConfig config, ILogger<GooglePlayClient> log)
    {
        _config = config;
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BuildChatBot/1.0");
        _token = new Lazy<GoogleServiceAccountToken>(
            () => new GoogleServiceAccountToken(config.GooglePlayServiceAccountJson!, Scope, _http),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private string Package => _config.GooglePlayPackageName;

    public async Task CheckAccessAsync(CancellationToken ct)
    {
        var editId = await InsertEditAsync(ct).ConfigureAwait(false);
        await DeleteEditAsync(editId, ct).ConfigureAwait(false);
    }

    public async Task<PlayUploadResult> UploadToTrackAsync(string aabPath, string track, string releaseName,
        Func<string, Task>? onStep, CancellationToken ct)
    {
        async Task Step(string text)
        {
            if (onStep is null) return;
            try { await onStep(text).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "onStep threw for {Step}", text); }
        }

        var editId = await InsertEditAsync(ct).ConfigureAwait(false);
        var committed = false;
        try
        {
            var sizeMb = new FileInfo(aabPath).Length / (1024.0 * 1024.0);
            await Step($"📤 Uploading {Path.GetFileName(aabPath)} ({sizeMb:0.0} MB) to Google Play").ConfigureAwait(false);
            var versionCode = await UploadBundleAsync(editId, aabPath, ct).ConfigureAwait(false);

            await Step($"🏷 Setting the {track} track to {releaseName}").ConfigureAwait(false);
            var status = "completed";
            try
            {
                await UpdateTrackAsync(editId, track, releaseName, versionCode, status, ct).ConfigureAwait(false);
                await Step("📮 Committing the Play edit").ConfigureAwait(false);
                await CommitAsync(editId, ct).ConfigureAwait(false);
            }
            catch (GooglePlayException ex) when (ex.Detail.Contains("draft app", StringComparison.OrdinalIgnoreCase))
            {
                // "Only releases with status draft may be created on draft app." — the app was never
                // published anywhere, so Play takes the bundle only as a draft the owner rolls out by hand once.
                // Play reports it on the track update or on the commit, so both sit inside this try.
                _log.LogWarning("Play refused a completed release on a draft app; retrying as a draft release.");
                status = "draft";
                await UpdateTrackAsync(editId, track, releaseName, versionCode, status, ct).ConfigureAwait(false);
                await CommitAsync(editId, ct).ConfigureAwait(false);
            }

            committed = true;
            return new PlayUploadResult(versionCode, track, status);
        }
        finally
        {
            // An uncommitted edit would linger for a week and block nothing, but leave no litter anyway.
            if (!committed)
            {
                try { await DeleteEditAsync(editId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogDebug(ex, "Could not delete Play edit {Edit}", editId); }
            }
        }
    }

    // ------------------------------------------------------------------------------- edits

    private async Task<string> InsertEditAsync(CancellationToken ct)
    {
        var root = await SendJsonAsync(HttpMethod.Post, $"{BaseUrl}{Esc(Package)}/edits", new JsonObject(), ct)
            .ConfigureAwait(false);
        var id = root?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
            throw new GooglePlayException(200, "Google Play created an edit without an id.");
        return id;
    }

    private Task DeleteEditAsync(string editId, CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Delete, $"{BaseUrl}{Esc(Package)}/edits/{Esc(editId)}", null, ct);

    private async Task<long> UploadBundleAsync(string editId, string aabPath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.GooglePlayUploadTimeoutSeconds));

        await using var file = File.OpenRead(aabPath);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{UploadUrl}{Esc(Package)}/edits/{Esc(editId)}/bundles?uploadType=media")
        {
            Content = new StreamContent(file),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        JsonNode? root;
        try
        {
            root = await SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GooglePlayException(408,
                $"The bundle upload did not finish within {_config.GooglePlayUploadTimeoutSeconds} s " +
                "(GOOGLE_PLAY_UPLOAD_TIMEOUT_SECONDS).");
        }

        var code = root?["versionCode"];
        if (code is null)
            throw new GooglePlayException(200, "Google Play accepted the bundle but returned no versionCode.");
        return code.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? long.Parse(code.GetValue<string>(), CultureInfo.InvariantCulture)
            : code.GetValue<long>();
    }

    private Task UpdateTrackAsync(string editId, string track, string releaseName, long versionCode, string status,
        CancellationToken ct)
    {
        // Replaces the track's releases with this one — the usual "newest build wins" for a testing track.
        var body = new JsonObject
        {
            ["track"] = track,
            ["releases"] = new JsonArray(new JsonObject
            {
                ["name"] = releaseName,
                ["versionCodes"] = new JsonArray(versionCode.ToString(CultureInfo.InvariantCulture)),
                ["status"] = status,
            }),
        };
        return SendJsonAsync(HttpMethod.Put,
            $"{BaseUrl}{Esc(Package)}/edits/{Esc(editId)}/tracks/{Esc(track)}", body, ct);
    }

    private async Task CommitAsync(string editId, CancellationToken ct)
    {
        var url = $"{BaseUrl}{Esc(Package)}/edits/{Esc(editId)}:commit";
        try
        {
            await SendJsonAsync(HttpMethod.Post, url, null, ct).ConfigureAwait(false);
        }
        catch (GooglePlayException ex) when (ex.Detail.Contains("changesNotSentForReview", StringComparison.Ordinal))
        {
            // Play asks for this flag when the app has other changes waiting (e.g. after a rejection).
            // It only stops the commit from sending them to review; the testing release still goes out.
            _log.LogInformation("Play asked for changesNotSentForReview=true; committing again with it.");
            await SendJsonAsync(HttpMethod.Post, url + "?changesNotSentForReview=true", null, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------- http

    private async Task<JsonNode?> SendJsonAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);

        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendAsync(request, timeout.Token).ConfigureAwait(false);
    }

    private async Task<JsonNode?> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        EnsureConfigured();
        var token = await _token.Value.GetTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The URL carries only the package name and edit id — no secrets.
        _log.LogDebug("{Method} {Url}", request.Method.Method, request.RequestUri);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var detail = Describe(status, text);
            _log.LogWarning("{Method} {Url} failed: {Status} {Detail}", request.Method.Method, request.RequestUri, status, detail);
            throw new GooglePlayException(status, detail);
        }

        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (Exception ex)
        {
            throw new GooglePlayException(status, $"Google Play returned a body that is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Google's own message, plus the fix for the errors a first setup always hits.</summary>
    private string Describe(int status, string body)
    {
        string message;
        try
        {
            message = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body;
        }
        catch
        {
            message = body;
        }

        string? hint = null;
        if (status == (int)HttpStatusCode.NotFound
            || message.Contains("Package not found", StringComparison.OrdinalIgnoreCase))
        {
            hint = $"Play has no app '{Package}' visible to this key. Check GOOGLE_PLAY_PACKAGE_NAME, " +
                   "and that the app exists in Play Console with at least one .aab uploaded by hand " +
                   "(the API cannot make the very first upload).";
        }
        else if (status == (int)HttpStatusCode.Forbidden || status == (int)HttpStatusCode.Unauthorized)
        {
            var email = SafeClientEmail();
            hint = message.Contains("has not been used", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("is disabled", StringComparison.OrdinalIgnoreCase)
                ? "Enable \"Google Play Android Developer API\" in the Google Cloud project of the service account."
                : $"In Play Console → Users and permissions, invite {email} and give it " +
                  "\"Release apps to testing tracks\" for this app. New permissions can take a few minutes to apply.";
        }

        return hint is null ? message : $"{message}\n→ {hint}";
    }

    private string SafeClientEmail()
    {
        try { return _token.Value.ClientEmail; }
        catch { return "the service account's client_email"; }
    }

    private void EnsureConfigured()
    {
        if (!_config.GooglePlayConfigured)
            throw new InvalidOperationException(
                "Google Play is not configured. Missing: " + string.Join(", ", _config.MissingGooglePlayKeys()));
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);
}
