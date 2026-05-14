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
    public string? RepoSshPubkeyPath { get; set; }
    public string? RepoSshPassphrase { get; set; }

    // Flutter / build flags
    public string FlutterBin { get; set; } = "flutter";
    public string FlutterTargetPlatform { get; set; } = "android-arm64";
    public string? FlutterFlavor { get; set; }
    public string? FlutterTargetFile { get; set; }
    public string? FlutterExtraArgs { get; set; }
    public bool FlutterRunPubGet { get; set; } = true;
    public bool FlutterRunClean { get; set; } = true;

    // Android toolchain
    public string? JavaHome { get; set; }
    public string? AndroidSdkRoot { get; set; }
    public string? ApksignerPath { get; set; }

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
        cfg.RepoSshPubkeyPath = EnvOrNull("REPO_SSH_PUBKEY_PATH", cfg.RepoSshPubkeyPath);
        cfg.RepoSshPassphrase = EnvOrNull("REPO_SSH_PASSPHRASE", cfg.RepoSshPassphrase);

        // Flutter
        cfg.FlutterBin = Env("FLUTTER_BIN", cfg.FlutterBin);
        cfg.FlutterTargetPlatform = Env("FLUTTER_TARGET_PLATFORM", cfg.FlutterTargetPlatform);
        cfg.FlutterFlavor = EnvOrNull("FLUTTER_FLAVOR", cfg.FlutterFlavor);
        cfg.FlutterTargetFile = EnvOrNull("FLUTTER_TARGET_FILE", cfg.FlutterTargetFile);
        cfg.FlutterExtraArgs = EnvOrNull("FLUTTER_EXTRA_ARGS", cfg.FlutterExtraArgs);
        cfg.FlutterRunPubGet = EnvBool("FLUTTER_RUN_PUB_GET", cfg.FlutterRunPubGet);
        cfg.FlutterRunClean = EnvBool("FLUTTER_RUN_CLEAN", cfg.FlutterRunClean);

        // Android toolchain
        cfg.JavaHome = EnvOrNull("JAVA_HOME", cfg.JavaHome);
        cfg.AndroidSdkRoot = EnvOrNull("ANDROID_SDK_ROOT", cfg.AndroidSdkRoot);
        cfg.ApksignerPath = EnvOrNull("APKSIGNER_PATH", cfg.ApksignerPath);

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

        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid configuration:\n - " + string.Join("\n - ", errors));
    }

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
