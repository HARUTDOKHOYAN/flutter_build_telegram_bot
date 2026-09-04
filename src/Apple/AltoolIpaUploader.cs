using System.Diagnostics;
using System.Text;
using BuildChatBot.Config;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Apple;

/// <summary>
/// Uploads an .ipa with <c>xcrun altool --upload-app</c>, authenticated with the App Store Connect
/// API key. Requires macOS with Xcode command line tools installed.
/// </summary>
/// <remarks>
/// altool does not take the key file as an argument: it looks for <c>AuthKey_&lt;KEYID&gt;.p8</c>
/// inside the directory named by <c>API_PRIVATE_KEYS_DIR</c> (falling back to <c>./private_keys</c>,
/// <c>~/private_keys</c>, <c>~/.private_keys</c>, <c>~/.appstoreconnect/private_keys</c>).
/// So the configured key is either used in place (when it already has the expected file name) or
/// copied once into <c>&lt;TEMP_DIR_BASE&gt;/private_keys/</c> with 0600 permissions.
/// </remarks>
public sealed class AltoolIpaUploader(BotConfig config, ILogger<AltoolIpaUploader> log) : IIpaUploader
{
    private const int MaxOutputChars = 3500;

    public async Task<IpaUploadResult> UploadAsync(string ipaPath, Action<string>? onLog, CancellationToken ct)
    {
        // ---- pre-checks: these are user/setup problems, reported as a failed result, not an exception.
        if (!OperatingSystem.IsMacOS())
            return Fail("Uploading to App Store Connect requires macOS (xcrun altool is part of Xcode).", onLog);

        if (string.IsNullOrWhiteSpace(ipaPath) || !File.Exists(ipaPath))
            return Fail($"IPA not found: {(string.IsNullOrWhiteSpace(ipaPath) ? "(no path)" : ipaPath)}", onLog);

        var missing = config.MissingAppleKeys();
        if (missing.Count > 0)
            return Fail("App Store Connect credentials are missing. Add to env.local: " + string.Join(", ", missing), onLog);

        var xcrun = ResolveXcrun();
        if (xcrun is null)
            return Fail("xcrun was not found. Install the Xcode command line tools (xcode-select --install).", onLog);

        string keysDir;
        try
        {
            keysDir = PrepareKeysDirectory();
        }
        catch (Exception ex)
        {
            return Fail($"Could not stage the App Store Connect key for altool: {ex.Message}", onLog);
        }

        // ---- run altool
        var psi = new ProcessStartInfo
        {
            FileName = xcrun,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("altool");
        psi.ArgumentList.Add("--upload-app");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(ipaPath);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("ios");
        psi.ArgumentList.Add("--apiKey");
        psi.ArgumentList.Add(config.AppleApiKeyId!);
        psi.ArgumentList.Add("--apiIssuer");
        psi.ArgumentList.Add(config.AppleApiIssuerId!);
        psi.Environment["API_PRIVATE_KEYS_DIR"] = keysDir;

        var output = new StringBuilder();
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Capture(string? line, TaskCompletionSource done)
        {
            if (line is null) { done.TrySetResult(); return; }
            lock (output) output.AppendLine(line);
            log.LogInformation("altool: {Line}", line);
            onLog?.Invoke(line);
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => Capture(e.Data, stdoutDone);
        proc.ErrorDataReceived += (_, e) => Capture(e.Data, stderrDone);

        log.LogInformation("$ {Bin} altool --upload-app -f {Ipa} -t ios --apiKey <redacted> --apiIssuer <redacted>",
            xcrun, ipaPath);

        var sw = Stopwatch.StartNew();
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return Fail($"Failed to start xcrun altool: {ex.Message}", onLog);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var registration = ct.Register(() => KillTree(proc));
        try
        {
            // Wait without ct so the kill callback can run and the exit code is still observed.
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }

        // Let the async readers drain before inspecting the combined output.
        await Task.WhenAll(stdoutDone.Task, stderrDone.Task).ConfigureAwait(false);
        sw.Stop();
        ct.ThrowIfCancellationRequested();

        string combined;
        lock (output) combined = output.ToString();

        var exitCode = proc.ExitCode;
        // altool prints "UPLOAD SUCCEEDED" / "No errors uploading ..." on success and shouts
        // "ERROR ITMS-xxxxx" / "*** Error:" on failure — and it has been known to exit 0 anyway.
        var sawError = combined.Contains("ERROR", StringComparison.Ordinal)
                       || combined.Contains("*** Error", StringComparison.Ordinal);
        var success = exitCode == 0 && !sawError;

        log.LogInformation("altool exited {Exit} in {Elapsed} (success={Success})", exitCode, sw.Elapsed, success);

        var report = combined.Trim();
        if (report.Length == 0)
            report = $"altool produced no output (exit code {exitCode}).";
        else if (!success)
            report = $"altool exit code {exitCode}\n{report}";

        return new IpaUploadResult(success, Trim(report));
    }

    /// <summary>
    /// Returns the directory to hand altool via <c>API_PRIVATE_KEYS_DIR</c>: the key's own directory
    /// when it is already named <c>AuthKey_&lt;KEYID&gt;.p8</c>, otherwise a private staging directory
    /// under <c>TEMP_DIR_BASE</c> holding a 0600 copy.
    /// </summary>
    private string PrepareKeysDirectory()
    {
        var expectedName = $"AuthKey_{config.AppleApiKeyId!}.p8";
        var source = Path.GetFullPath(config.AppleApiKeyPath!);

        if (string.Equals(Path.GetFileName(source), expectedName, StringComparison.Ordinal))
        {
            var ownDir = Path.GetDirectoryName(source);
            if (!string.IsNullOrEmpty(ownDir)) return ownDir;
        }

        var stagingDir = Path.GetFullPath(Path.Combine(config.TempDirBase, "private_keys"));
        Directory.CreateDirectory(stagingDir);
        Harden(stagingDir, isDirectory: true);

        var destination = Path.Combine(stagingDir, expectedName);
        var sourceInfo = new FileInfo(source);
        var destinationInfo = new FileInfo(destination);
        var stale = !destinationInfo.Exists
                    || destinationInfo.Length != sourceInfo.Length
                    || destinationInfo.LastWriteTimeUtc < sourceInfo.LastWriteTimeUtc;

        if (stale)
        {
            File.Copy(source, destination, overwrite: true);
            log.LogInformation("Staged the App Store Connect key as {Name} for altool", expectedName);
        }
        Harden(destination, isDirectory: false);

        return stagingDir;
    }

    private static void Harden(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best effort: a file system that cannot express Unix modes is not a reason to fail the upload.
        }
    }

    private static string? ResolveXcrun()
    {
        const string wellKnown = "/usr/bin/xcrun";
        if (File.Exists(wellKnown)) return wellKnown;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "xcrun");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or we lost the race with its exit — nothing to do.
        }
    }

    private IpaUploadResult Fail(string message, Action<string>? onLog)
    {
        log.LogWarning("IPA upload aborted: {Message}", message);
        onLog?.Invoke(message);
        return new IpaUploadResult(false, message);
    }

    private static string Trim(string text) =>
        text.Length <= MaxOutputChars ? text : "…\n" + text[^MaxOutputChars..];
}
