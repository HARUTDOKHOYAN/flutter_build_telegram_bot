using System.Diagnostics;
using System.Text;
using BuildChatBot.Config;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Build;

public sealed record FlutterBuildResult(bool Success, int ExitCode, string LogFile, string LastError);

/// <summary>
/// Wraps `flutter clean`, `flutter pub get`, and `flutter build apk` with streamed logging.
/// </summary>
public sealed class FlutterRunner(BotConfig config, ILogger<FlutterRunner> log)
{
    public async Task<FlutterBuildResult> BuildReleaseApkAsync(string buildId, CancellationToken ct)
    {
        Directory.CreateDirectory(config.LogDir);
        var logFile = Path.Combine(config.LogDir, $"build-{buildId}.log");
        await using var logStream = new StreamWriter(logFile, append: false, Encoding.UTF8) { AutoFlush = true };

        if (config.FlutterRunClean)
        {
            var clean = await RunFlutterAsync(new[] { "clean" }, logStream, ct);
            if (clean.ExitCode != 0)
                return new FlutterBuildResult(false, clean.ExitCode, logFile, TailErrors(logFile));
        }

        if (config.FlutterRunPubGet)
        {
            var pub = await RunFlutterAsync(new[] { "pub", "get" }, logStream, ct);
            if (pub.ExitCode != 0)
                return new FlutterBuildResult(false, pub.ExitCode, logFile, TailErrors(logFile));
        }

        var buildArgs = new List<string>
        {
            "build", "apk", "--release",
            $"--target-platform={config.FlutterTargetPlatform}",
        };
        if (!string.IsNullOrWhiteSpace(config.FlutterFlavor))
            buildArgs.Add($"--flavor={config.FlutterFlavor}");
        if (!string.IsNullOrWhiteSpace(config.FlutterTargetFile))
        {
            buildArgs.Add("-t");
            buildArgs.Add(config.FlutterTargetFile!);
        }
        if (!string.IsNullOrWhiteSpace(config.FlutterExtraArgs))
        {
            foreach (var part in SplitArgs(config.FlutterExtraArgs!))
                buildArgs.Add(part);
        }

        var build = await RunFlutterAsync(buildArgs, logStream, ct);
        return new FlutterBuildResult(build.ExitCode == 0, build.ExitCode, logFile,
            build.ExitCode == 0 ? "" : TailErrors(logFile));
    }

    private async Task<(int ExitCode, TimeSpan Duration)> RunFlutterAsync(
        IEnumerable<string> args, StreamWriter logStream, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.FlutterBin,
            WorkingDirectory = config.RepoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        if (!string.IsNullOrWhiteSpace(config.JavaHome))
            psi.Environment["JAVA_HOME"] = config.JavaHome;
        if (!string.IsNullOrWhiteSpace(config.AndroidSdkRoot))
        {
            psi.Environment["ANDROID_SDK_ROOT"] = config.AndroidSdkRoot;
            psi.Environment["ANDROID_HOME"] = config.AndroidSdkRoot;
        }

        var sw = Stopwatch.StartNew();
        log.LogInformation("$ {Bin} {Args}", psi.FileName, string.Join(' ', args));
        await logStream.WriteLineAsync($"$ {psi.FileName} {string.Join(' ', args)}");

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) logStream.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) logStream.WriteLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        sw.Stop();
        await logStream.WriteLineAsync($"# exit {proc.ExitCode} in {sw.Elapsed}");
        return (proc.ExitCode, sw.Elapsed);
    }

    private static string TailErrors(string logFile, int tailLines = 30)
    {
        try
        {
            var lines = File.ReadAllLines(logFile);
            var tail = lines.Skip(Math.Max(0, lines.Length - tailLines));
            return string.Join('\n', tail);
        }
        catch
        {
            return "(unable to read log file)";
        }
    }

    private static IEnumerable<string> SplitArgs(string input)
    {
        // Simple split — respects double-quoted segments. Sufficient for FLUTTER_EXTRA_ARGS.
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in input)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
