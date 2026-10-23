namespace Lexio.Services;

/// <summary>Serializes a group of async operations and can stop accepting work before draining it.</summary>
internal sealed class AsyncOperationGate
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private int _pending;
    private bool _stopping;
    private TaskCompletionSource? _drained;

    public Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        });
    }

    public async Task<TResult> RunAsync<TResult>(Func<Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Register();
        try
        {
            await _serial.WaitAsync().ConfigureAwait(false);
            try
            {
                return await operation().ConfigureAwait(false);
            }
            finally
            {
                _serial.Release();
            }
        }
        finally
        {
            Complete();
        }
    }

    public Task StopAndWaitAsync()
    {
        lock (_sync)
        {
            _stopping = true;
            return _pending == 0
                ? Task.CompletedTask
                : (_drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    private void Register()
    {
        lock (_sync)
        {
            if (_stopping)
                throw new ObjectDisposedException(nameof(AsyncOperationGate), "The operation gate is stopping.");
            _pending++;
        }
    }

    private void Complete()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            if (--_pending == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }
        drained?.TrySetResult();
    }
}
