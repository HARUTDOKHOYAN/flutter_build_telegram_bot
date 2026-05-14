using BuildChatBot.Config;
using YamlDotNet.RepresentationModel;

namespace BuildChatBot.Build;

public sealed record AppVersion(string Name, string Code);

/// <summary>
/// Reads pubspec.yaml's `version: X.Y.Z+N` and produces user-friendly filenames.
/// </summary>
public sealed class ArtifactNamer(BotConfig config)
{
    public AppVersion ReadAppVersion()
    {
        var pubspec = Path.Combine(config.RepoPath, "pubspec.yaml");
        if (!File.Exists(pubspec)) return new AppVersion("0.0.0", "0");

        try
        {
            using var reader = new StreamReader(pubspec);
            var yaml = new YamlStream();
            yaml.Load(reader);
            var root = (YamlMappingNode)yaml.Documents[0].RootNode;
            if (!root.Children.TryGetValue(new YamlScalarNode("version"), out var versionNode))
                return new AppVersion("0.0.0", "0");
            var raw = ((YamlScalarNode)versionNode).Value ?? "0.0.0";
            var plus = raw.IndexOf('+');
            return plus < 0
                ? new AppVersion(raw.Trim(), "0")
                : new AppVersion(raw[..plus].Trim(), raw[(plus + 1)..].Trim());
        }
        catch
        {
            return new AppVersion("0.0.0", "0");
        }
    }

    public string BuildFilename(AppVersion version, string sha)
    {
        var flavor = string.IsNullOrWhiteSpace(config.FlutterFlavor) ? "main" : config.FlutterFlavor;
        var shortSha = sha.Length >= 7 ? sha[..7] : sha;
        return $"app-{flavor}-{version.Name}+{version.Code}-{shortSha}.apk";
    }
}
