namespace BuildChatBot.Apple;

/// <summary>A build as App Store Connect sees it.</summary>
/// <param name="Id">ASC resource id (opaque).</param>
/// <param name="VersionString">Marketing version, e.g. "1.0.5" (preReleaseVersion.version).</param>
/// <param name="BuildNumber">CFBundleVersion, e.g. "13" (builds.version).</param>
/// <param name="ProcessingState">PROCESSING | FAILED | INVALID | VALID.</param>
public sealed record AscBuild(
    string Id,
    string VersionString,
    string BuildNumber,
    string ProcessingState,
    DateTimeOffset? UploadedAt,
    bool Expired)
{
    public bool IsValid => ProcessingState.Equals("VALID", StringComparison.OrdinalIgnoreCase);
    public string Display => $"{VersionString} ({BuildNumber})";
}

/// <summary>An App Store version (the thing you submit for review).</summary>
/// <param name="AppStoreState">e.g. PREPARE_FOR_SUBMISSION, WAITING_FOR_REVIEW, IN_REVIEW, READY_FOR_SALE, ...</param>
public sealed record AscAppStoreVersion(string Id, string VersionString, string AppStoreState, string? ReleaseType);

/// <summary>Result of submitting a version for review.</summary>
public sealed record AscSubmitResult(bool Success, string ReviewSubmissionId, string Message);

/// <summary>Thrown when App Store Connect answers with an error payload; <see cref="Detail"/> is user-presentable.</summary>
public sealed class AppStoreConnectException : Exception
{
    public int StatusCode { get; }
    public string Detail { get; }
    public AppStoreConnectException(int statusCode, string detail) : base($"App Store Connect {statusCode}: {detail}")
    {
        StatusCode = statusCode;
        Detail = detail;
    }
}

/// <summary>
/// Thin typed wrapper over the App Store Connect REST API (https://api.appstoreconnect.apple.com/v1),
/// authenticated with an ES256 JWT built from the .p8 key in <c>BotConfig</c>.
/// All methods throw <see cref="AppStoreConnectException"/> on non-2xx responses.
/// </summary>
public interface IAppStoreConnectClient
{
    /// <summary>Resolve the ASC app id for <c>BotConfig.AppleBundleId</c>. Cached after first call.</summary>
    Task<string> GetAppIdAsync(CancellationToken ct);

    /// <summary>Find one build by marketing version + build number. Null when ASC has not seen it yet.</summary>
    Task<AscBuild?> FindBuildAsync(string versionString, string buildNumber, CancellationToken ct);

    /// <summary>Most recent builds (any processing state), newest first. Excludes expired builds.</summary>
    Task<IReadOnlyList<AscBuild>> ListRecentBuildsAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Poll until the build reaches VALID (returns it), or FAILED/INVALID (throws), or the timeout elapses (throws TimeoutException).
    /// <paramref name="onPoll"/> is invoked after every poll with the current state (or "NOT_YET_VISIBLE").
    /// </summary>
    Task<AscBuild> WaitForProcessingAsync(string versionString, string buildNumber, TimeSpan timeout, TimeSpan pollInterval,
        Func<string, Task>? onPoll, CancellationToken ct);

    /// <summary>Set usesNonExemptEncryption=false on a build so TestFlight does not stall on "Missing Compliance".</summary>
    Task SetExportComplianceExemptAsync(string buildId, CancellationToken ct);

    /// <summary>All App Store versions for the IOS platform, newest first.</summary>
    Task<IReadOnlyList<AscAppStoreVersion>> ListAppStoreVersionsAsync(CancellationToken ct);

    /// <summary>
    /// Return the editable IOS version with this versionString (state PREPARE_FOR_SUBMISSION / DEVELOPER_REJECTED / REJECTED etc.),
    /// or create a new one. Always ensures releaseType == AFTER_APPROVAL (automatic release).
    /// </summary>
    Task<AscAppStoreVersion> GetOrCreateEditableVersionAsync(string versionString, CancellationToken ct);

    /// <summary>Attach a processed build to an App Store version.</summary>
    Task AttachBuildAsync(string appStoreVersionId, string buildId, CancellationToken ct);

    /// <summary>
    /// Submit the version for review via the Review Submissions API
    /// (create reviewSubmission → add reviewSubmissionItem → PATCH submitted=true).
    /// If a submission is already open for the app, reuse it.
    /// </summary>
    Task<AscSubmitResult> SubmitForReviewAsync(string appStoreVersionId, CancellationToken ct);
}
