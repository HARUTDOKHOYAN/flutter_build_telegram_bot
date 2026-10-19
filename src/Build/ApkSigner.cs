using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BuildChatBot.Config;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Build;

public sealed record ApkVerifyResult(bool Verified, bool DebugSigned, string Output);

/// <summary>
/// Runs `apksigner verify --verbose --print-certs` to confirm the APK is signed, and notes when the
/// signer is the Android debug key. A debug key still "Verifies", but Gradle falls back to it when
/// android/key.properties is missing on the build host, and Android then refuses to update a copy
/// signed with the upload key (INSTALL_FAILED_UPDATE_INCOMPATIBLE) — the tester has to uninstall.
/// </summary>
public sealed class ApkSigner(BotConfig config, ILogger<ApkSigner> log)
{
    public async Task<ApkVerifyResult> VerifyAsync(string apkPath, CancellationToken ct)
    {
        var apksigner = ResolveApksigner();
        if (apksigner is null)
            return new ApkVerifyResult(false, false, "apksigner not found. Set APKSIGNER_PATH or install Android build-tools.");

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
        psi.ArgumentList.Add("--print-certs");
        psi.ArgumentList.Add(apkPath);

        log.LogInformation("$ {Bin} verify --verbose --print-certs {Apk}", apksigner, apkPath);

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

        // The debug keystore Gradle generates is always "C=US, O=Android, CN=Android Debug".
        var debugSigned = output.Contains("CN=Android Debug", StringComparison.OrdinalIgnoreCase);

        return new ApkVerifyResult(verified, debugSigned, output);
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
