using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BuildChatBot.Config;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Build;

public sealed record ApkVerifyResult(bool Verified, string Output);

/// <summary>
/// Runs `apksigner verify --verbose` to confirm the APK is signed.
/// </summary>
public sealed class ApkSigner(BotConfig config, ILogger<ApkSigner> log)
{
    public async Task<ApkVerifyResult> VerifyAsync(string apkPath, CancellationToken ct)
    {
        var apksigner = ResolveApksigner();
        if (apksigner is null)
            return new ApkVerifyResult(false, "apksigner not found. Set APKSIGNER_PATH or install Android build-tools.");

        var psi = new ProcessStartInfo
        {
            FileName = apksigner,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("verify");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add(apkPath);

        log.LogInformation("$ {Bin} verify --verbose {Apk}", apksigner, apkPath);

        var sb = new StringBuilder();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        var output = sb.ToString();
        var verified = proc.ExitCode == 0
            && output.Contains("Verifies", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("DOES NOT VERIFY", StringComparison.OrdinalIgnoreCase);

        return new ApkVerifyResult(verified, output);
    }

    private string? ResolveApksigner()
    {
        if (!string.IsNullOrWhiteSpace(config.ApksignerPath) && File.Exists(config.ApksignerPath))
            return config.ApksignerPath;

        var sdkRoot = config.AndroidSdkRoot
            ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
            ?? Environment.GetEnvironmentVariable("ANDROID_HOME");

        if (!string.IsNullOrWhiteSpace(sdkRoot))
        {
            var buildToolsDir = Path.Combine(sdkRoot, "build-tools");
            if (Directory.Exists(buildToolsDir))
            {
                var candidate = Directory.EnumerateDirectories(buildToolsDir)
                    .OrderByDescending(d => d)
                    .Select(d => Path.Combine(d, IsWindows() ? "apksigner.bat" : "apksigner"))
                    .FirstOrDefault(File.Exists);
                if (candidate is not null) return candidate;
            }
        }

        // Fall back to PATH
        var which = IsWindows() ? "where" : "which";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(which, "apksigner")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (p is null) return null;
            var line = p.StandardOutput.ReadLine();
            p.WaitForExit();
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
        catch { return null; }
    }

    private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
