using BuildChatBot.Config;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Build;

/// <summary>
/// Stores built APKs keyed by commit SHA so repeat builds can short-circuit.
/// </summary>
public sealed class ApkCache(BotConfig config, ILogger<ApkCache> log)
{
    public string? TryGet(string sha)
    {
        var path = ResolvePath(sha);
        return File.Exists(path) ? path : null;
    }

    public string Store(string sha, string apkPath)
    {
        Directory.CreateDirectory(config.ApkCacheDir);
        var dest = ResolvePath(sha);
        File.Copy(apkPath, dest, overwrite: true);
        log.LogInformation("Cached APK for {Sha} → {Dest}", sha[..7], dest);
        return dest;
    }

    private string ResolvePath(string sha) =>
        Path.Combine(config.ApkCacheDir, $"app-{sha}.apk");
}
