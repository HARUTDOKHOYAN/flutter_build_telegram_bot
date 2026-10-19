using System.Diagnostics;
using System.Text;
using BuildChatBot.Config;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Build;

public sealed record FlutterBuildResult(bool Success, int ExitCode, string LogFile, string LastError);

/// <summary>
/// The `flutter build apk` arguments for this build, plus a tag naming that recipe for
/// <see cref="ApkCache"/>. <see cref="Error"/> is set when the build must not run at all.
/// </summary>
public sealed record ApkBuildPlan(IReadOnlyList<string> Args, string CacheTag, string? Error);

/// <summary>
/// Wraps `flutter clean`, `flutter pub get`, and `flutter build apk` with streamed logging.
/// </summary>
public sealed class FlutterRunner(BotConfig config, ILogger<FlutterRunner> log)
{
    /// <summary>
    /// Builds the argument list after the pull, so the define file is the one at HEAD. The cache
    /// tag hashes the args and the define file's bytes: an APK built from a different recipe
    /// (including every APK cached before release.env was passed) never matches.
    /// </summary>
    public ApkBuildPlan Plan()
    {
        var args = new List<string>
        {
            "build", "apk", "--release",
            $"--target-platform={config.FlutterTargetPlatform}",
        };
        if (!string.IsNullOrWhiteSpace(config.FlutterFlavor))
            args.Add($"--flavor={config.FlutterFlavor}");
        if (!string.IsNullOrWhiteSpace(config.FlutterTargetFile))
        {
            args.Add("-t");
            args.Add(config.FlutterTargetFile!);
        }

        byte[] defines = [];
        var defineFile = config.ResolvedDartDefineFile;
        if (defineFile is not null)
        {
            if (!File.Exists(defineFile))
                return new ApkBuildPlan(args, "", $"Dart define file not found: {defineFile}. A release APK " +
                    "without it closes at launch. Restore it in the repo, or set FLUTTER_DART_DEFINE_FILE.");
            args.Add($"--dart-define-from-file={defineFile}");
            defines = File.ReadAllBytes(defineFile);
        }

        // Last, so an explicit --dart-define here still wins over the file (Flutter's rule).
        if (!string.IsNullOrWhiteSpace(config.FlutterExtraArgs))
            args.AddRange(SplitArgs(config.FlutterExtraArgs!));

        using var sha = System.Security.Cryptography.SHA256.Create();
        var recipe = Encoding.UTF8.GetBytes(string.Join('\0', args) + '\0');
        sha.TransformBlock(recipe, 0, recipe.Length, null, 0);
        sha.TransformFinalBlock(defines, 0, defines.Length);
        var tag = Convert.ToHexString(sha.Hash!)[..10].ToLowerInvariant();

        return new ApkBuildPlan(args, tag, null);
    }

    public async Task<FlutterBuildResult> BuildReleaseApkAsync(string buildId, ApkBuildPlan plan, CancellationToken ct)
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

        var build = await RunFlutterAsync(plan.Args, logStream, ct);
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

        ToolEnvironment.Apply(psi, config);

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
