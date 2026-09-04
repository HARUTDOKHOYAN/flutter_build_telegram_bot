using System.Collections.Concurrent;
using BuildChatBot.Config;
using BuildChatBot.Telegram;
using Microsoft.Extensions.Logging;

namespace BuildChatBot.Release;

/// <summary>
/// Per-chat "one pending question at a time" state machine used by the release flows.
///
/// A prompt is created by <see cref="AskTextAsync"/>, which sends the question (optionally with an
/// inline keyboard) and then awaits the answer. The answer arrives from <see cref="UpdateRouter"/>
/// either as a plain text message or as an inline-keyboard callback payload, both funnelled through
/// <see cref="TryAnswer"/>. A prompt is abandoned by <c>/cancel</c> (<see cref="Cancel"/>), by a new
/// release command (<see cref="Displace"/>), or by the configured timeout — in every one of those
/// cases the awaiting flow gets <c>null</c> back and must stop.
/// </summary>
public sealed class ReleaseConversation(
    BotConfig config,
    ITelegramSender sender,
    ILogger<ReleaseConversation> log)
{
    private readonly ConcurrentDictionary<long, PendingPrompt> _pending = new();

    /// <summary>True when this chat is currently waiting for the user to answer something.</summary>
    public bool HasPending(long chatId) => _pending.ContainsKey(chatId);

    /// <summary>
    /// Asks a question and waits for the answer. Returns the message text the user typed, or the
    /// callback payload of the button they tapped. Returns null on timeout, <c>/cancel</c>,
    /// displacement by another release command, or host shutdown.
    /// </summary>
    public async Task<string?> AskTextAsync(long chatId, string question,
        IEnumerable<(string Label, string Data)>? buttons, CancellationToken ct)
    {
        // Defensive: a flow should have called Displace() first, but never leave two prompts alive.
        Abandon(chatId);

        var messageId = await sender.SendTextAsync(chatId, question, Layout(buttons), ct).ConfigureAwait(false);

        var prompt = new PendingPrompt(messageId);
        _pending[chatId] = prompt;

        using var reg = ct.Register(static state => ((PendingPrompt)state!).Complete(null), prompt);

        var timeout = Task.Delay(TimeSpan.FromSeconds(config.ReleasePromptTimeoutSeconds), CancellationToken.None);
        var winner = await Task.WhenAny(prompt.Answer, timeout).ConfigureAwait(false);

        if (winner != prompt.Answer)
        {
            // Timed out. Only report it if we're still the live prompt for this chat.
            if (_pending.TryRemove(new KeyValuePair<long, PendingPrompt>(chatId, prompt)))
            {
                await sender.RemoveKeyboardAsync(chatId, messageId, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await sender.SendTextAsync(chatId, "⌛ Timed out, nothing was built.", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Could not send the prompt-timeout notice to chat {Chat}.", chatId);
                }
            }
            prompt.Complete(null);
            return null;
        }

        return await prompt.Answer.ConfigureAwait(false);
    }

    /// <summary>
    /// Feeds an answer (message text or callback payload) to this chat's pending prompt.
    /// Returns false when nothing was pending, in which case the caller should ignore the input.
    /// </summary>
    public bool TryAnswer(long chatId, string answer)
    {
        if (!_pending.TryRemove(chatId, out var prompt)) return false;
        ClearKeyboard(chatId, prompt.MessageId);
        prompt.Complete(answer);
        return true;
    }

    /// <summary>Handles <c>/cancel</c>. Returns false when there was nothing to cancel.</summary>
    public bool Cancel(long chatId) => Abandon(chatId);

    /// <summary>
    /// Drops any pending prompt because a new release command is starting.
    /// Returns true when a prompt was actually discarded (the caller tells the user).
    /// </summary>
    public bool Displace(long chatId) => Abandon(chatId);

    private bool Abandon(long chatId)
    {
        if (!_pending.TryRemove(chatId, out var prompt)) return false;
        ClearKeyboard(chatId, prompt.MessageId);
        prompt.Complete(null);
        return true;
    }

    /// <summary>Fire-and-forget keyboard removal — an answered question shouldn't keep tappable buttons.</summary>
    private void ClearKeyboard(long chatId, int messageId) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await sender.RemoveKeyboardAsync(chatId, messageId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Could not remove the keyboard from message {Msg} in chat {Chat}.", messageId, chatId);
            }
        });

    /// <summary>
    /// One row for a two-button question (e.g. [Keep 1.0.5] [Cancel]); one button per row for
    /// longer lists (e.g. the TestFlight build picker), where labels are wide.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<(string Label, string Data)>>? Layout(
        IEnumerable<(string Label, string Data)>? buttons)
    {
        if (buttons is null) return null;
        var flat = buttons.ToList();
        if (flat.Count == 0) return null;

        var rows = new List<IReadOnlyList<(string Label, string Data)>>();
        if (flat.Count <= 2)
        {
            rows.Add(flat);
        }
        else
        {
            foreach (var b in flat) rows.Add(new List<(string Label, string Data)> { b });
        }
        return rows;
    }

    private sealed class PendingPrompt(int messageId)
    {
        private readonly TaskCompletionSource<string?> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MessageId { get; } = messageId;
        public Task<string?> Answer => _tcs.Task;
        public void Complete(string? answer) => _tcs.TrySetResult(answer);
    }
}
