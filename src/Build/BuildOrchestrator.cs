using System.Diagnostics;
using BuildChatBot.Config;
using BuildChatBot.Persistence;
using BuildChatBot.Telegram;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Build;

/// <summary>
/// Implements the 13-step build workflow described in the spec.
/// </summary>
public sealed class BuildOrchestrator(
    BotConfig config,
    BuildQueue queue,
    GitService git,
    FlutterRunner flutter,
    ApkSigner signer,
    ApkCache cache,
    ArtifactNamer namer,
    GradleCleaner gradle,
    ITelegramSender sender,
    BuildLogStore logStore,
    ILogger<BuildOrchestrator> log)
{
    private volatile string _state = "Idle";

    public string DescribeState() => _state;

    public async Task RunAsync(long chatId, CancellationToken globalCt)
    {
        var buildId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var startedAt = DateTimeOffset.Now;
        var totalSw = Stopwatch.StartNew();

        // Step 4: initial ack message — created BEFORE we acquire the lock so we can also
        // surface "Queued, position N" if there's contention.
        var statusMsgId = await sender.SendTextAsync(chatId,
            $"🚀 Build requested from `{config.RepoBranch}` — checking queue…", globalCt).ConfigureAwait(false);

        async Task ReportQueuePosition(int pos) =>
            await sender.EditTextAsync(chatId, statusMsgId,
                $"⏳ Queued — position {pos}. Will start automatically.", globalCt).ConfigureAwait(false);

        IDisposable? lockToken = null;
        ProgressReporter? progress = null;
        string commitSha = "";
        var versionName = "0.0.0";
        var versionCode = "0";
        var fromCache = false;
        var signatureVerified = false;
        var apkSize = 0L;
        var success = false;
        string? error = null;

        try
        {
            lockToken = await queue.AcquireAsync(ReportQueuePosition, globalCt).ConfigureAwait(false);

            _state = $"Building (chat {chatId}, branch {config.RepoBranch})";

            await sender.EditTextAsync(chatId, statusMsgId,
                $"🚀 Starting Android build from `{config.RepoBranch}`…", globalCt).ConfigureAwait(false);

            progress = new ProgressReporter(sender, chatId, statusMsgId, config.HeartbeatSeconds,
                $"🚀 Starting Android build from `{config.RepoBranch}`…");

            // Step 5: pull
            await progress.UpdateImmediateAsync($"📥 Pulling `{config.RepoBranch}`…", globalCt).ConfigureAwait(false);
            PullResult pull;
            try
            {
                pull = await Task.Run(() => git.Pull(), globalCt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Status line stays short; the detailed stderr goes as a separate chat message.
                error = $"Git pull failed: {ex.Message}";
                await progress.UpdateImmediateAsync(
                    $"❌ Git pull failed on `{config.RepoBranch}` — see details below.",
                    globalCt).ConfigureAwait(false);
                var detail = ex.Message.Length > 3800 ? ex.Message[^3800..] : ex.Message;
                await sender.SendTextAsync(chatId, $"```\n{detail}\n```", globalCt).ConfigureAwait(false);
                return;
            }

            commitSha = pull.HeadSha;
            await progress.UpdateImmediateAsync(
                $"📥 Repository synced ({commitSha[..7]}) — {(pull.WasUpdated ? "updated" : "no new commits")}",
                globalCt).ConfigureAwait(false);

            string apkPath;

            // Step 6: cache short-circuit
            var cached = !pull.WasUpdated ? cache.TryGet(commitSha) : null;
            if (cached is not null)
            {
                fromCache = true;
                apkPath = cached;
                progress.SetStatus($"♻️ Cached build for {commitSha[..7]} — re-sending…");
            }
            else
            {
                // Step 7: build
                progress.SetStatus("🛠 Building APK (this can take 3–15 minutes)…");
                var buildResult = await flutter.BuildReleaseApkAsync(buildId, globalCt).ConfigureAwait(false);
                if (!buildResult.Success)
                {
                    var tail = buildResult.LastError;
                    if (tail.Length > 3500) tail = tail[^3500..];
                    error = $"Flutter build failed (exit {buildResult.ExitCode}). Last log lines:\n```\n{tail}\n```";
                    await progress.UpdateImmediateAsync($"❌ Build failed (see log {buildResult.LogFile})", globalCt).ConfigureAwait(false);
                    await sender.SendTextAsync(chatId, error, globalCt).ConfigureAwait(false);
                    return;
                }

                // Step 8: locate artifact
                apkPath = Path.Combine(config.RepoPath, config.ApkOutputRelative);
                if (!File.Exists(apkPath))
                {
                    error = $"Build succeeded but APK not found at {apkPath}.";
                    await progress.UpdateImmediateAsync($"❌ {error}", globalCt).ConfigureAwait(false);
                    return;
                }

                apkSize = new FileInfo(apkPath).Length;
                cache.Store(commitSha, apkPath);

                await progress.UpdateImmediateAsync(
                    $"✅ Build complete ({Mb(apkSize)} MB)", globalCt).ConfigureAwait(false);
            }

            apkSize = new FileInfo(apkPath).Length;

            // Step 9: verify signing
            await progress.UpdateImmediateAsync("🔐 Verifying APK signature…", globalCt).ConfigureAwait(false);
            var verify = await signer.VerifyAsync(apkPath, globalCt).ConfigureAwait(false);
            signatureVerified = verify.Verified;
            if (!verify.Verified)
            {
                error = $"APK signature verification failed:\n{verify.Output}";
                await progress.UpdateImmediateAsync("❌ APK signature verification failed", globalCt).ConfigureAwait(false);
                await sender.SendTextAsync(chatId, error, globalCt).ConfigureAwait(false);
                return;
            }
            await progress.UpdateImmediateAsync("✅ APK signature verified", globalCt).ConfigureAwait(false);

            // Step 10 & 11: distribute
            var version = namer.ReadAppVersion();
            versionName = version.Name;
            versionCode = version.Code;
            var fileName = namer.BuildFilename(version, commitSha);
            var elapsed = totalSw.Elapsed;
            var caption = BuildCaption(version, commitSha, apkSize, elapsed, fromCache);

            await progress.UpdateImmediateAsync($"📤 Uploading {fileName} ({Mb(apkSize)} MB)…", globalCt).ConfigureAwait(false);
            await sender.SendDocumentAsync(chatId, apkPath, fileName, caption, globalCt).ConfigureAwait(false);

            await progress.UpdateImmediateAsync(
                $"✅ Done — `{config.RepoBranch}` {commitSha[..7]} ({Mb(apkSize)} MB, {elapsed:mm\\:ss})" +
                (fromCache ? " (cached)" : ""),
                globalCt).ConfigureAwait(false);

            success = true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled error during build for chat {Chat}", chatId);
            error = ex.Message;
            try
            {
                await sender.SendTextAsync(chatId, $"❌ Unexpected error: {ex.Message}", globalCt).ConfigureAwait(false);
            }
            catch { /* swallow */ }
        }
        finally
        {
            if (progress is not null) await progress.DisposeAsync();
            lockToken?.Dispose();
            _state = "Idle";

            // Step 12: periodic Gradle clean
            if (success && !fromCache)
            {
                try { gradle.MaybeClean(); }
                catch (Exception ex) { log.LogWarning(ex, "Gradle clean threw."); }
            }

            // Step 13: log record
            totalSw.Stop();
            try
            {
                logStore.Append(new BuildRecord(
                    BuildId: buildId,
                    StartedAt: startedAt,
                    FinishedAt: DateTimeOffset.Now,
                    ChatId: chatId,
                    Branch: config.RepoBranch,
                    CommitSha: commitSha,
                    VersionName: versionName,
                    VersionCode: versionCode,
                    FromCache: fromCache,
                    SignatureVerified: signatureVerified,
                    Success: success,
                    ApkSizeBytes: apkSize,
                    DurationMs: (long)totalSw.Elapsed.TotalMilliseconds,
                    Error: error));
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to persist build record.");
            }
        }
    }

    private string BuildCaption(AppVersion version, string sha, long sizeBytes, TimeSpan duration, bool fromCache)
    {
        var lines = new List<string>
        {
            $"✅ {(fromCache ? "Cached" : "Built")} {config.RepoBranch}",
            $"version: {version.Name}+{version.Code}",
            $"commit:  {sha[..Math.Min(sha.Length, 12)]}",
            $"size:    {Mb(sizeBytes)} MB",
            $"time:    {duration:mm\\:ss}",
            "",
            "Install:",
            " 1. Download on Android device",
            " 2. Settings → enable \"Install unknown apps\" for your browser/files app",
            " 3. Tap the APK to install",
        };
        return string.Join('\n', lines);
    }

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1");
}
