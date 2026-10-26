namespace BuildChatBot.GooglePlay;

/// <param name="VersionCode">The versionCode Google Play read from the uploaded bundle.</param>
/// <param name="Track">The track the release was put on (e.g. internal).</param>
/// <param name="Status">Release status actually used: <c>completed</c>, or <c>draft</c> when Play refused a
/// completed release (an app that was never published only accepts drafts).</param>
public sealed record PlayUploadResult(long VersionCode, string Track, string Status);

/// <summary>A non-2xx answer from the Google Play Developer API (or the OAuth token endpoint).</summary>
public sealed class GooglePlayException : Exception
{
    public int StatusCode { get; }
    public string Detail { get; }
    public GooglePlayException(int statusCode, string detail) : base($"Google Play {statusCode}: {detail}")
    {
        StatusCode = statusCode;
        Detail = detail;
    }
}

/// <summary>
/// Thin wrapper over the Google Play Developer API v3 "edits" flow
/// (https://developers.google.com/android-publisher/edits), authenticated with a service account.
/// All methods throw <see cref="GooglePlayException"/> on API errors.
/// </summary>
public interface IGooglePlayClient
{
    /// <summary>
    /// Opens and immediately deletes an edit. Proves the key, the API, the Play Console permissions and
    /// the package name all work — before a 10-minute build, not after it.
    /// </summary>
    Task CheckAccessAsync(CancellationToken ct);

    /// <summary>
    /// Uploads the .aab and rolls it out to <paramref name="track"/> in one edit:
    /// insert edit → upload bundle → set the track's release → commit.
    /// </summary>
    Task<PlayUploadResult> UploadToTrackAsync(string aabPath, string track, string releaseName,
        Func<string, Task>? onStep, CancellationToken ct);
}
