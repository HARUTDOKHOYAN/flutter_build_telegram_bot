using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using BuildChatBot.Apple;
using BuildChatBot.Build;
using BuildChatBot.Config;
using BuildChatBot.Persistence;
using BuildChatBot.Telegram;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Release;

/// <summary>
/// Drives the two iOS release commands:
///
///   /release_ios_testflight — ask for version + build number, run <c>tool/release.sh --platform ios</c>,
///                             upload the .ipa to App Store Connect, wait for processing.
///   /release_ios_appstore   — pick a build that is ALREADY in TestFlight and submit it for App Store
///                             review with automatic release after approval.
///
/// Progress is reported by editing a single status message, exactly like <see cref="BuildOrchestrator"/>.
/// </summary>
public sealed class ReleaseOrchestrator(
    BotConfig config,
    BuildQueue queue,
    GitService git,
    IReleaseScriptRunner runner,
    IIpaUploader uploader,
    IAppStoreConnectClient asc,
    ITelegramSender sender,
    ReleaseConversation conversation,
    BuildLogStore logStore,
    ILogger<ReleaseOrchestrator> log)
{
    private const string PubspecRelative = "pubspec.yaml";
    private const int MaxBuildNumber = 2_100_000_000;
    private const int TelegramTextBudget = 3500;

    private static readonly Regex SemVerRegex = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);
    private static readonly Regex VersionBuildRegex =
        new(@"^(\d+\.\d+\.\d+)\s*(?:\+|\(|\s)\s*(\d+)\)?$", RegexOptions.Compiled);

    private volatile string _state = "Idle";

    public string DescribeState() => _state;

    // ---------------------------------------------------------------------------------------
    // /release_ios_testflight
    // ---------------------------------------------------------------------------------------

    public async Task RunTestFlightAsync(long chatId, CancellationToken globalCt)
    {
        if (!await EnsureAppleConfiguredAsync(chatId, globalCt).ConfigureAwait(false)) return;

        if (!OperatingSystem.IsMacOS())
        {
            await sender.SendTextAsync(chatId, "iOS release needs a macOS build host.", globalCt).ConfigureAwait(false);
            return;
        }

        await NotifyDisplacedAsync(chatId, globalCt).ConfigureAwait(false);

        var (curVersion, curBuild) = runner.ReadCurrentVersion();

        // Step 1: marketing version.
        var version = await AskAsync(
            chatId,
            $"📦 Last build: {curVersion}+{curBuild}.\nSend the new version (X.Y.Z), or tap Keep.",
            new[] { ($"Keep {curVersion}", "keep"), ("Cancel", "cancel") },
            answer => answer.Equals("keep", StringComparison.OrdinalIgnoreCase)
                ? curVersion
                : SemVerRegex.IsMatch(answer) ? answer : null,
            "⚠️ That is not a version number. Send it as X.Y.Z (for example 1.0.6).",
            globalCt).ConfigureAwait(false);
        if (version is null) return;

        // Step 2: build number.
        var nextBuild = curBuild + 1;
        var buildText = await AskAsync(
            chatId,
            $"🔢 Send the build number, or tap Next ({nextBuild}).",
            new[] { ($"Next {nextBuild}", "next"), ("Cancel", "cancel") },
            answer =>
            {
                if (answer.Equals("next", StringComparison.OrdinalIgnoreCase))
                    return nextBuild.ToString(CultureInfo.InvariantCulture);
                if (!int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return null;
                if (n <= curBuild || n > MaxBuildNumber) return null;
                return n.ToString(CultureInfo.InvariantCulture);
            },
            $"⚠️ The build number must be a whole number greater than {curBuild} and at most {MaxBuildNumber}.",
            globalCt).ConfigureAwait(false);
        if (buildText is null) return;
        var build = int.Parse(buildText, CultureInfo.InvariantCulture);

        // Step 3: confirm.
        var confirmed = await ConfirmAsync(
            chatId,
            $"🚀 Build {version}+{build} for iOS → TestFlight?\nBranch {config.RepoBranch}",
            "✅ Yes, build",
            "❌ No",
            globalCt).ConfigureAwait(false);
        if (!confirmed) return;

        await RunTestFlightPipelineAsync(chatId, version, build, globalCt).ConfigureAwait(false);
    }

    private async Task RunTestFlightPipelineAsync(long chatId, string version, int build, CancellationToken globalCt)
    {
        var buildId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var startedAt = DateTimeOffset.Now;
        var totalSw = Stopwatch.StartNew();

        var statusMsgId = await sender.SendTextAsync(chatId,
            $"🚀 iOS release {version}+{build} requested from `{config.RepoBranch}` — checking queue…",
            globalCt).ConfigureAwait(false);

        async Task ReportQueuePosition(int pos) =>
            await sender.EditTextAsync(chatId, statusMsgId,
                $"⏳ Queued — position {pos}. Will start automatically.", globalCt).ConfigureAwait(false);

        IDisposable? lockToken = null;
        ProgressReporter? progress = null;
        var commitSha = "";
        var success = false;
        string? error = null;
        string? notes = null;

        try
        {
            lockToken = await queue.AcquireAsync(ReportQueuePosition, globalCt).ConfigureAwait(false);

            _state = $"Building {version}+{build} (iOS TestFlight)";

            var initial = $"🚀 Starting iOS release {version}+{build} from `{config.RepoBranch}`…";
            await sender.EditTextAsync(chatId, statusMsgId, initial, globalCt).ConfigureAwait(false);
            progress = new ProgressReporter(sender, chatId, statusMsgId, config.HeartbeatSeconds, initial);

            // --- pull ---------------------------------------------------------------------
            await progress.UpdateImmediateAsync($"📥 Pulling `{config.RepoBranch}`…", globalCt).ConfigureAwait(false);
            PullResult pull;
            try
            {
                pull = await Task.Run(() => git.Pull(), globalCt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = $"Git pull failed: {ex.Message}";
                await progress.UpdateImmediateAsync(
                    $"❌ Git pull failed on `{config.RepoBranch}` — see details below.", globalCt).ConfigureAwait(false);
                await SendBlockAsync(chatId, null, ex.Message, globalCt).ConfigureAwait(false);
                return;
            }

            commitSha = pull.HeadSha;
            await progress.UpdateImmediateAsync(
                $"📥 Repository synced ({Short(commitSha)}) — {(pull.WasUpdated ? "updated" : "no new commits")}",
                globalCt).ConfigureAwait(false);

            // --- release script -----------------------------------------------------------
            await progress.UpdateImmediateAsync("🛠 Running tool/release.sh (iOS)…", globalCt).ConfigureAwait(false);
            var reporter = progress;
            var script = await runner.RunAsync(
                new ReleaseScriptRequest(ReleasePlatform.Ios, version, build),
                buildId,
                step => reporter.UpdateImmediateAsync($"🛠 {step}…", globalCt),
                globalCt).ConfigureAwait(false);

            if (!script.Success)
            {
                // The script bumps pubspec.yaml before building; a failed build must not leave it dirty.
                DiscardVersionBump();
                error = $"Release script failed (exit {script.ExitCode}).";
                await progress.UpdateImmediateAsync(
                    $"❌ Release script failed (exit {script.ExitCode}) — see log {script.LogFile}",
                    globalCt).ConfigureAwait(false);
                await SendBlockAsync(chatId, $"❌ Release script failed (exit {script.ExitCode})",
                    script.LastLines, globalCt).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(script.IpaPath))
            {
                DiscardVersionBump();
                error = "The release script finished but produced no .ipa.";
                await progress.UpdateImmediateAsync($"❌ {error}", globalCt).ConfigureAwait(false);
                await SendBlockAsync(chatId, $"❌ {error} Log: {script.LogFile}",
                    script.LastLines, globalCt).ConfigureAwait(false);
                return;
            }

            // --- upload -------------------------------------------------------------------
            await progress.UpdateImmediateAsync("📤 Uploading to App Store Connect…", globalCt).ConfigureAwait(false);
            var upload = await uploader.UploadAsync(script.IpaPath, onLog: null, globalCt).ConfigureAwait(false);
            if (!upload.Success)
            {
                // Deliberately NOT discarding the version bump: the build succeeded, so this
                // version+build pair is consumed and must not be reused.
                error = "Upload to App Store Connect failed.";
                await progress.UpdateImmediateAsync("❌ Upload to App Store Connect failed — see details below.",
                    globalCt).ConfigureAwait(false);
                await SendBlockAsync(chatId, "❌ Upload to App Store Connect failed",
                    upload.Output, globalCt).ConfigureAwait(false);
                return;
            }

            // --- commit the version bump ---------------------------------------------------
            if (config.ReleaseCommitVersionBump)
            {
                await progress.UpdateImmediateAsync("📝 Committing pubspec.yaml…", globalCt).ConfigureAwait(false);
                try
                {
                    if (await Task.Run(() => git.HasLocalChanges(PubspecRelative), globalCt).ConfigureAwait(false))
                    {
                        await Task.Run(() => git.CommitAndPush(
                            new[] { PubspecRelative },
                            $"release: {version}+{build} (iOS TestFlight)"), globalCt).ConfigureAwait(false);
                    }
                    else
                    {
                        log.LogInformation("pubspec.yaml has no local changes; skipping the version-bump commit.");
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Could not commit/push the version bump.");
                    await sender.SendTextAsync(chatId,
                        $"⚠️ Could not commit pubspec.yaml — commit it by hand.\n{Trim(ex.Message, 1000)}",
                        globalCt).ConfigureAwait(false);
                }
            }

            // --- wait for Apple to process --------------------------------------------------
            await progress.UpdateImmediateAsync(
                $"⏳ Waiting for Apple to process build {version} ({build})…", globalCt).ConfigureAwait(false);

            AscBuild? processed = null;
            try
            {
                processed = await asc.WaitForProcessingAsync(
                    version,
                    build.ToString(CultureInfo.InvariantCulture),
                    TimeSpan.FromSeconds(config.AppleProcessingTimeoutSeconds),
                    TimeSpan.FromSeconds(config.AppleProcessingPollSeconds),
                    state => reporter.UpdateImmediateAsync($"⏳ Apple processing: {state}…", globalCt),
                    globalCt).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                var minutes = Math.Max(1, config.AppleProcessingTimeoutSeconds / 60);
                notes = $"Uploaded; Apple processing not confirmed within {minutes} min.";
                await sender.SendTextAsync(chatId,
                    $"⚠️ Uploaded, but Apple has not finished processing after {minutes} min. " +
                    "Check App Store Connect.", globalCt).ConfigureAwait(false);
            }
            catch (AppStoreConnectException ex)
            {
                error = $"App Store Connect rejected the build: {ex.Detail}";
                await progress.UpdateImmediateAsync("❌ Apple could not process the build — see details below.",
                    globalCt).ConfigureAwait(false);
                await SendBlockAsync(chatId, "❌ Apple could not process the build", ex.Detail, globalCt)
                    .ConfigureAwait(false);
                return;
            }

            // --- export compliance ----------------------------------------------------------
            if (config.AppleSetExportComplianceExempt && processed is { IsValid: true })
            {
                try
                {
                    await asc.SetExportComplianceExemptAsync(processed.Id, globalCt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Could not set export compliance on build {BuildId}.", processed.Id);
                    await sender.SendTextAsync(chatId,
                        "⚠️ Could not set the export-compliance answer. TestFlight may show " +
                        "\"Missing Compliance\" until you answer it in App Store Connect.",
                        globalCt).ConfigureAwait(false);
                }
            }

            // --- done -----------------------------------------------------------------------
            var elapsed = totalSw.Elapsed;
            await progress.UpdateImmediateAsync(
                $"✅ {version} ({build}) is in TestFlight — {elapsed:mm\\:ss}", globalCt).ConfigureAwait(false);

            var summary = new List<string>
            {
                $"✅ TestFlight upload complete{(notes is null ? "" : " (with a warning)")}",
                $"version: {version}",
                $"build:   {build}",
                $"commit:  {Short(commitSha)}",
                $"dist:    {script.DistDir ?? "(unknown)"}",
                $"log:     {script.LogFile}",
                $"time:    {elapsed:mm\\:ss}",
            };
            if (notes is not null) summary.Add($"note:    {notes}");
            await sender.SendTextAsync(chatId, string.Join('\n', summary), globalCt).ConfigureAwait(false);

            notes ??= script.DistDir;
            success = true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled error during the iOS TestFlight release for chat {Chat}", chatId);
            error = ex.Message;
            try
            {
                await sender.SendTextAsync(chatId, $"❌ Unexpected error: {Trim(ex.Message, 1000)}", globalCt)
                    .ConfigureAwait(false);
            }
            catch { /* swallow */ }
        }
        finally
        {
            if (progress is not null) await progress.DisposeAsync();
            lockToken?.Dispose();
            _state = "Idle";

            totalSw.Stop();
            AppendRecord("ios_testflight", buildId, startedAt, chatId, commitSha,
                version, build.ToString(CultureInfo.InvariantCulture), success, totalSw.Elapsed, error, notes);
        }
    }

    // ---------------------------------------------------------------------------------------
    // /release_ios_appstore
    // ---------------------------------------------------------------------------------------

    public async Task RunAppStoreAsync(long chatId, CancellationToken globalCt)
    {
        // No macOS check here: this flow only talks to the App Store Connect REST API.
        if (!await EnsureAppleConfiguredAsync(chatId, globalCt).ConfigureAwait(false)) return;

        await NotifyDisplacedAsync(chatId, globalCt).ConfigureAwait(false);

        var buildId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var startedAt = DateTimeOffset.Now;
        var totalSw = Stopwatch.StartNew();
        var success = false;
        string? error = null;
        string? notes = null;
        AscBuild? chosen = null;

        try
        {
            var loadingMsgId = await sender.SendTextAsync(chatId, "🔎 Loading TestFlight builds…", globalCt)
                .ConfigureAwait(false);

            IReadOnlyList<AscBuild> recent;
            try
            {
                recent = await asc.ListRecentBuildsAsync(10, globalCt).ConfigureAwait(false);
            }
            catch (AppStoreConnectException ex)
            {
                error = ex.Detail;
                await sender.EditTextAsync(chatId, loadingMsgId,
                    "❌ Could not read the build list from App Store Connect.", globalCt).ConfigureAwait(false);
                await SendBlockAsync(chatId, null, ex.Detail, globalCt).ConfigureAwait(false);
                return;
            }

            var usable = recent.Where(b => b.IsValid && !b.Expired).ToList();
            if (usable.Count == 0)
            {
                await sender.EditTextAsync(chatId, loadingMsgId, "No processed TestFlight builds found.", globalCt)
                    .ConfigureAwait(false);
                error = "No processed TestFlight builds found.";
                return;
            }

            await sender.EditTextAsync(chatId, loadingMsgId,
                $"🔎 Found {usable.Count} processed TestFlight build(s).", globalCt).ConfigureAwait(false);

            var offered = usable.Take(8).ToList();
            var buttons = offered
                .Select(b => (Label: b.Display, Data: b.Id))
                .Append(("Cancel", "cancel"))
                .ToList();

            var pickedId = await AskAsync(
                chatId,
                "Which build should go to the App Store?\n" +
                "Tap one, or send it as 1.0.5+13.",
                buttons,
                answer => ResolveBuild(offered, answer)?.Id,
                "⚠️ I don't have that build. Tap one of the buttons, or send it as 1.0.5+13.",
                globalCt).ConfigureAwait(false);
            if (pickedId is null) return;

            chosen = offered.First(b => b.Id == pickedId);

            var confirmed = await ConfirmAsync(
                chatId,
                $"🍎 Submit {chosen.Display} to App Store review with automatic release after approval?",
                "✅ Yes, submit",
                "❌ No",
                globalCt).ConfigureAwait(false);
            if (!confirmed) return;

            _state = $"Submitting {chosen.Display} (iOS App Store)";

            var statusMsgId = await sender.SendTextAsync(chatId,
                $"🍎 Submitting {chosen.Display} to App Store review…", globalCt).ConfigureAwait(false);

            try
            {
                await sender.EditTextAsync(chatId, statusMsgId,
                    $"📝 Preparing App Store version {chosen.VersionString}…", globalCt).ConfigureAwait(false);
                var storeVersion = await asc.GetOrCreateEditableVersionAsync(chosen.VersionString, globalCt)
                    .ConfigureAwait(false);

                await sender.EditTextAsync(chatId, statusMsgId, "🔗 Attaching build…", globalCt).ConfigureAwait(false);
                await asc.AttachBuildAsync(storeVersion.Id, chosen.Id, globalCt).ConfigureAwait(false);

                await sender.EditTextAsync(chatId, statusMsgId, "📮 Submitting for review…", globalCt)
                    .ConfigureAwait(false);
                var submit = await asc.SubmitForReviewAsync(storeVersion.Id, globalCt).ConfigureAwait(false);

                if (!submit.Success)
                {
                    error = submit.Message;
                    await sender.EditTextAsync(chatId, statusMsgId,
                        "❌ App Store Connect did not accept the submission.", globalCt).ConfigureAwait(false);
                    await SendBlockAsync(chatId, null, submit.Message, globalCt).ConfigureAwait(false);
                    return;
                }

                notes = $"reviewSubmission {submit.ReviewSubmissionId}";
                await sender.EditTextAsync(chatId, statusMsgId,
                    $"✅ {chosen.Display} submitted for review. Apple will release it automatically after approval.",
                    globalCt).ConfigureAwait(false);
                success = true;
            }
            catch (AppStoreConnectException ex)
            {
                // This is the expected failure mode — ASC explains missing screenshots, metadata,
                // export compliance and so on in `Detail`. Show it verbatim.
                error = ex.Detail;
                await sender.EditTextAsync(chatId, statusMsgId,
                    "❌ App Store Connect rejected the submission — see details below.", globalCt)
                    .ConfigureAwait(false);
                await SendBlockAsync(chatId, "❌ App Store Connect says:", ex.Detail, globalCt).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled error during the App Store submission for chat {Chat}", chatId);
            error = ex.Message;
            try
            {
                await sender.SendTextAsync(chatId, $"❌ Unexpected error: {Trim(ex.Message, 1000)}", globalCt)
                    .ConfigureAwait(false);
            }
            catch { /* swallow */ }
        }
        finally
        {
            _state = "Idle";
            totalSw.Stop();
            AppendRecord("ios_appstore", buildId, startedAt, chatId, commitSha: "",
                versionName: chosen?.VersionString ?? "0.0.0",
                versionCode: chosen?.BuildNumber ?? "0",
                success: success, duration: totalSw.Elapsed, error: error, notes: notes);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------------------

    private async Task<bool> EnsureAppleConfiguredAsync(long chatId, CancellationToken ct)
    {
        if (config.AppleConfigured) return true;
        await sender.SendTextAsync(chatId,
            "App Store Connect is not configured. Add to env.local: " +
            string.Join(", ", config.MissingAppleKeys()), ct).ConfigureAwait(false);
        return false;
    }

    private async Task NotifyDisplacedAsync(long chatId, CancellationToken ct)
    {
        if (conversation.Displace(chatId))
            await sender.SendTextAsync(chatId, "↩️ Dropping the previous question — starting over.", ct)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Asks a question, validates the answer with <paramref name="parse"/> (which returns null when the
    /// answer is unusable), and re-asks exactly once before giving up. Returns null when the user
    /// cancelled, the prompt timed out, or the second answer was still invalid.
    /// </summary>
    private async Task<string?> AskAsync(
        long chatId,
        string question,
        IReadOnlyList<(string Label, string Data)> buttons,
        Func<string, string?> parse,
        string invalidHint,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var raw = await conversation.AskTextAsync(chatId, question, buttons, ct).ConfigureAwait(false);
            if (raw is null) return null; // timed out, cancelled, or displaced — already reported

            var answer = raw.Trim();
            if (answer.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                await sender.SendTextAsync(chatId, "Cancelled.", ct).ConfigureAwait(false);
                return null;
            }

            var parsed = parse(answer);
            if (parsed is not null) return parsed;

            if (attempt == 0)
                await sender.SendTextAsync(chatId, invalidHint, ct).ConfigureAwait(false);
        }

        await sender.SendTextAsync(chatId, "Cancelled — I could not read that answer.", ct).ConfigureAwait(false);
        return null;
    }

    private async Task<bool> ConfirmAsync(long chatId, string question, string yesLabel, string noLabel,
        CancellationToken ct)
    {
        var answer = await conversation.AskTextAsync(chatId, question,
            new[] { (yesLabel, "yes"), (noLabel, "no") }, ct).ConfigureAwait(false);

        if (answer is not null && answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;

        if (answer is not null) // null already produced a timeout/cancel notice
            await sender.SendTextAsync(chatId, "Cancelled.", ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>Matches a raw answer against the offered builds: an ASC id, "1.0.5+13" or "1.0.5 (13)".</summary>
    private static AscBuild? ResolveBuild(IReadOnlyList<AscBuild> builds, string answer)
    {
        var byId = builds.FirstOrDefault(b => string.Equals(b.Id, answer, StringComparison.Ordinal));
        if (byId is not null) return byId;

        var m = VersionBuildRegex.Match(answer);
        if (!m.Success) return null;

        return builds.FirstOrDefault(b =>
            string.Equals(b.VersionString, m.Groups[1].Value, StringComparison.Ordinal) &&
            string.Equals(b.BuildNumber, m.Groups[2].Value, StringComparison.Ordinal));
    }

    private void DiscardVersionBump()
    {
        try
        {
            if (git.HasLocalChanges(PubspecRelative))
                git.DiscardChanges(PubspecRelative);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not discard the pubspec.yaml version bump after a failed release build.");
        }
    }

    /// <summary>Sends an optional headline plus a fenced code block, trimmed to fit one Telegram message.</summary>
    private async Task SendBlockAsync(long chatId, string? headline, string? body, CancellationToken ct)
    {
        var head = string.IsNullOrWhiteSpace(headline) ? "" : headline + "\n";
        var budget = TelegramTextBudget - head.Length;
        var text = string.IsNullOrWhiteSpace(body)
            ? head + "(no output)"
            : head + $"```\n{Trim(body, budget)}\n```";
        await sender.SendTextAsync(chatId, text, ct).ConfigureAwait(false);
    }

    /// <summary>Keeps the tail — the interesting part of a build log is always at the end.</summary>
    private static string Trim(string text, int max) =>
        max <= 0 || text.Length <= max ? text : text[^max..];

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    private void AppendRecord(string kind, string buildId, DateTimeOffset startedAt, long chatId, string commitSha,
        string versionName, string versionCode, bool success, TimeSpan duration, string? error, string? notes)
    {
        try
        {
            logStore.Append(new BuildRecord(
                Kind: kind,
                BuildId: buildId,
                StartedAt: startedAt,
                FinishedAt: DateTimeOffset.Now,
                ChatId: chatId,
                Branch: config.RepoBranch,
                CommitSha: commitSha,
                VersionName: versionName,
                VersionCode: versionCode,
                FromCache: false,
                SignatureVerified: false,
                Success: success,
                ApkSizeBytes: 0,
                DurationMs: (long)duration.TotalMilliseconds,
                Error: error,
                Notes: notes));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to persist the release record.");
        }
    }
}
