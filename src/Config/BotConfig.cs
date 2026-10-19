using Microsoft.Extensions.Configuration;

namespace BuildChatBot.Config;

/// <summary>
/// Strongly-typed configuration POCO bound from appsettings.json + env.local.
/// Every field is overridable via env.local — see env.local.example for the canonical key list.
/// </summary>
public sealed class BotConfig
{
    // Telegram
    public string TelegramBotToken { get; set; } = "";
    public int TelegramApiId { get; set; }
    public string TelegramApiHash { get; set; } = "";
    public int TelegramLargeFileThresholdMb { get; set; } = 49;

    // Repository
    public string RepoPath { get; set; } = "";
    public string RepoBranch { get; set; } = "master";
    public string RepoRemoteName { get; set; } = "origin";
    public string? RepoSshKeyPath { get; set; }
    public string? RepoSshPassphrase { get; set; }

    // Flutter / build flags
    public string FlutterBin { get; set; } = "flutter";
    public string FlutterTargetPlatform { get; set; } = "android-arm64";
    public string? FlutterFlavor { get; set; }
    public string? FlutterTargetFile { get; set; }
    public string? FlutterExtraArgs { get; set; }
    /// <summary>
    /// File (relative to <see cref="RepoPath"/> unless absolute) passed as
    /// `--dart-define-from-file`. The Airexys app refuses to start in release mode without the
    /// API_BASE_URL/AUTH_BASE_URL defines it carries, so a missing file fails the build instead of
    /// shipping an APK that closes at launch (2026-10-19: every bot APK did). `none` turns it off
    /// (a blank env value falls back to this default like every other key).
    /// </summary>
    public string? FlutterDartDefineFile { get; set; } = "release.env";

    /// <summary>Absolute path of <see cref="FlutterDartDefineFile"/>, or null when turned off.</summary>
    public string? ResolvedDartDefineFile =>
        string.IsNullOrWhiteSpace(FlutterDartDefineFile)
        || FlutterDartDefineFile.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.IsPathRooted(FlutterDartDefineFile) ? FlutterDartDefineFile : Path.Combine(RepoPath, FlutterDartDefineFile);
    public bool FlutterRunPubGet { get; set; } = true;
    public bool FlutterRunClean { get; set; } = true;

    // Android toolchain
    public string? JavaHome { get; set; }
    public string? AndroidSdkRoot { get; set; }
    public string? ApksignerPath { get; set; }

    /// <summary>
    /// Folders prepended to PATH for every build child (release.sh, flutter, gradlew), separated
    /// by ':' (';' on Windows); '~/' is expanded. For tools the launchd PATH cannot find, e.g. a
    /// gem-installed `pod` or an fvm Flutter. Blank: only the auto-detected folders are added.
    /// </summary>
    public string? ExtraPath { get; set; }

    // Artifact paths
    public string ApkOutputRelative { get; set; } = "build/app/outputs/flutter-apk/app-release.apk";
    public string ApkCacheDir { get; set; } = "./cache/apks";
    public string TempDirBase { get; set; } = "./tmp";

    // Runtime behaviour
    public int HeartbeatSeconds { get; set; } = 30;
    public int QueueCapacity { get; set; } = 10;
    public int GradleCleanEveryNBuilds { get; set; } = 20;
    public string LogDir { get; set; } = "./logs";
    public string BuildRecordPath { get; set; } = "./logs/builds.jsonl";

    // Release pipeline (tool/release.sh inside the Flutter repo)
    /// <summary>Path of the release script, relative to <see cref="RepoPath"/> unless absolute.</summary>
    public string ReleaseScriptPath { get; set; } = "tool/release.sh";
    /// <summary>Seconds the bot waits for the user to answer a version/build-number prompt before cancelling.</summary>
    public int ReleasePromptTimeoutSeconds { get; set; } = 300;
    /// <summary>Commit + push pubspec.yaml after a successful release build (the pubspec is the release log).</summary>
    public bool ReleaseCommitVersionBump { get; set; } = true;
    public string ReleaseGitAuthorName { get; set; } = "BuildChatBot";
    public string ReleaseGitAuthorEmail { get; set; } = "buildbot@local";

    // Apple / App Store Connect (only needed for /release_ios_* commands; validated lazily)
    public string? AppleApiKeyId { get; set; }
    public string? AppleApiIssuerId { get; set; }
    /// <summary>Absolute path to the AuthKey_XXXX.p8 private key downloaded from App Store Connect.</summary>
    public string? AppleApiKeyPath { get; set; }
    /// <summary>Bundle identifier of the iOS app, e.g. com.airexys.app. Used to resolve the App Store Connect app id.</summary>
    public string? AppleBundleId { get; set; }
    /// <summary>Seconds to wait for App Store Connect to finish processing an uploaded build.</summary>
    public int AppleProcessingTimeoutSeconds { get; set; } = 2400;
    /// <summary>Seconds between App Store Connect processing polls.</summary>
    public int AppleProcessingPollSeconds { get; set; } = 30;
    /// <summary>When true, mark processed builds as "does not use non-exempt encryption" so TestFlight doesn't stall on Missing Compliance.</summary>
    public bool AppleSetExportComplianceExempt { get; set; } = true;

    /// <summary>
    /// Build a <see cref="BotConfig"/>. Precedence (highest wins):
    ///   1. Environment variables (env.local is loaded into process env at startup).
    ///   2. appsettings.json "Bot" section (optional — empty by default).
    ///   3. C# field initializers above (the single source of truth for defaults).
    /// </summary>
    public static BotConfig Load(IConfiguration configuration)
    {
        var cfg = new BotConfig();
        // Optional appsettings.json overlay. The shipped file has no "Bot" section,
        // so this is a no-op unless you choose to add one.
        configuration.GetSection("Bot").Bind(cfg);

        // Telegram
        cfg.TelegramBotToken = Env("TELEGRAM_BOT_TOKEN", cfg.TelegramBotToken);
        cfg.TelegramApiId = EnvInt("TELEGRAM_API_ID", cfg.TelegramApiId);
        cfg.TelegramApiHash = Env("TELEGRAM_API_HASH", cfg.TelegramApiHash);
        cfg.TelegramLargeFileThresholdMb = EnvInt("TELEGRAM_LARGE_FILE_THRESHOLD_MB", cfg.TelegramLargeFileThresholdMb);

        // Repo
        cfg.RepoPath = Env("REPO_PATH", cfg.RepoPath);
        cfg.RepoBranch = Env("REPO_BRANCH", cfg.RepoBranch);
        cfg.RepoRemoteName = Env("REPO_REMOTE_NAME", cfg.RepoRemoteName);
        cfg.RepoSshKeyPath = EnvOrNull("REPO_SSH_KEY_PATH", cfg.RepoSshKeyPath);
        cfg.RepoSshPassphrase = EnvOrNull("REPO_SSH_PASSPHRASE", cfg.RepoSshPassphrase);

        // Flutter
        cfg.FlutterBin = Env("FLUTTER_BIN", cfg.FlutterBin);
        cfg.FlutterTargetPlatform = Env("FLUTTER_TARGET_PLATFORM", cfg.FlutterTargetPlatform);
        cfg.FlutterFlavor = EnvOrNull("FLUTTER_FLAVOR", cfg.FlutterFlavor);
        cfg.FlutterTargetFile = EnvOrNull("FLUTTER_TARGET_FILE", cfg.FlutterTargetFile);
        cfg.FlutterExtraArgs = EnvOrNull("FLUTTER_EXTRA_ARGS", cfg.FlutterExtraArgs);
        cfg.FlutterDartDefineFile = EnvOrNull("FLUTTER_DART_DEFINE_FILE", cfg.FlutterDartDefineFile);
        cfg.FlutterRunPubGet = EnvBool("FLUTTER_RUN_PUB_GET", cfg.FlutterRunPubGet);
        cfg.FlutterRunClean = EnvBool("FLUTTER_RUN_CLEAN", cfg.FlutterRunClean);

        // Android toolchain
        cfg.JavaHome = EnvOrNull("JAVA_HOME", cfg.JavaHome);
        cfg.AndroidSdkRoot = EnvOrNull("ANDROID_SDK_ROOT", cfg.AndroidSdkRoot);
        cfg.ApksignerPath = EnvOrNull("APKSIGNER_PATH", cfg.ApksignerPath);
        cfg.ExtraPath = EnvOrNull("EXTRA_PATH", cfg.ExtraPath);

        // Artifact paths
        cfg.ApkOutputRelative = Env("APK_OUTPUT_RELATIVE", cfg.ApkOutputRelative);
        cfg.ApkCacheDir = Env("APK_CACHE_DIR", cfg.ApkCacheDir);
        cfg.TempDirBase = Env("TEMP_DIR_BASE", cfg.TempDirBase);

        // Runtime
        cfg.HeartbeatSeconds = EnvInt("HEARTBEAT_SECONDS", cfg.HeartbeatSeconds);
        cfg.QueueCapacity = EnvInt("QUEUE_CAPACITY", cfg.QueueCapacity);
        cfg.GradleCleanEveryNBuilds = EnvInt("GRADLE_CLEAN_EVERY_N_BUILDS", cfg.GradleCleanEveryNBuilds);
        cfg.LogDir = Env("LOG_DIR", cfg.LogDir);
        cfg.BuildRecordPath = Env("BUILD_RECORD_PATH", cfg.BuildRecordPath);

        // Release pipeline
        cfg.ReleaseScriptPath = Env("RELEASE_SCRIPT_PATH", cfg.ReleaseScriptPath);
        cfg.ReleasePromptTimeoutSeconds = EnvInt("RELEASE_PROMPT_TIMEOUT_SECONDS", cfg.ReleasePromptTimeoutSeconds);
        cfg.ReleaseCommitVersionBump = EnvBool("RELEASE_COMMIT_VERSION_BUMP", cfg.ReleaseCommitVersionBump);
        cfg.ReleaseGitAuthorName = Env("RELEASE_GIT_AUTHOR_NAME", cfg.ReleaseGitAuthorName);
        cfg.ReleaseGitAuthorEmail = Env("RELEASE_GIT_AUTHOR_EMAIL", cfg.ReleaseGitAuthorEmail);

        // Apple / App Store Connect
        cfg.AppleApiKeyId = EnvOrNull("APPLE_API_KEY_ID", cfg.AppleApiKeyId);
        cfg.AppleApiIssuerId = EnvOrNull("APPLE_API_ISSUER_ID", cfg.AppleApiIssuerId);
        cfg.AppleApiKeyPath = EnvOrNull("APPLE_API_KEY_PATH", cfg.AppleApiKeyPath);
        cfg.AppleBundleId = EnvOrNull("APPLE_BUNDLE_ID", cfg.AppleBundleId);
        cfg.AppleProcessingTimeoutSeconds = EnvInt("APPLE_PROCESSING_TIMEOUT_SECONDS", cfg.AppleProcessingTimeoutSeconds);
        cfg.AppleProcessingPollSeconds = EnvInt("APPLE_PROCESSING_POLL_SECONDS", cfg.AppleProcessingPollSeconds);
        cfg.AppleSetExportComplianceExempt = EnvBool("APPLE_SET_EXPORT_COMPLIANCE_EXEMPT", cfg.AppleSetExportComplianceExempt);

        return cfg;
    }

    public void Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(TelegramBotToken)) errors.Add("TELEGRAM_BOT_TOKEN is required.");
        if (TelegramApiId <= 0) errors.Add("TELEGRAM_API_ID is required and must be > 0.");
        if (string.IsNullOrWhiteSpace(TelegramApiHash)) errors.Add("TELEGRAM_API_HASH is required.");
        if (string.IsNullOrWhiteSpace(RepoPath)) errors.Add("REPO_PATH is required.");
        else if (!Directory.Exists(RepoPath)) errors.Add($"REPO_PATH does not exist: {RepoPath}");
        if (string.IsNullOrWhiteSpace(RepoBranch)) errors.Add("REPO_BRANCH is required.");
        if (HeartbeatSeconds < 5) errors.Add("HEARTBEAT_SECONDS must be >= 5.");
        if (QueueCapacity < 1) errors.Add("QUEUE_CAPACITY must be >= 1.");

        if (ReleasePromptTimeoutSeconds < 30) errors.Add("RELEASE_PROMPT_TIMEOUT_SECONDS must be >= 30.");
        if (AppleProcessingPollSeconds < 5) errors.Add("APPLE_PROCESSING_POLL_SECONDS must be >= 5.");

        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid configuration:\n - " + string.Join("\n - ", errors));
    }

    /// <summary>True when every key needed to talk to App Store Connect is present.</summary>
    public bool AppleConfigured =>
        !string.IsNullOrWhiteSpace(AppleApiKeyId)
        && !string.IsNullOrWhiteSpace(AppleApiIssuerId)
        && !string.IsNullOrWhiteSpace(AppleApiKeyPath)
        && !string.IsNullOrWhiteSpace(AppleBundleId);

    /// <summary>
    /// Human-readable list of the Apple keys that are missing. Empty when <see cref="AppleConfigured"/>.
    /// Used by the release commands to tell the user exactly what to add to env.local.
    /// </summary>
    public IReadOnlyList<string> MissingAppleKeys()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(AppleApiKeyId)) missing.Add("APPLE_API_KEY_ID");
        if (string.IsNullOrWhiteSpace(AppleApiIssuerId)) missing.Add("APPLE_API_ISSUER_ID");
        if (string.IsNullOrWhiteSpace(AppleApiKeyPath)) missing.Add("APPLE_API_KEY_PATH");
        else if (!File.Exists(AppleApiKeyPath)) missing.Add($"APPLE_API_KEY_PATH (file not found: {AppleApiKeyPath})");
        if (string.IsNullOrWhiteSpace(AppleBundleId)) missing.Add("APPLE_BUNDLE_ID");
        return missing;
    }

    /// <summary>Absolute path of the release script.</summary>
    public string ResolvedReleaseScriptPath =>
        Path.IsPathRooted(ReleaseScriptPath) ? ReleaseScriptPath : Path.Combine(RepoPath, ReleaseScriptPath);

    private static string Env(string key, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private static string? EnvOrNull(string key, string? fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private static int EnvInt(string key, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return int.TryParse(v, out var n) ? n : fallback;
    }

    private static bool EnvBool(string key, bool fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(v)) return fallback;
        return v.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => fallback,
        };
    }
}
