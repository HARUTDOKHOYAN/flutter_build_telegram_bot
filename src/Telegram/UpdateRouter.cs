using BuildChatBot.Build;
using BuildChatBot.Config;
using BuildChatBot.Release;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;

namespace BuildChatBot.Telegram;

/// <summary>
/// Receives Telegram updates and dispatches them:
///   - inline-keyboard callbacks → acknowledged, then handed to <see cref="ReleaseConversation"/>;
///   - plain text               → the pending question for that chat, when there is one;
///   - <c>/commands</c>         → the orchestrators (long-running ones on a background task).
/// </summary>
public sealed class UpdateRouter(
    BuildOrchestrator orchestrator,
    ReleaseOrchestrator release,
    ReleaseConversation conversation,
    BotConfig config,
    ILogger<UpdateRouter> log) : IUpdateHandler
{
    private const string HelpText =
        "BuildChatBot — Flutter build & release bot.\n\n" +
        "Commands:\n" +
        "  /test_build_android — build a test APK (side-load) from the configured branch\n" +
        "  /release_ios_testflight — build iOS and upload it to TestFlight\n" +
        "  /release_ios_appstore — submit a TestFlight build to App Store review (auto-release after approval)\n" +
        "  /release_android_play — build the Android .aab and roll it out to Google Play internal testing\n" +
        "  /cancel — cancel the question I'm currently waiting on\n" +
        "  /status — show current build and release state\n" +
        "  /version — show bot + config version\n" +
        "  /help — this message\n\n" +
        "The release commands ask you a few questions first. Answer by tapping a button or " +
        "sending a plain message.";

    public async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(client, callback, ct).ConfigureAwait(false);
            return;
        }

        if (update.Message is not { Text: { } text } message) return;
        var chatId = message.Chat.Id;
        var trimmed = text.Trim();

        if (!trimmed.StartsWith('/'))
        {
            // Plain text is only meaningful as the answer to a pending question; otherwise ignore it.
            if (conversation.TryAnswer(chatId, trimmed))
                log.LogInformation("Chat {Chat} answered a pending prompt with a text message.", chatId);
            return;
        }

        // Strip optional @botname suffix on commands.
        var spaceIdx = trimmed.IndexOf(' ');
        var head = spaceIdx < 0 ? trimmed : trimmed[..spaceIdx];
        var atIdx = head.IndexOf('@');
        if (atIdx > 0) head = head[..atIdx];

        log.LogInformation("Received {Command} from chat {Chat} (user {User})",
            head, chatId, message.From?.Username ?? message.From?.Id.ToString() ?? "?");

        switch (head)
        {
            case "/start":
            case "/help":
                await client.SendMessage(chatId, HelpText, cancellationToken: ct).ConfigureAwait(false);
                break;

            case "/test_build_android":
                // Fire-and-forget so the polling loop isn't blocked. Errors get reported to the chat.
                _ = Task.Run(() => orchestrator.RunAsync(chatId, ct), ct);
                break;

            case "/release_ios_testflight":
                _ = Task.Run(() => release.RunTestFlightAsync(chatId, ct), ct);
                break;

            case "/release_ios_appstore":
                _ = Task.Run(() => release.RunAppStoreAsync(chatId, ct), ct);
                break;

            case "/release_android_play":
                _ = Task.Run(() => release.RunPlayAsync(chatId, ct), ct);
                break;

            case "/cancel":
                await client.SendMessage(chatId,
                    conversation.Cancel(chatId)
                        ? "Cancelled."
                        : "Nothing to cancel — I'm not waiting on an answer. (This does not stop a running build.)",
                    cancellationToken: ct).ConfigureAwait(false);
                break;

            case "/status":
                await client.SendMessage(chatId, DescribeStatus(chatId), cancellationToken: ct).ConfigureAwait(false);
                break;

            case "/version":
                await client.SendMessage(chatId,
                    $"BuildChatBot {ThisAssemblyVersion()}\n" +
                    $"branch={config.RepoBranch} flavor={config.FlutterFlavor ?? "(none)"} platform={config.FlutterTargetPlatform}",
                    cancellationToken: ct).ConfigureAwait(false);
                break;

            default:
                await client.SendMessage(chatId, $"Unknown command: {head}. Try /help.", cancellationToken: ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleCallbackAsync(ITelegramBotClient client, CallbackQuery callback, CancellationToken ct)
    {
        // Always answer first — otherwise the button keeps spinning in the client.
        try
        {
            await client.AnswerCallbackQuery(callback.Id, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "AnswerCallbackQuery failed for {Id}", callback.Id);
        }

        var chatId = callback.Message?.Chat.Id;
        if (chatId is null || callback.Data is null) return;

        log.LogInformation("Callback {Data} from chat {Chat}", callback.Data, chatId);
        if (!conversation.TryAnswer(chatId.Value, callback.Data))
            log.LogDebug("Callback {Data} arrived with no pending prompt for chat {Chat}.", callback.Data, chatId);
    }

    private string DescribeStatus(long chatId)
    {
        var lines = new List<string>
        {
            $"Test build: {orchestrator.DescribeState()}",
            $"Release: {release.DescribeState()}",
        };
        if (conversation.HasPending(chatId))
            lines.Add("Waiting for your answer to a question — /cancel to drop it.");
        return string.Join('\n', lines);
    }

    public Task HandleErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        log.LogError(exception, "Polling error from {Source}", source);
        return Task.CompletedTask;
    }

    private static string ThisAssemblyVersion() =>
        typeof(UpdateRouter).Assembly.GetName().Version?.ToString() ?? "dev";
}
