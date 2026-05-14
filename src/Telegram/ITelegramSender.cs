namespace BuildChatBot.Telegram;

/// <summary>
/// Abstracts message + document sending so the orchestrator doesn't care which underlying
/// client (Telegram.Bot vs WTelegramBot) actually handles the request.
/// </summary>
public interface ITelegramSender
{
    Task<int> SendTextAsync(long chatId, string text, CancellationToken ct);
    Task EditTextAsync(long chatId, int messageId, string text, CancellationToken ct);
    Task SendDocumentAsync(long chatId, string filePath, string fileName, string? caption, CancellationToken ct);
    Task StartAsync(CancellationToken ct);
}
