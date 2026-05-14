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

    private void Run(string file, IEnumerable<string> args)
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
    }
}
