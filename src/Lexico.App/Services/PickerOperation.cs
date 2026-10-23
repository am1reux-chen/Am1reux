namespace Lexio.Services;

internal static class PickerOperation
{
    public static void TryRun(Action operation, Action<Exception> onError)
    {
        try
        {
            operation();
        }
        catch (Exception error)
        {
            onError(error);
        }
    }

    public static async Task TryRunAsync(Func<Task> operation, Func<Exception, Task> onError)
    {
        try
        {
            await operation();
        }
        catch (Exception error)
        {
            await onError(error);
        }
    }

    public static async Task<T?> TryRunAsync<T>(Func<Task<T?>> operation, Func<Exception, Task> onError,
        Action? onNoSelection = null)
        where T : class
    {
        try
        {
            var result = await operation();
            if (result is null)
                onNoSelection?.Invoke();
            return result;
        }
        catch (Exception error)
        {
            await onError(error);
            return null;
        }
    }
}
