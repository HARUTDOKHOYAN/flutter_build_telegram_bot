using BuildChatBot.Config;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using TgFile = Telegram.Bot.Types.InputFile;

namespace BuildChatBot.Telegram;

/// <summary>
/// Sends text and small documents via the standard Bot API; routes &gt;= threshold uploads
/// through WTelegramBot (MTProto, 2 GB limit).
/// </summary>
public sealed class HybridTelegramSender : ITelegramSender, IAsyncDisposable
{
    private readonly BotConfig _config;
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<HybridTelegramSender> _log;
    private readonly long _thresholdBytes;
    private readonly SemaphoreSlim _wtInit = new(1, 1);
    private WTelegram.Bot? _wtClient;
    private SqliteConnection? _wtConnection;

    public HybridTelegramSender(BotConfig config, ITelegramBotClient botClient, ILogger<HybridTelegramSender> log)
    {
        _config = config;
        _botClient = botClient;
        _log = log;
        _thresholdBytes = (long)config.TelegramLargeFileThresholdMb * 1024 * 1024;
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<int> SendTextAsync(long chatId, string text, CancellationToken ct)
    {
        var msg = await _botClient.SendMessage(chatId, text, cancellationToken: ct).ConfigureAwait(false);
        return msg.MessageId;
    }

    public async Task EditTextAsync(long chatId, int messageId, string text, CancellationToken ct)
    {
        try
        {
            await _botClient.EditMessageText(chatId, messageId, text, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Edits frequently no-op when the text is identical or the message is too old; non-fatal.
            _log.LogDebug(ex, "EditMessageText failed (chatId={Chat}, msgId={Msg})", chatId, messageId);
        }
    }

    public async Task SendDocumentAsync(long chatId, string filePath, string fileName, string? caption, CancellationToken ct)
    {
        var size = new FileInfo(filePath).Length;
        if (size < _thresholdBytes)
        {
            await using var fs = File.OpenRead(filePath);
            await _botClient.SendDocument(
                chatId: chatId,
                document: TgFile.FromStream(fs, fileName),
                caption: caption,
                cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        _log.LogInformation("File {File} is {Size:N0} bytes (>= {Threshold:N0}); routing through WTelegramBot.",
            fileName, size, _thresholdBytes);

        var wt = await EnsureWtClientAsync(ct).ConfigureAwait(false);
        await using var stream = File.OpenRead(filePath);
        // WTelegramBot's Bot class re-uses Telegram.Bot's InputFile abstraction.
        // (Its overload doesn't take a CancellationToken — the operation is non-cancelable mid-upload.)
        await wt.SendDocument(
            chatId: chatId,
            document: TgFile.FromStream(stream, fileName),
            caption: caption).ConfigureAwait(false);
    }

    private async Task<WTelegram.Bot> EnsureWtClientAsync(CancellationToken ct)
    {
        if (_wtClient is not null) return _wtClient;

        await _wtInit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_wtClient is not null) return _wtClient;

            Directory.CreateDirectory(_config.LogDir);
            var sessionDb = Path.Combine(_config.LogDir, "wt-session.sqlite");
            _wtConnection = new SqliteConnection($"Data Source={sessionDb}");
            _wtConnection.Open();

            _wtClient = new WTelegram.Bot(
                botToken: _config.TelegramBotToken,
                apiId: _config.TelegramApiId,
                apiHash: _config.TelegramApiHash,
                dbConnection: _wtConnection);
            return _wtClient;
        }
        finally
        {
            _wtInit.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _wtClient?.Dispose();
        _wtConnection?.Dispose();
        return ValueTask.CompletedTask;
    }
}
