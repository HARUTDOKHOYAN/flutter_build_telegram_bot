using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BuildChatBot.Config;
using Microsoft.Extensions.Logging;
using static BuildChatBot.Apple.AscJson;

namespace BuildChatBot.Apple;

/// <summary>
/// Thin wrapper over the App Store Connect REST API v1. Registered as a singleton, so the
/// <see cref="HttpClient"/>, the JWT and the resolved app id are all created once and reused.
/// Responses are read with <see cref="JsonNode"/> + <see cref="AscJson"/> rather than a typed
/// model: ASC adds fields freely and half of them are optional.
/// </summary>
public sealed class AppStoreConnectClient : IAppStoreConnectClient
{
    private const string BaseUrl = "https://api.appstoreconnect.apple.com/v1";

    /// <summary>App Store version states that can still be edited and reused for a new submission.</summary>
    private static readonly string[] EditableStates =
    [
        "PREPARE_FOR_SUBMISSION", "DEVELOPER_REJECTED", "REJECTED", "METADATA_REJECTED", "INVALID_BINARY",
    ];

    /// <summary>States meaning "already handed to Apple" — nothing can be changed or resubmitted.</summary>
    private static readonly string[] SubmittedStates = ["WAITING_FOR_REVIEW", "IN_REVIEW"];

    private readonly BotConfig _config;
    private readonly ILogger<AppStoreConnectClient> _log;
    private readonly HttpClient _http;
    private readonly Lazy<AscJwtFactory> _jwt;
    private readonly SemaphoreSlim _appIdGate = new(1, 1);
    private string? _appId;

    public AppStoreConnectClient(BotConfig config, ILogger<AppStoreConnectClient> log)
    {
        _config = config;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BuildChatBot/1.0");
        // Built lazily: the singleton is constructed at startup even when no Apple keys are configured.
        _jwt = new Lazy<AscJwtFactory>(
            () => new AscJwtFactory(config.AppleApiKeyId!, config.AppleApiIssuerId!, config.AppleApiKeyPath!),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // ---------------------------------------------------------------- apps

    public async Task<string> GetAppIdAsync(CancellationToken ct)
    {
        if (_appId is not null) return _appId;

        EnsureConfigured();
        await _appIdGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_appId is not null) return _appId;

            var bundleId = _config.AppleBundleId!;
            var root = await SendAsync(HttpMethod.Get,
                $"/apps?filter[bundleId]={Escape(bundleId)}&limit=1", null, ct).ConfigureAwait(false);

            var first = Array(root, "data")?.FirstOrDefault();
            var id = Str(first, "id");
            if (string.IsNullOrWhiteSpace(id))
                throw new AppStoreConnectException(404,
                    $"No app with bundle id '{bundleId}' is visible to this App Store Connect API key. " +
                    "Check APPLE_BUNDLE_ID and that the key has access to the app.");

            _appId = id;
            _log.LogInformation("Resolved App Store Connect app id for {BundleId}", bundleId);
            return id;
        }
        finally
        {
            _appIdGate.Release();
        }
    }

    // -------------------------------------------------------------- builds

    public async Task<AscBuild?> FindBuildAsync(string versionString, string buildNumber, CancellationToken ct)
    {
        var appId = await GetAppIdAsync(ct).ConfigureAwait(false);
        var path =
            $"/builds?filter[app]={Escape(appId)}" +
            $"&filter[preReleaseVersion.version]={Escape(versionString)}" +
            $"&filter[version]={Escape(buildNumber)}" +
            "&include=preReleaseVersion" +
            "&fields[builds]=version,processingState,uploadedDate,expired" +
            "&fields[preReleaseVersions]=version&limit=5";

        var root = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        var data = Array(root, "data");
        if (data is null || data.Count == 0) return null;

        var versions = IndexPreReleaseVersions(root);
        return MapBuild(data[0], versions, versionString);
    }

    public async Task<IReadOnlyList<AscBuild>> ListRecentBuildsAsync(int limit, CancellationToken ct)
    {
        var appId = await GetAppIdAsync(ct).ConfigureAwait(false);
        var clamped = Math.Clamp(limit, 1, 50);
        var path =
            $"/builds?filter[app]={Escape(appId)}&filter[expired]=false&sort=-uploadedDate" +
            "&include=preReleaseVersion" +
            "&fields[builds]=version,processingState,uploadedDate,expired" +
            $"&fields[preReleaseVersions]=version&limit={clamped}";

        var root = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        var data = Array(root, "data");
        if (data is null || data.Count == 0) return [];

        var versions = IndexPreReleaseVersions(root);
        var builds = new List<AscBuild>(data.Count);
        foreach (var item in data)
        {
            var build = MapBuild(item, versions, null);
            if (build is not null) builds.Add(build);
        }
        return builds;
    }

    public async Task<AscBuild> WaitForProcessingAsync(string versionString, string buildNumber, TimeSpan timeout,
        TimeSpan pollInterval, Func<string, Task>? onPoll, CancellationToken ct)
    {
        // A sub-second interval would hammer the API; a zero/negative timeout still gets one attempt.
        var interval = pollInterval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : pollInterval;
        var deadline = DateTimeOffset.UtcNow + (timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            AscBuild? build;
            try
            {
                build = await FindBuildAsync(versionString, buildNumber, ct).ConfigureAwait(false);
            }
            catch (AppStoreConnectException ex) when (ex.StatusCode >= 500 || ex.StatusCode == 429)
            {
                // Transient server-side hiccup: keep polling until the deadline instead of failing the release.
                _log.LogWarning("Transient App Store Connect error while polling ({Status}): {Detail}",
                    ex.StatusCode, ex.Detail);
                build = null;
            }

            var state = build?.ProcessingState ?? "NOT_YET_VISIBLE";
            if (build is not null)
            {
                if (build.IsValid)
                {
                    _log.LogInformation("Build {Display} finished processing (VALID)", build.Display);
                    return build;
                }

                if (state.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("INVALID", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AppStoreConnectException(422,
                        $"Build {versionString} ({buildNumber}) processing state is {state.ToUpperInvariant()} " +
                        "— check App Store Connect for the reason email");
                }
            }

            if (onPoll is not null)
                await onPoll(state).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"App Store Connect did not finish processing build {versionString} ({buildNumber}) within " +
                    $"{timeout.TotalMinutes:0} min (last state: {state}). Processing normally takes 5–20 min; " +
                    "check App Store Connect / TestFlight and retry the submit step once the build is ready.");
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    public async Task SetExportComplianceExemptAsync(string buildId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "builds",
                ["id"] = buildId,
                ["attributes"] = new JsonObject { ["usesNonExemptEncryption"] = false },
            },
        };

        try
        {
            await SendAsync(HttpMethod.Patch, $"/builds/{Uri.EscapeDataString(buildId)}", body, ct).ConfigureAwait(false);
            _log.LogInformation("Marked build as exempt from non-exempt encryption compliance");
        }
        catch (AppStoreConnectException ex) when (ex.StatusCode is 409 or 422)
        {
            // ASC answers 409/422 when the attribute is already set (it is write-once per build).
            _log.LogInformation("Export compliance already set on the build ({Status}): {Detail}",
                ex.StatusCode, ex.Detail);
        }
    }

    // --------------------------------------------------- App Store versions

    public async Task<IReadOnlyList<AscAppStoreVersion>> ListAppStoreVersionsAsync(CancellationToken ct)
    {
        var appId = await GetAppIdAsync(ct).ConfigureAwait(false);
        var path =
            $"/apps/{Uri.EscapeDataString(appId)}/appStoreVersions?filter[platform]=IOS" +
            "&fields[appStoreVersions]=versionString,appStoreState,releaseType,createdDate&limit=50";

        var root = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        var data = Array(root, "data");
        if (data is null || data.Count == 0) return [];

        var mapped = new List<(AscAppStoreVersion Version, DateTimeOffset? Created, int Order)>(data.Count);
        for (var i = 0; i < data.Count; i++)
        {
            var version = MapVersion(data[i]);
            if (version is null) continue;
            mapped.Add((version, Date(Prop(data[i], "attributes"), "createdDate"), i));
        }

        // Newest first when ASC gave us createdDate; otherwise preserve the order the API returned.
        return mapped.Any(m => m.Created is not null)
            ? mapped.OrderByDescending(m => m.Created ?? DateTimeOffset.MinValue).ThenBy(m => m.Order)
                    .Select(m => m.Version).ToList()
            : mapped.Select(m => m.Version).ToList();
    }

    public async Task<AscAppStoreVersion> GetOrCreateEditableVersionAsync(string versionString, CancellationToken ct)
    {
        var appId = await GetAppIdAsync(ct).ConfigureAwait(false);
        var existing = await ListAppStoreVersionsAsync(ct).ConfigureAwait(false);

        var matches = existing
            .Where(v => string.Equals(v.VersionString, versionString, StringComparison.Ordinal))
            .ToList();

        var editable = matches.FirstOrDefault(v =>
            EditableStates.Contains(v.AppStoreState, StringComparer.OrdinalIgnoreCase));

        if (editable is not null)
            return await EnsureAfterApprovalAsync(editable, ct).ConfigureAwait(false);

        var submitted = matches.FirstOrDefault(v =>
            SubmittedStates.Contains(v.AppStoreState, StringComparer.OrdinalIgnoreCase));
        if (submitted is not null)
            throw new AppStoreConnectException(409,
                $"Version {versionString} is already {submitted.AppStoreState} — Apple has it. " +
                "Cancel the submission in App Store Connect or wait for the review result.");

        if (matches.Count > 0)
        {
            var states = string.Join(", ", matches.Select(m => m.AppStoreState).Distinct());
            throw new AppStoreConnectException(409,
                $"Version {versionString} already exists in App Store Connect with state {states} and cannot be " +
                "edited. The marketing version has already been used — bump it (e.g. in pubspec.yaml) and release again.");
        }

        var body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "appStoreVersions",
                ["attributes"] = new JsonObject
                {
                    ["platform"] = "IOS",
                    ["versionString"] = versionString,
                    ["releaseType"] = "AFTER_APPROVAL",
                },
                ["relationships"] = new JsonObject
                {
                    ["app"] = new JsonObject
                    {
                        ["data"] = new JsonObject { ["type"] = "apps", ["id"] = appId },
                    },
                },
            },
        };

        var root = await SendAsync(HttpMethod.Post, "/appStoreVersions", body, ct).ConfigureAwait(false);
        var created = MapVersion(Prop(root, "data"))
            ?? throw new AppStoreConnectException(500,
                $"App Store Connect accepted the new version {versionString} but returned no usable resource.");

        _log.LogInformation("Created App Store version {Version} (state {State})",
            created.VersionString, created.AppStoreState);
        return created;
    }

    public async Task AttachBuildAsync(string appStoreVersionId, string buildId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["data"] = new JsonObject { ["type"] = "builds", ["id"] = buildId },
        };

        await SendAsync(HttpMethod.Patch,
            $"/appStoreVersions/{Uri.EscapeDataString(appStoreVersionId)}/relationships/build",
            body, ct).ConfigureAwait(false);

        _log.LogInformation("Attached build to App Store version");
    }

    // ---------------------------------------------------- review submission

    public async Task<AscSubmitResult> SubmitForReviewAsync(string appStoreVersionId, CancellationToken ct)
    {
        var appId = await GetAppIdAsync(ct).ConfigureAwait(false);
        var submissionId = await GetOrCreateReviewSubmissionAsync(appId, ct).ConfigureAwait(false);

        var itemBody = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "reviewSubmissionItems",
                ["relationships"] = new JsonObject
                {
                    ["reviewSubmission"] = new JsonObject
                    {
                        ["data"] = new JsonObject { ["type"] = "reviewSubmissions", ["id"] = submissionId },
                    },
                    ["appStoreVersion"] = new JsonObject
                    {
                        ["data"] = new JsonObject { ["type"] = "appStoreVersions", ["id"] = appStoreVersionId },
                    },
                },
            },
        };

        try
        {
            await SendAsync(HttpMethod.Post, "/reviewSubmissionItems", itemBody, ct).ConfigureAwait(false);
        }
        catch (AppStoreConnectException ex) when (ex.StatusCode == 409
            || (ex.StatusCode == 422 && ex.Detail.Contains("already", StringComparison.OrdinalIgnoreCase)))
        {
            // The version is already an item of this submission — that is exactly what we wanted.
            _log.LogInformation("App Store version is already part of review submission {Id} ({Status})",
                submissionId, ex.StatusCode);
        }

        var submitBody = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "reviewSubmissions",
                ["id"] = submissionId,
                ["attributes"] = new JsonObject { ["submitted"] = true },
            },
        };

        await SendAsync(HttpMethod.Patch, $"/reviewSubmissions/{Uri.EscapeDataString(submissionId)}",
            submitBody, ct).ConfigureAwait(false);

        _log.LogInformation("Review submission {Id} submitted", submissionId);
        return new AscSubmitResult(true, submissionId, "Submitted for review; release type AFTER_APPROVAL");
    }

    private async Task<string> GetOrCreateReviewSubmissionAsync(string appId, CancellationToken ct)
    {
        var openPath =
            $"/apps/{Uri.EscapeDataString(appId)}/reviewSubmissions" +
            "?filter[platform]=IOS&filter[state]=READY_FOR_REVIEW&limit=1";

        var open = await SendAsync(HttpMethod.Get, openPath, null, ct).ConfigureAwait(false);
        var existingId = Str(Array(open, "data")?.FirstOrDefault(), "id");
        if (!string.IsNullOrWhiteSpace(existingId))
        {
            _log.LogInformation("Reusing open review submission {Id}", existingId);
            return existingId;
        }

        var body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "reviewSubmissions",
                ["attributes"] = new JsonObject { ["platform"] = "IOS" },
                ["relationships"] = new JsonObject
                {
                    ["app"] = new JsonObject
                    {
                        ["data"] = new JsonObject { ["type"] = "apps", ["id"] = appId },
                    },
                },
            },
        };

        var created = await SendAsync(HttpMethod.Post, "/reviewSubmissions", body, ct).ConfigureAwait(false);
        var id = Str(Prop(created, "data"), "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new AppStoreConnectException(500,
                "App Store Connect created a review submission but returned no id.");

        _log.LogInformation("Created review submission {Id}", id);
        return id;
    }

    // --------------------------------------------------------------- mapping

    private static Dictionary<string, string> IndexPreReleaseVersions(JsonNode? root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var included = Array(root, "included");
        if (included is null) return map;

        foreach (var item in included)
        {
            if (!string.Equals(Str(item, "type"), "preReleaseVersions", StringComparison.Ordinal)) continue;
            var id = Str(item, "id");
            var version = Str(Prop(item, "attributes"), "version");
            if (id is not null && version is not null) map[id] = version;
        }
        return map;
    }

    private static AscBuild? MapBuild(JsonNode? item, Dictionary<string, string> versions, string? fallbackVersion)
    {
        var id = Str(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var attributes = Prop(item, "attributes");
        var preReleaseId = Str(Path(item, "relationships", "preReleaseVersion", "data"), "id");

        var versionString =
            (preReleaseId is not null && versions.TryGetValue(preReleaseId, out var v) ? v : null)
            ?? fallbackVersion
            ?? "?";

        return new AscBuild(
            id,
            versionString,
            Str(attributes, "version") ?? "?",
            Str(attributes, "processingState") ?? "UNKNOWN",
            Date(attributes, "uploadedDate"),
            Bool(attributes, "expired"));
    }

    private static AscAppStoreVersion? MapVersion(JsonNode? item)
    {
        var id = Str(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var attributes = Prop(item, "attributes");
        return new AscAppStoreVersion(
            id,
            Str(attributes, "versionString") ?? "?",
            Str(attributes, "appStoreState") ?? "UNKNOWN",
            Str(attributes, "releaseType"));
    }

    private async Task<AscAppStoreVersion> EnsureAfterApprovalAsync(AscAppStoreVersion version, CancellationToken ct)
    {
        if (string.Equals(version.ReleaseType, "AFTER_APPROVAL", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogInformation("Reusing editable App Store version {Version} (state {State})",
                version.VersionString, version.AppStoreState);
            return version;
        }

        var body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "appStoreVersions",
                ["id"] = version.Id,
                ["attributes"] = new JsonObject { ["releaseType"] = "AFTER_APPROVAL" },
            },
        };

        var root = await SendAsync(HttpMethod.Patch,
            $"/appStoreVersions/{Uri.EscapeDataString(version.Id)}", body, ct).ConfigureAwait(false);

        _log.LogInformation("Reusing editable App Store version {Version} (state {State}); releaseType {Old} -> AFTER_APPROVAL",
            version.VersionString, version.AppStoreState, version.ReleaseType ?? "(none)");

        return MapVersion(Prop(root, "data")) ?? version with { ReleaseType = "AFTER_APPROVAL" };
    }

    // ---------------------------------------------------------------- plumbing

    private void EnsureConfigured()
    {
        if (_config.AppleConfigured) return;
        throw new InvalidOperationException(
            "App Store Connect is not configured. Add these keys to env.local: "
            + string.Join(", ", _config.MissingAppleKeys()));
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    /// <summary>
    /// Issue one authenticated request. Returns the parsed body, or null for empty bodies (204).
    /// Non-2xx responses become <see cref="AppStoreConnectException"/> carrying ASC's own error text.
    /// A 429 is retried once after <c>Retry-After</c>.
    /// </summary>
    private async Task<JsonNode?> SendAsync(HttpMethod method, string pathAndQuery, JsonNode? body, CancellationToken ct)
    {
        EnsureConfigured();

        var attempt = 0;
        while (true)
        {
            attempt++;
            _log.LogDebug("{Method} {Path}", method.Method, pathAndQuery);

            using var request = new HttpRequestMessage(method, BaseUrl + pathAndQuery);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt.Value.GetToken());
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status == (int)HttpStatusCode.TooManyRequests && attempt == 1)
            {
                var wait = RetryAfter(response);
                _log.LogWarning("{Method} {Path} rate limited (429); retrying in {Seconds}s",
                    method.Method, pathAndQuery, wait.TotalSeconds);
                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = DescribeErrors(text, response);
                _log.LogWarning("{Method} {Path} failed: {Status} {Detail}", method.Method, pathAndQuery, status, detail);
                throw new AppStoreConnectException(status, detail);
            }

            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                return JsonNode.Parse(text);
            }
            catch (Exception ex)
            {
                throw new AppStoreConnectException(status,
                    $"App Store Connect returned a body that is not valid JSON: {ex.Message}");
            }
        }
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
        }
        return TimeSpan.FromSeconds(10);
    }

    /// <summary>Flatten ASC's <c>errors[]</c> payload into one user-presentable line (or lines).</summary>
    private static string DescribeErrors(string body, HttpResponseMessage response)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var errors = Array(JsonNode.Parse(body), "errors");
                if (errors is not null && errors.Count > 0)
                {
                    var parts = new List<string>(errors.Count);
                    var httpStatus = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                    foreach (var error in errors)
                    {
                        var errorStatus = Str(error, "status");
                        var code = Str(error, "code");
                        var title = Str(error, "title");
                        var detail = Str(error, "detail");
                        var pieces = new List<string>(4);
                        // The HTTP status is already on the exception — only repeat a per-error status when it differs.
                        if (!string.IsNullOrWhiteSpace(errorStatus) && errorStatus != httpStatus) pieces.Add(errorStatus);
                        if (!string.IsNullOrWhiteSpace(code)) pieces.Add(code);
                        if (!string.IsNullOrWhiteSpace(title)) pieces.Add(title);
                        if (!string.IsNullOrWhiteSpace(detail) && detail != title) pieces.Add(detail);
                        if (pieces.Count > 0) parts.Add(string.Join(" — ", pieces));
                    }
                    if (parts.Count > 0) return string.Join("\n", parts);
                }
            }
            catch
            {
                // Not the documented error envelope — fall through to the raw body.
            }

            var raw = body.Trim();
            return raw.Length > 800 ? raw[..800] + "…" : raw;
        }

        return $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
    }
}
