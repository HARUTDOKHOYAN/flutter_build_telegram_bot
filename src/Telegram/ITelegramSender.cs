namespace BuildChatBot.Telegram;

/// <summary>
/// Abstracts message + document sending so the orchestrator doesn't care which underlying
/// client (Telegram.Bot vs WTelegramBot) actually handles the request.
/// </summary>
public interface ITelegramSender
{
    Task<int> SendTextAsync(long chatId, string text, CancellationToken ct);

    /// <summary>
    /// Sends text with an inline keyboard. <paramref name="buttons"/> is a list of rows;
    /// each button is a (visible label, callback payload) pair. Pass null/empty for no keyboard.
    /// </summary>
    Task<int> SendTextAsync(long chatId, string text,
        IReadOnlyList<IReadOnlyList<(string Label, string Data)>>? buttons, CancellationToken ct);

    Task EditTextAsync(long chatId, int messageId, string text, CancellationToken ct);

    /// <summary>Strips the inline keyboard off an already-sent message (an answered question).</summary>
    Task RemoveKeyboardAsync(long chatId, int messageId, CancellationToken ct);

    Task SendDocumentAsync(long chatId, string filePath, string fileName, string? caption, CancellationToken ct);
    Task StartAsync(CancellationToken ct);
}
