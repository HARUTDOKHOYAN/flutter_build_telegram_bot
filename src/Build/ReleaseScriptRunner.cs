using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BuildChatBot.Config;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Build;

/// <summary>
/// Runs the Flutter repo's <c>tool/release.sh</c> via <c>bash</c>, streaming every line to
/// <c>logs/release-&lt;buildId&gt;.log</c> and mirroring the script's <c>==&gt; Step</c> markers
/// back to the caller through <c>onStep</c>.
///
/// The script colours its output, so every line is stripped of ANSI escapes before it is
/// logged or parsed (TERM=dumb / NO_COLOR=1 are set too, but the script hard-codes the
/// escapes rather than asking terminfo). No timeout is applied: an iOS archive + export
/// routinely takes 20-40 minutes.
/// </summary>
public sealed class ReleaseScriptRunner(BotConfig config, ILogger<ReleaseScriptRunner> log) : IReleaseScriptRunner
{
    private const int TailLines = 40;
    private const int MaxLastLineChars = 3500;
    private const int MaxFailBlockChars = 1200;

    // CSI ("\e[31m") plus the simpler two-character escapes the script's RESET could emit.
    private static readonly Regex AnsiRegex =
        new(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled);

    // step() prints: echo "${BOLD}==> $*${RESET}"  ->  "==> Preflight"
    private static readonly Regex StepRegex =
        new(@"^==>\s*(?<name>.+?)\s*$", RegexOptions.Compiled);

    // "  building as:            1.0.5+13  (versionName/...=1.0.5, versionCode/...=13)"
    private static readonly Regex BuildingAsRegex =
        new(@"^\s*building as:\s*(?<name>\d+\.\d+\.\d+)\+(?<num>\d+)\b", RegexOptions.Compiled);

    // pubspec.yaml: version: 1.0.5+13   (quotes and stray whitespace tolerated)
    private static readonly Regex PubspecVersionRegex =
        new(@"^version:\s*[""']?\s*(?<name>\d+\.\d+\.\d+)(?:\+(?<num>\d+))?",
            RegexOptions.Compiled);

    /// <inheritdoc />
    public (string Version, int BuildNumber) ReadCurrentVersion()
    {
        var pubspec = Path.Combine(config.RepoPath, "pubspec.yaml");
        try
        {
            if (!File.Exists(pubspec)) return ("0.0.0", 0);

            foreach (var raw in File.ReadLines(pubspec))
            {
                if (!raw.StartsWith("version:", StringComparison.Ordinal)) continue;
                var m = PubspecVersionRegex.Match(raw);
                if (!m.Success) return ("0.0.0", 0);
                var num = m.Groups["num"].Success && int.TryParse(m.Groups["num"].Value, out var n) ? n : 0;
                return (m.Groups["name"].Value, num);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read version from {Pubspec}", pubspec);
        }
        return ("0.0.0", 0);
    }

    /// <inheritdoc />
    public async Task<ReleaseScriptResult> RunAsync(
        ReleaseScriptRequest request, string buildId, Func<string, Task>? onStep, CancellationToken ct)
    {
        Directory.CreateDirectory(config.LogDir);
        var logFile = Path.Combine(config.LogDir, $"release-{buildId}.log");
        var script = config.ResolvedReleaseScriptPath;

        if (!File.Exists(script))
        {
            var msg = $"Release script not found at {script}. " +
                      $"Expected RELEASE_SCRIPT_PATH ('{config.ReleaseScriptPath}') " +
                      $"relative to REPO_PATH ('{config.RepoPath}') unless absolute.";
            log.LogError("{Message}", msg);
            TryWriteAllText(logFile, msg);
            var (v, b) = ReadCurrentVersion();
            return new ReleaseScriptResult(false, -1, logFile, v, b, null, null, null, null, msg);
        }

        var args = BuildArguments(script, request);
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            WorkingDirectory = config.RepoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        ApplyEnvironment(psi);

        var commandLine = $"bash {string.Join(' ', args)}";
        log.LogInformation("$ {Command}", commandLine);

        var tail = new Queue<string>(TailLines + 1);
        var failLines = new List<string>();
        string? parsedVersion = null;
        var parsedBuildNumber = 0;

        await using var logStream = new StreamWriter(logFile, append: false, Encoding.UTF8) { AutoFlush = true };
        await logStream.WriteLineAsync($"$ {commandLine}").ConfigureAwait(false);

        var gate = new SemaphoreSlim(1, 1);
        var sw = Stopwatch.StartNew();

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Both streams are drained concurrently with async reads. OutputDataReceived is
        // avoided on purpose: its callbacks can interleave badly with our own writes.
        async Task PumpAsync(StreamReader reader)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;

                var clean = AnsiRegex.Replace(line, string.Empty);
                string? step = null;

                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await logStream.WriteLineAsync(clean).ConfigureAwait(false);
                    log.LogDebug("{Line}", clean);

                    tail.Enqueue(clean);
                    while (tail.Count > TailLines) tail.Dequeue();

                    var trimmed = clean.TrimStart();
                    if (trimmed.StartsWith("FAIL", StringComparison.Ordinal) && failLines.Count < 40)
                        failLines.Add(trimmed);

                    var stepMatch = StepRegex.Match(trimmed);
                    if (stepMatch.Success) step = stepMatch.Groups["name"].Value;

                    if (parsedVersion is null)
                    {
                        var vm = BuildingAsRegex.Match(clean);
                        if (vm.Success)
                        {
                            parsedVersion = vm.Groups["name"].Value;
                            parsedBuildNumber = int.Parse(vm.Groups["num"].Value);
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }

                if (step is not null && onStep is not null)
                {
                    try
                    {
                        await onStep(step).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.LogDebug(ex, "onStep callback threw for step {Step}", step);
                    }
                }
            }
        }

        var pumps = Task.WhenAll(PumpAsync(proc.StandardOutput), PumpAsync(proc.StandardError));

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log.LogWarning("Release build {BuildId} cancelled — killing the process tree.", buildId);
            TryKillTree(proc);
            try { await pumps.ConfigureAwait(false); } catch { /* draining a killed child */ }
            await logStream.WriteLineAsync("# cancelled").ConfigureAwait(false);
            throw;
        }

        await pumps.ConfigureAwait(false);
        sw.Stop();
        var exitCode = proc.ExitCode;
        await logStream.WriteLineAsync($"# exit {exitCode} in {sw.Elapsed}").ConfigureAwait(false);

        if (parsedVersion is null)
        {
            var (v, b) = ReadCurrentVersion();
            parsedVersion = v;
            parsedBuildNumber = b;
        }

        string? distDir = null, ipa = null, aab = null, apk = null;
        if (exitCode == 0 && !request.DryRun)
        {
            distDir = Path.Combine(config.RepoPath, "dist", $"{parsedVersion}+{parsedBuildNumber}");
            if (Directory.Exists(distDir))
            {
                ipa = FirstFile(distDir, "*.ipa");
                aab = FirstFile(distDir, "*.aab");
                apk = FirstFile(distDir, "*.apk");
            }
            else
            {
                log.LogWarning("Release script exited 0 but {DistDir} does not exist.", distDir);
                distDir = null;
            }
        }

        var success = exitCode == 0;
        var lastLines = success && failLines.Count == 0 ? "" : ComposeLastLines(failLines, tail);

        log.LogInformation("release.sh exit {ExitCode} in {Elapsed} (version {Version}+{Build})",
            exitCode, sw.Elapsed, parsedVersion, parsedBuildNumber);

        return new ReleaseScriptResult(
            success, exitCode, logFile, parsedVersion, parsedBuildNumber, distDir, ipa, aab, apk, lastLines);
    }

    private static List<string> BuildArguments(string script, ReleaseScriptRequest request)
    {
        var args = new List<string>
        {
            script,
            "--platform",
            request.Platform switch
            {
                ReleasePlatform.Ios => "ios",
                ReleasePlatform.Android => "android",
                _ => "all",
            },
        };

        if (!string.IsNullOrWhiteSpace(request.Version))
        {
            args.Add("--version");
            args.Add(request.Version!.Trim());
        }
        if (request.BuildNumber is { } bn)
        {
            args.Add("--build-number");
            args.Add(bn.ToString());
        }
        if (request.SkipHealth) args.Add("--skip-health");
        if (request.BuildApk) args.Add("--apk");
        if (request.DryRun) args.Add("--dry-run");
        return args;
    }

    private void ApplyEnvironment(ProcessStartInfo psi)
    {
        if (!string.IsNullOrWhiteSpace(config.JavaHome))
            psi.Environment["JAVA_HOME"] = config.JavaHome;
        if (!string.IsNullOrWhiteSpace(config.AndroidSdkRoot))
        {
            psi.Environment["ANDROID_SDK_ROOT"] = config.AndroidSdkRoot;
            psi.Environment["ANDROID_HOME"] = config.AndroidSdkRoot;
        }

        // The script calls `flutter` (and curl / security / xcodebuild / shasum) by name.
        psi.Environment["PATH"] = BuildPath(psi.Environment.TryGetValue("PATH", out var p) ? p : null);

        // The script hard-codes ANSI escapes, but keep well-behaved children plain too.
        psi.Environment["TERM"] = "dumb";
        psi.Environment["NO_COLOR"] = "1";
    }

    private string BuildPath(string? current)
    {
        var sep = Path.PathSeparator.ToString();
        var parts = (current ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        void Ensure(string dir)
        {
            if (!string.IsNullOrWhiteSpace(dir) &&
                !parts.Any(x => string.Equals(x, dir, StringComparison.Ordinal)))
                parts.Add(dir);
        }

        if (Path.IsPathRooted(config.FlutterBin))
        {
            var flutterDir = Path.GetDirectoryName(config.FlutterBin);
            if (!string.IsNullOrWhiteSpace(flutterDir)) Ensure(flutterDir!);
        }

        if (OperatingSystem.IsMacOS())
        {
            Ensure("/usr/bin");
            Ensure("/bin");
            Ensure("/usr/local/bin");
            Ensure("/opt/homebrew/bin");
        }

        return string.Join(sep, parts);
    }

    private static string? FirstFile(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(Path.GetFullPath)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>FAIL lines first (they are what the user must fix), then the tail of the log.</summary>
    private static string ComposeLastLines(IReadOnlyList<string> failLines, IEnumerable<string> tail)
    {
        var tailText = string.Join('\n', tail).Trim();

        var fails = failLines.Where(f => !tailText.Contains(f, StringComparison.Ordinal)).ToList();
        var failText = fails.Count == 0 ? "" : string.Join('\n', fails).Trim();
        if (failText.Length > MaxFailBlockChars)
            failText = failText[..MaxFailBlockChars];

        var budget = MaxLastLineChars - (failText.Length == 0 ? 0 : failText.Length + 1);
        if (tailText.Length > budget)
            tailText = budget <= 0 ? "" : tailText[^budget..];

        var combined = failText.Length == 0
            ? tailText
            : tailText.Length == 0 ? failText : failText + "\n" + tailText;

        return combined.Trim();
    }

    private void TryKillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not kill the release script process tree.");
        }
    }

    private void TryWriteAllText(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not write {Path}", path);
        }
    }
}
