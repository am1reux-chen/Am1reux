using Lexico.Core.Data;

namespace Lexio.Services;

internal sealed class AutomaticBackupWorker : IAsyncDisposable
{
    private readonly AppDatabase _database;
    private readonly string _backupPath;
    private readonly TimeSpan _interval;
    private readonly Action<Exception> _reportError;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _runTask;
    private int _disposeStarted;

    public AutomaticBackupWorker(AppDatabase database, string backupPath, TimeSpan interval, Action<Exception> reportError)
    {
        _database = database;
        _backupPath = backupPath;
        _interval = interval;
        _reportError = reportError;
        _runTask = RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        bool ownsDispose = Interlocked.CompareExchange(ref _disposeStarted, 1, 0) == 0;
        if (ownsDispose)
            _shutdown.Cancel();

        try
        {
            await _runTask.ConfigureAwait(false);
        }
        finally
        {
            if (ownsDispose)
                _shutdown.Dispose();
        }
    }

    private async Task RunAsync()
    {
        await BackupOnceAsync().ConfigureAwait(false);
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
                await BackupOnceAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task BackupOnceAsync()
    {
        try
        {
            await _database.BackupToAsync(_backupPath).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _reportError(error);
        }
    }
}
