namespace BuildChatBot.Build;

public enum ReleasePlatform { Ios, Android, All }

/// <summary>Arguments forwarded to <c>tool/release.sh</c>.</summary>
/// <param name="Version">Marketing version X.Y.Z (passed as --version). Null = keep current.</param>
/// <param name="BuildNumber">Build number (passed as --build-number). Null = script auto-increments.</param>
public sealed record ReleaseScriptRequest(
    ReleasePlatform Platform,
    string? Version,
    int? BuildNumber,
    bool SkipHealth = false,
    bool BuildApk = false,
    bool DryRun = false);

/// <param name="DistDir">Absolute path of dist/&lt;version&gt;+&lt;build&gt;/ or null when unknown / dry-run.</param>
/// <param name="IpaPath">Absolute path of the .ipa inside DistDir, or null.</param>
/// <param name="AabPath">Absolute path of the .aab inside DistDir, or null.</param>
/// <param name="ApkPath">Absolute path of the .apk inside DistDir, or null.</param>
/// <param name="LastLines">Tail of the log (for failure reporting), already trimmed to fit a Telegram message.</param>
public sealed record ReleaseScriptResult(
    bool Success,
    int ExitCode,
    string LogFile,
    string ResolvedVersion,
    int ResolvedBuildNumber,
    string? DistDir,
    string? IpaPath,
    string? AabPath,
    string? ApkPath,
    string LastLines);

/// <summary>
/// Runs the Flutter repo's <c>tool/release.sh</c> and streams its output.
/// The script prints <c>==> Step name</c> for every phase; the runner reports each one via <c>onStep</c>
/// so the orchestrator can mirror progress into Telegram.
/// </summary>
public interface IReleaseScriptRunner
{
    /// <summary>Read <c>version: X.Y.Z+N</c> from pubspec.yaml in the repo. Returns ("0.0.0", 0) when absent.</summary>
    (string Version, int BuildNumber) ReadCurrentVersion();

    Task<ReleaseScriptResult> RunAsync(ReleaseScriptRequest request, string buildId, Func<string, Task>? onStep, CancellationToken ct);
}
