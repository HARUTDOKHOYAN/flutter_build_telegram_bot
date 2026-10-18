using System.Diagnostics;
using BuildChatBot.Config;

namespace BuildChatBot.Build;

/// <summary>
/// The environment every build child process gets (release.sh, flutter, gradlew).
///
/// Why this exists (2026-10-18): on the server Mac the bot runs as a launchd LaunchAgent,
/// which starts it with a bare environment — a short PATH and often no UTF-8 locale. The
/// release preflight then printed <c>ok flutter: </c> with an empty version, and the iOS
/// build runs <c>pod install</c> (two new pods landed that day), which CocoaPods refuses
/// without a UTF-8 <c>LANG</c> and cannot do at all when <c>pod</c> lives in a gem bin
/// folder that is not on PATH. One helper so the release script, the Android build and
/// the Gradle clean can no longer drift apart (only release.sh had PATH handling before).
/// </summary>
public static class ToolEnvironment
{
    /// <summary>Applies JAVA_HOME / ANDROID_SDK_ROOT, a complete PATH, a UTF-8 locale and HOME.</summary>
    public static void Apply(ProcessStartInfo psi, BotConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.JavaHome))
            psi.Environment["JAVA_HOME"] = config.JavaHome;
        if (!string.IsNullOrWhiteSpace(config.AndroidSdkRoot))
        {
            psi.Environment["ANDROID_SDK_ROOT"] = config.AndroidSdkRoot;
            psi.Environment["ANDROID_HOME"] = config.AndroidSdkRoot;
        }

        psi.Environment["PATH"] = BuildPath(
            psi.Environment.TryGetValue("PATH", out var p) ? p : null, config);

        if (OperatingSystem.IsWindows()) return;

        // CocoaPods aborts with "CocoaPods requires your terminal to be using UTF-8 encoding"
        // when LANG is unset, which is the launchd default. Only fill what is missing: a
        // locale the operator chose in env.local or the plist wins.
        if (IsBlank(psi, "LANG")) psi.Environment["LANG"] = "en_US.UTF-8";
        if (IsBlank(psi, "LC_ALL")) psi.Environment["LC_ALL"] = psi.Environment["LANG"];

        // flutter, pod and git all read HOME (pub cache, CocoaPods specs repo, ~/.gitconfig).
        if (IsBlank(psi, "HOME"))
            psi.Environment["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// <c>EXTRA_PATH</c> first (the operator's word wins), then the inherited PATH, then the
    /// Flutter bin folder and every standard tool folder that exists on this machine.
    /// </summary>
    public static string BuildPath(string? current, BotConfig config)
    {
        var parts = new List<string>();

        void Add(string? dir, bool mustExist)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            dir = dir.Trim();
            if (mustExist && !Directory.Exists(dir)) return;
            if (!parts.Contains(dir, StringComparer.Ordinal)) parts.Add(dir);
        }

        foreach (var d in Split(config.ExtraPath)) Add(ExpandHome(d), mustExist: false);
        foreach (var d in Split(current)) Add(d, mustExist: false);

        if (Path.IsPathRooted(config.FlutterBin))
            Add(Path.GetDirectoryName(config.FlutterBin), mustExist: false);

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var d in new[]
                     {
                         "/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin",
                         "/usr/bin", "/bin", "/usr/sbin", "/sbin",
                         // Common Flutter install folders, for FLUTTER_BIN=flutter setups.
                         Path.Combine(home, "development/flutter/bin"),
                         Path.Combine(home, "flutter/bin"),
                         Path.Combine(home, "fvm/default/bin"),
                         // Where `pod` usually lives besides Homebrew.
                         Path.Combine(home, ".rbenv/shims"),
                         Path.Combine(home, ".rvm/bin"),
                         Path.Combine(home, ".gem/bin"),
                         "/opt/homebrew/opt/ruby/bin",
                         "/usr/local/opt/ruby/bin",
                     })
                Add(d, mustExist: true);

            // gem install --user-install / Homebrew ruby gem bins are versioned folders.
            foreach (var pattern in new[]
                     {
                         (Root: Path.Combine(home, ".gem/ruby"), Sub: "bin"),
                         (Root: "/opt/homebrew/lib/ruby/gems", Sub: "bin"),
                         (Root: "/usr/local/lib/ruby/gems", Sub: "bin"),
                     })
            {
                if (!Directory.Exists(pattern.Root)) continue;
                foreach (var v in Directory.GetDirectories(pattern.Root).OrderDescending())
                    Add(Path.Combine(v, pattern.Sub), mustExist: true);
            }
        }

        return string.Join(Path.PathSeparator, parts);
    }

    private static IEnumerable<string> Split(string? path) =>
        (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ExpandHome(string dir) =>
        dir.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dir[2..])
            : dir;

    private static bool IsBlank(ProcessStartInfo psi, string key) =>
        !psi.Environment.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v);
}
