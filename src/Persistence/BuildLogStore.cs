using System.Text.Json;
using BuildChatBot.Config;

namespace BuildChatBot.Persistence;

/// <param name="Kind">Which command produced this record: "test_apk", "ios_testflight" or "ios_appstore".</param>
/// <param name="Notes">Free-form extra context — dist directory, review submission id, warnings.</param>
public sealed record BuildRecord(
    string Kind,
    string BuildId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long ChatId,
    string Branch,
    string CommitSha,
    string VersionName,
    string VersionCode,
    bool FromCache,
    bool SignatureVerified,
    bool Success,
    long ApkSizeBytes,
    long DurationMs,
    string? Error,
    string? Notes = null);

/// <summary>
/// Appends one JSON line per build to a file (JSONL — easy to grep, easy to ship).
/// </summary>
public sealed class BuildLogStore(BotConfig config)
{
    private readonly object _gate = new();

    public void Append(BuildRecord record)
    {
        var dir = Path.GetDirectoryName(config.BuildRecordPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(record);
        lock (_gate)
        {
            File.AppendAllText(config.BuildRecordPath, json + Environment.NewLine);
        }
    }
}
