using BuildChatBot.Build;
using BuildChatBot.Config;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace BuildChatBot.Telegram;

/// <summary>
/// Receives Telegram updates and dispatches recognised commands to the orchestrator.
/// </summary>
public sealed class UpdateRouter(
    BuildOrchestrator orchestrator,
    BotConfig config,
    ILogger<UpdateRouter> log) : IUpdateHandler
{
    public async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } text } message) return;
        var chatId = message.Chat.Id;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/')) return;

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
                await client.SendMessage(chatId,
                    "BuildChatBot — Flutter Android build bot.\n\n" +
                    "Commands:\n" +
                    "  /build_android — build a release APK from the configured branch\n" +
                    "  /status — show current build state\n" +
                    "  /version — show bot + config version",
                    cancellationToken: ct).ConfigureAwait(false);
                break;

            case "/build_android":
                // Fire-and-forget so the polling loop isn't blocked. Errors get reported to the chat.
                _ = Task.Run(() => orchestrator.RunAsync(chatId, ct), ct);
                break;

            case "/status":
                await client.SendMessage(chatId, orchestrator.DescribeState(), cancellationToken: ct).ConfigureAwait(false);
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

    public Task HandleErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        log.LogError(exception, "Polling error from {Source}", source);
        return Task.CompletedTask;
    }

    private static string ThisAssemblyVersion() =>
        typeof(UpdateRouter).Assembly.GetName().Version?.ToString() ?? "dev";
}
