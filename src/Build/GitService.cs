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

        // Honour the configured SSH key if the user pointed at one.
        if (!string.IsNullOrWhiteSpace(config.RepoSshKeyPath))
        {
            psi.Environment["GIT_SSH_COMMAND"] =
                $"ssh -i \"{config.RepoSshKeyPath}\" -o IdentitiesOnly=yes";
        }

        log.LogInformation("$ {File} {Args}", file, string.Join(' ', args));
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{file}'.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            var msg = $"git {string.Join(' ', args)} failed (exit {proc.ExitCode}).\nstdout:\n{stdout}\nstderr:\n{stderr}";
            throw new InvalidOperationException(msg);
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
}
