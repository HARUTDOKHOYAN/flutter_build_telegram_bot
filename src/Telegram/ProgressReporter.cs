using System.Diagnostics;

namespace BuildChatBot.Telegram;

/// <summary>
/// Edits a status message every N seconds with the latest progress label + elapsed time.
/// Used to keep the user informed during long Flutter builds.
/// </summary>
public sealed class ProgressReporter : IAsyncDisposable
{
    private readonly ITelegramSender _sender;
    private readonly long _chatId;
    private readonly int _messageId;
    private readonly int _intervalSeconds;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;
    private volatile string _status;

    public ProgressReporter(ITelegramSender sender, long chatId, int messageId, int intervalSeconds, string initialStatus)
    {
        _sender = sender;
        _chatId = chatId;
        _messageId = messageId;
        _intervalSeconds = Math.Max(5, intervalSeconds);
        _status = initialStatus;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void SetStatus(string status) => _status = status;

    public async Task UpdateImmediateAsync(string status, CancellationToken ct)
    {
        _status = status;
        await _sender.EditTextAsync(_chatId, _messageId, Render(), ct).ConfigureAwait(false);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct).ConfigureAwait(false);
                await _sender.EditTextAsync(_chatId, _messageId, Render(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    private string Render() => $"{_status}\n⏱ {_stopwatch.Elapsed:mm\\:ss} elapsed";

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { /* swallow */ }
        _cts.Dispose();
    }
}
