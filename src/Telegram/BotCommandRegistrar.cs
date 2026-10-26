using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace BuildChatBot.Telegram;

/// <summary>
/// Publishes the bot's slash-command list to Telegram via <c>setMyCommands</c>.
/// Telegram caches the list server-side and shows it as a popup whenever a user
/// types <c>/</c> or taps the in-chat menu button.
///
/// Keep this list in sync with the switch in <see cref="UpdateRouter"/>.
/// </summary>
public sealed class BotCommandRegistrar(ITelegramBotClient bot, ILogger<BotCommandRegistrar> log)
{
    // Order here is the order users see in the popup.
    private static readonly BotCommand[] Commands =
    {
        new() { Command = "test_build_android", Description = "Build a local APK" },
        new() { Command = "release_ios_testflight", Description = "Upload it to TestFlight" },
        new() { Command = "release_ios_appstore",   Description = "TestFlight to App Store" },
        new() { Command = "release_android_play",   Description = "Google Play Testing" },
        new() { Command = "status",        Description = "Show current build and release state" },
        new() { Command = "help",          Description = "Usage information" },
    };

    public async Task PublishAsync(CancellationToken ct)
    {
        // Publish to multiple scopes so the popup appears reliably:
        //   - default      : every chat that isn't covered by a more specific scope
        //   - private      : 1-1 chats with users
        //   - group        : group + supergroup chats the bot has been added to
        // Channels are intentionally omitted (this bot doesn't make sense there).
        var scopes = new BotCommandScope?[]
        {
            null,                                  // default scope
            new BotCommandScopeAllPrivateChats(),
            new BotCommandScopeAllGroupChats(),
        };

        foreach (var scope in scopes)
        {
            try
            {
                await bot.SetMyCommands(Commands, scope: scope, cancellationToken: ct).ConfigureAwait(false);
                log.LogInformation("Published {Count} commands ({Scope}).",
                    Commands.Length, scope?.GetType().Name ?? "default");
            }
            catch (Exception ex)
            {
                // Non-fatal: the bot still works, users just may not see this scope's popup.
                log.LogWarning(ex, "Failed to publish commands for scope {Scope}.",
                    scope?.GetType().Name ?? "default");
            }
        }
    }
}
