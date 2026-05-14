namespace BuildChatBot.Build;

/// <summary>
/// FIFO single-slot semaphore. Reports queue position when the caller has to wait.
/// </summary>
public sealed class BuildQueue
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private int _waiting; // pending callers, excluding the active one

    public int CurrentWaiting => Volatile.Read(ref _waiting);

    /// <summary>
    /// Returns a disposable lock token. The <paramref name="onPosition"/> callback is invoked
    /// with the caller's queue position the moment we know there's contention (position &gt; 0).
    /// </summary>
    public async Task<IDisposable> AcquireAsync(Func<int, Task>? onPosition, CancellationToken ct)
    {
        var position = Interlocked.Increment(ref _waiting) - 1;

        try
        {
            if (position > 0 && onPosition is not null)
                await onPosition(position).ConfigureAwait(false);

            await _slot.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _waiting);
            throw;
        }

        Interlocked.Decrement(ref _waiting);
        return new Releaser(_slot);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                sem.Release();
        }
    }
}
