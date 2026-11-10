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

    // Resumable upload: the .aab goes up in chunks (a multiple of 256 KiB, as Google requires), each
    // with its own timeout and retries. On 2026-11-10 the single 56 MB POST stalled on the build
    // host for the whole 900 s budget twice in a row, on a 48 Mbit/s uplink that curl pushed the same
    // file through in seconds: a socket-level stall that only a fresh request gets past. Resuming
    // asks Play how much it has and sends the rest, so a stalled chunk costs ChunkTimeout, not the
    // whole run. Attempts cover both a stalled chunk and a Play 5xx.
    private const int ChunkSize = 8 * 1024 * 1024;
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(120);
    private const int ChunkAttempts = 5;

    private readonly BotConfig _config;
    private readonly ILogger<GooglePlayClient> _log;
    // No client-wide timeout: a bundle upload is bounded by GOOGLE_PLAY_UPLOAD_TIMEOUT_SECONDS,
    // every other call by CallTimeout, each through its own linked token. No auto-redirect: the
    // resumable protocol answers "308 Resume Incomplete" to a chunk, which must reach this code
    // as a response, not be chased as a redirect.
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
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
            var name = Path.GetFileName(aabPath);
            await Step($"📤 Uploading {name} ({sizeMb:0.0} MB) to Google Play").ConfigureAwait(false);
            var versionCode = await UploadBundleAsync(editId, aabPath,
                sent => Step($"📤 Uploading {name}: {sent / (1024.0 * 1024.0):0.0} / {sizeMb:0.0} MB to Google Play"),
                ct).ConfigureAwait(false);

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

    /// <summary>
    /// Uploads the bundle with Google's resumable protocol: one POST opens a session, then PUTs of
    /// ChunkSize bytes with a Content-Range each. Play answers 308 with the bytes it holds until the
    /// last chunk, which gets the bundle JSON. A chunk that stalls or fails is retried after asking
    /// Play where to resume, so a bad socket costs one chunk, not the run. <paramref name="onSent"/>
    /// gets the running byte count after each chunk.
    /// </summary>
    private async Task<long> UploadBundleAsync(string editId, string aabPath, Func<long, Task> onSent,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_config.GooglePlayUploadTimeoutSeconds));
        var budget = timeout.Token;

        JsonNode? root;
        try
        {
            await using var file = File.OpenRead(aabPath);
            var total = file.Length;
            var session = await OpenUploadSessionAsync(editId, total, budget).ConfigureAwait(false);

            var buffer = new byte[ChunkSize];
            long offset = 0;
            root = null;
            while (root is null)
            {
                file.Position = offset;
                var length = await ReadFullyAsync(file, buffer, (int)Math.Min(ChunkSize, total - offset), budget)
                    .ConfigureAwait(false);
                var (next, done) = await SendChunkAsync(session, buffer, length, offset, total, budget)
                    .ConfigureAwait(false);
                root = done;
                offset = next;
                try { await onSent(Math.Min(offset, total)).ConfigureAwait(false); }
                catch (Exception ex) { _log.LogDebug(ex, "onSent threw at {Offset}", offset); }
            }
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

    /// <summary>Opens a resumable upload session; the returned URI takes the chunks.</summary>
    private async Task<Uri> OpenUploadSessionAsync(string editId, long total, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{UploadUrl}{Esc(Package)}/edits/{Esc(editId)}/bundles?uploadType=resumable")
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", total.ToString(CultureInfo.InvariantCulture));

        using var response = await SendRawAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var detail = Describe(status, text);
            _log.LogWarning("POST {Url} failed: {Status} {Detail}", request.RequestUri, status, detail);
            throw new GooglePlayException(status, detail);
        }

        var session = response.Headers.Location;
        if (session is null)
            throw new GooglePlayException((int)response.StatusCode,
                "Google Play opened a resumable upload without a session URI (no Location header).");
        return session;
    }

    /// <summary>
    /// Sends one chunk. Returns the offset to continue from and, on the final chunk, the bundle
    /// JSON. A stalled or 5xx chunk is retried in place up to ChunkAttempts times; when Play reports
    /// it already holds part of the chunk, the new offset is returned so the caller re-reads from
    /// there instead.
    /// </summary>
    private async Task<(long Next, JsonNode? Done)> SendChunkAsync(Uri session, byte[] buffer, int length,
        long offset, long total, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ChunkTimeout);
            string failure;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, session)
                {
                    Content = new ByteArrayContent(buffer, 0, length),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, total);

                using var response = await SendRawAsync(request, timeout.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status == 308)
                    return (ReceivedUpTo(response), null);
                var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return (total, ParseJson(text, status));
                var detail = Describe(status, text);
                if (status < 500 && status != 408 && status != 429)
                {
                    _log.LogWarning("PUT chunk at {Offset} failed: {Status} {Detail}", offset, status, detail);
                    throw new GooglePlayException(status, detail);
                }
                failure = $"{status} {detail}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failure = $"no reply within {ChunkTimeout.TotalSeconds:0} s";
            }
            catch (HttpRequestException ex)
            {
                failure = ex.Message;
            }

            if (attempt >= ChunkAttempts)
                throw new GooglePlayException(408,
                    $"The bundle chunk at {offset / (1024.0 * 1024.0):0.0} MB failed {ChunkAttempts} times; last: {failure}.");

            _log.LogWarning("Chunk at {Offset} attempt {Attempt}/{Max} failed ({Failure}); asking Play where to resume.",
                offset, attempt, ChunkAttempts, failure);
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);

            var (resume, done) = await QueryResumeOffsetAsync(session, total, ct).ConfigureAwait(false);
            if (done is not null) return (total, done);
            if (resume != offset) return (resume, null);
        }
    }

    /// <summary>Asks Play how much of the upload it holds: the next offset, or the bundle if it is complete.</summary>
    private async Task<(long Next, JsonNode? Done)> QueryResumeOffsetAsync(Uri session, long total, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Put, session)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(total);

        using var response = await SendRawAsync(request, timeout.Token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status == 308) return (ReceivedUpTo(response), null);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return (total, ParseJson(text, status));
        // The session itself is gone (404/410 after a day, or a 4xx we cannot resume from).
        var detail = Describe(status, text);
        _log.LogWarning("Resume query failed: {Status} {Detail}", status, detail);
        throw new GooglePlayException(status, detail);
    }

    /// <summary>The offset after the last byte Play confirms in a 308's "Range: bytes=0-N"; 0 when it holds nothing.</summary>
    private static long ReceivedUpTo(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var values)) return 0;
        var range = values.FirstOrDefault() ?? "";
        var dash = range.LastIndexOf('-');
        return dash >= 0 && long.TryParse(range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            ? last + 1
            : 0;
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        return read;
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
        using var response = await SendRawAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var detail = Describe(status, text);
            _log.LogWarning("{Method} {Url} failed: {Status} {Detail}", request.Method.Method, request.RequestUri, status, detail);
            throw new GooglePlayException(status, detail);
        }

        return ParseJson(text, status);
    }

    /// <summary>Authorises and sends; the caller owns the response and judges its status.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct)
    {
        EnsureConfigured();
        var token = await _token.Value.GetTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The URL carries only the package name, edit id and upload session — no secrets.
        _log.LogDebug("{Method} {Url}", request.Method.Method, request.RequestUri);
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static JsonNode? ParseJson(string text, int status)
    {
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
