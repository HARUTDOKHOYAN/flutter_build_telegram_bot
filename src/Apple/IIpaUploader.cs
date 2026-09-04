namespace BuildChatBot.Apple;

public sealed record IpaUploadResult(bool Success, string Output);

/// <summary>
/// Uploads an .ipa to App Store Connect (the build then appears in TestFlight after processing).
/// Implementation: <c>xcrun altool --upload-app</c> authenticated with the App Store Connect API key.
/// </summary>
public interface IIpaUploader
{
    /// <param name="ipaPath">Absolute path to the .ipa produced by <c>flutter build ipa</c>.</param>
    /// <param name="onLog">Optional callback receiving each stdout/stderr line as it arrives.</param>
    Task<IpaUploadResult> UploadAsync(string ipaPath, Action<string>? onLog, CancellationToken ct);
}
