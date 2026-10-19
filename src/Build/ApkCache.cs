using BuildChatBot.Config;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Build;

/// <summary>
/// Stores built APKs keyed by commit SHA plus the build recipe tag (<see cref="ApkBuildPlan.CacheTag"/>)
/// so repeat builds can short-circuit. The tag is in the key because the old SHA-only key would
/// have kept re-sending APKs built without release.env after the build command was fixed (2026-10-19);
/// those `app-<sha>.apk` files now never match and can be deleted.
/// </summary>
public sealed class ApkCache(BotConfig config, ILogger<ApkCache> log)
{
    public string? TryGet(string sha, string tag)
    {
        var path = ResolvePath(sha, tag);
        return File.Exists(path) ? path : null;
    }

    public string Store(string sha, string tag, string apkPath)
    {
        Directory.CreateDirectory(config.ApkCacheDir);
        var dest = ResolvePath(sha, tag);
        File.Copy(apkPath, dest, overwrite: true);
        log.LogInformation("Cached APK for {Sha} → {Dest}", sha[..7], dest);
        return dest;
    }

    private string ResolvePath(string sha, string tag) =>
        Path.Combine(config.ApkCacheDir, $"app-{sha}-{tag}.apk");
}
