namespace Lexio.Services;

/// <summary>Dispatches UI work without falling back to the caller thread during queue shutdown.</summary>
internal static class UiDispatcher
{
    public static Task DispatchAsync(bool hasThreadAccess, Func<Action, bool> tryEnqueue, Action action)
    {
        if (hasThreadAccess)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception error)
            {
                return Task.FromException(error);
            }
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!tryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }))
        {
            completion.TrySetResult();
        }

        return completion.Task;
    }

    public static Task DispatchAsync(bool hasThreadAccess, Func<Action, bool> tryEnqueue, Func<Task> work)
    {
        if (hasThreadAccess)
        {
            try
            {
                return work();
            }
            catch (Exception error)
            {
                return Task.FromException(error);
            }
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!tryEnqueue(() =>
            {
                Task operation;
                try
                {
                    operation = work();
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                    return;
                }

                _ = CompleteAsync(operation, completion);
            }))
        {
            completion.TrySetResult();
        }

        return completion.Task;
    }

    private static async Task CompleteAsync(Task operation, TaskCompletionSource completion)
    {
        try
        {
            await operation.ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }
}
