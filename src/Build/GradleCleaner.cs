using System.Diagnostics;
using BuildChatBot.Config;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Build;

/// <summary>
/// Periodic Gradle cache prune. Stores the build counter in a sidecar file so it survives restarts.
/// </summary>
public sealed class GradleCleaner(BotConfig config, ILogger<GradleCleaner> log)
{
    private readonly object _gate = new();

    public void MaybeClean()
    {
        if (config.GradleCleanEveryNBuilds <= 0) return;

        lock (_gate)
        {
            var counterFile = Path.Combine(config.LogDir, ".build-counter");
            Directory.CreateDirectory(config.LogDir);

            var count = 0;
            if (File.Exists(counterFile) && int.TryParse(File.ReadAllText(counterFile), out var c)) count = c;
            count++;

            if (count >= config.GradleCleanEveryNBuilds)
            {
                Clean();
                count = 0;
            }

            File.WriteAllText(counterFile, count.ToString());
        }
    }

    private void Clean()
    {
        var androidDir = Path.Combine(config.RepoPath, "android");
        var gradlew = Path.Combine(androidDir, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");

        if (!File.Exists(gradlew))
        {
            log.LogWarning("Skipping Gradle clean — {Gradlew} not found.", gradlew);
            return;
        }

        log.LogInformation("Running Gradle cache clean...");
        var psi = new ProcessStartInfo
        {
            FileName = gradlew,
            WorkingDirectory = androidDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("clean");
        psi.ArgumentList.Add("--no-daemon");

        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(TimeSpan.FromMinutes(5));
            log.LogInformation("Gradle clean finished with exit code {Exit}.", p?.ExitCode);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Gradle clean failed.");
        }
    }
}
