using System.Diagnostics;
using BuildChatBot.Config;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Build;

public sealed record PullResult(string HeadSha, bool WasUpdated, string Branch);

/// <summary>
/// Shells out to the system <c>git</c> binary for fetch + fast-forward so SSH agents,
/// credential helpers, and ~/.gitconfig all work out of the box. Uses LibGit2Sharp only
/// to read the current HEAD SHA before and after.
///
/// Why not LibGit2Sharp end-to-end? The bundled libgit2 binaries don't link against
/// libssh2 on most platforms, so SSH remotes simply fail. Delegating to the CLI keeps
/// private-repo support working with zero extra config.
/// </summary>
public sealed class GitService(BotConfig config, ILogger<GitService> log)
{
    public PullResult Pull()
    {
        if (!Repository.IsValid(config.RepoPath))
            throw new InvalidOperationException($"REPO_PATH is not a Git repository: {config.RepoPath}");

        // Fail fast with a friendly message if REPO_REMOTE_NAME isn't configured locally.
        // Stock git would say "'origin' does not appear to be a git repository", which is
        // confusing — the local repo IS valid, it just has no remote of that name.
        using (var repo = new Repository(config.RepoPath))
        {
            if (repo.Network.Remotes[config.RepoRemoteName] is null)
            {
                var existing = repo.Network.Remotes.Select(r => $"{r.Name} → {r.Url}").ToList();
                var have = existing.Count == 0 ? "(none configured)" : string.Join(", ", existing);
                throw new InvalidOperationException(
                    $"Remote '{config.RepoRemoteName}' is not configured in {config.RepoPath}. " +
                    $"Existing remotes: {have}.\n" +
                    $"Fix: cd \"{config.RepoPath}\" && git remote add {config.RepoRemoteName} <url> && " +
                    $"git branch --set-upstream-to={config.RepoRemoteName}/{config.RepoBranch} {config.RepoBranch}");
            }
        }

        var shaBefore = ReadHeadSha();

        Run("git", new[] { "fetch", config.RepoRemoteName, "--prune" });
        Run("git", new[] { "checkout", config.RepoBranch });
        Run("git", new[] { "pull", "--ff-only", config.RepoRemoteName, config.RepoBranch });

        var shaAfter = ReadHeadSha();
        var updated = !string.Equals(shaBefore, shaAfter, StringComparison.Ordinal);

        log.LogInformation("git pull complete. updated={Updated} head={Sha}", updated, shaAfter[..7]);
        return new PullResult(shaAfter, updated, config.RepoBranch);
    }

    private string ReadHeadSha()
    {
        using var repo = new Repository(config.RepoPath);
        return repo.Head.Tip?.Sha ?? throw new InvalidOperationException("Repository has no HEAD commit.");
    }

    /// <summary>Runs <paramref name="file"/> in the repo and returns its captured output.</summary>
    private (string Stdout, string Stderr) Run(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = config.RepoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // The bot has no terminal, so git must never stop to ask a human anything.
        // Without this, a passphrase-protected key or an HTTPS remote makes the build hang forever.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        var sshOptions = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.RepoSshKeyPath))
        {
            sshOptions.Add($"-i \"{config.RepoSshKeyPath}\"");
            sshOptions.Add("-o IdentitiesOnly=yes");
        }

        if (!string.IsNullOrWhiteSpace(config.RepoSshPassphrase))
        {
            // Feed the passphrase through SSH_ASKPASS. The passphrase reaches ssh through the
            // child's environment only — the helper script on disk contains no secret.
            // BatchMode must stay off here: it would disable askpass along with the prompt.
            psi.Environment["SSH_ASKPASS"] = EnsureAskPassScript();
            psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";   // OpenSSH 8.4+; ignores the missing DISPLAY
            psi.Environment["DISPLAY"] = ":0";                   // older OpenSSH still requires DISPLAY to be set
            psi.Environment[AskPassEnvVar] = config.RepoSshPassphrase;
        }
        else
        {
            // No passphrase configured: fail fast with a readable error instead of blocking on a prompt.
            sshOptions.Add("-o BatchMode=yes");
        }

        if (sshOptions.Count > 0)
            psi.Environment["GIT_SSH_COMMAND"] = "ssh " + string.Join(' ', sshOptions);

        log.LogInformation("$ {File} {Args}", file, string.Join(' ', args));
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{file}'.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var msg = $"git {string.Join(' ', args)} failed (exit {proc.ExitCode}).\nstdout:\n{stdout}\nstderr:\n{stderr}";
            throw new InvalidOperationException(msg + AuthHint(stderr));
        }

        return (stdout, stderr);
    }

    /// <summary>True when <paramref name="relativePath"/> has uncommitted (staged or unstaged) changes.</summary>
    public bool HasLocalChanges(string relativePath)
    {
        var (stdout, _) = Run("git", new[] { "status", "--porcelain", "--", relativePath });
        return !string.IsNullOrWhiteSpace(stdout);
    }

    /// <summary>
    /// Restores the given paths from HEAD (<c>git checkout -- &lt;paths&gt;</c>). Used to revert the
    /// release script's pubspec.yaml bump when the build that it described never completed.
    /// </summary>
    public void DiscardChanges(params string[] relativePaths)
    {
        if (relativePaths.Length == 0) return;

        var args = new List<string> { "checkout", "--" };
        args.AddRange(relativePaths);
        Run("git", args);
        log.LogInformation("git checkout -- {Paths} (local changes discarded)", string.Join(", ", relativePaths));
    }

    /// <summary>
    /// Stages, commits and pushes the given paths to <c>REPO_REMOTE_NAME</c>/<c>REPO_BRANCH</c> using the
    /// configured release author. No-ops when none of the paths changed. Throws
    /// <see cref="InvalidOperationException"/> with the git output when any step fails.
    /// </summary>
    public void CommitAndPush(IReadOnlyList<string> relativePaths, string message)
    {
        if (relativePaths.Count == 0) return;

        var addArgs = new List<string> { "add", "--" };
        addArgs.AddRange(relativePaths);
        Run("git", addArgs);

        if (!relativePaths.Any(HasLocalChanges))
        {
            log.LogInformation("Nothing to commit for {Paths} — skipping commit + push.",
                string.Join(", ", relativePaths));
            return;
        }

        var commitArgs = new List<string>
        {
            "-c", $"user.name={config.ReleaseGitAuthorName}",
            "-c", $"user.email={config.ReleaseGitAuthorEmail}",
            "commit", "-m", message, "--",
        };
        commitArgs.AddRange(relativePaths);
        Run("git", commitArgs);

        Run("git", new[] { "push", config.RepoRemoteName, $"HEAD:{config.RepoBranch}" });
        log.LogInformation("Pushed {Paths} to {Remote}/{Branch}: {Message}",
            string.Join(", ", relativePaths), config.RepoRemoteName, config.RepoBranch, message);
    }
    private const string AskPassEnvVar = "BUILDCHATBOT_SSH_PASSPHRASE";

    /// <summary>
    /// Writes (once) the tiny helper that OpenSSH calls to obtain the key passphrase. The script
    /// only echoes an environment variable, so the passphrase is never stored on disk.
    /// </summary>
    private string EnsureAskPassScript()
    {
        Directory.CreateDirectory(config.TempDirBase);
        var path = Path.GetFullPath(Path.Combine(config.TempDirBase, "ssh-askpass.sh"));
        var body = $"#!/bin/sh\nprintf '%s\\n' \"${AskPassEnvVar}\"\n";

        if (!File.Exists(path) || File.ReadAllText(path) != body)
            File.WriteAllText(path, body);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return path;
    }

    /// <summary>Turns the common SSH authentication failures into an actionable next step.</summary>
    private string AuthHint(string stderr)
    {
        if (stderr.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Permission denied (publickey)", StringComparison.OrdinalIgnoreCase))
        {
            return "\n\nHint: the SSH key needs a passphrase but the bot has no terminal. Either\n"
                 + "  a) add the key to the agent once:  ssh-add --apple-use-keychain "
                 + $"{config.RepoSshKeyPath ?? "<key>"}\n"
                 + "  b) or set REPO_SSH_PASSPHRASE in env.local, or\n"
                 + "  c) or remove the passphrase:  ssh-keygen -p -N \"\" -f "
                 + $"{config.RepoSshKeyPath ?? "<key>"}";
        }

        if (stderr.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
            return "\n\nHint: the remote's host key is not known yet. Connect once by hand on the "
                 + "build host to accept it, e.g. `ssh -T git@github.com`.";

        return "";
    }
}
