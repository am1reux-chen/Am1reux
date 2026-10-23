using Lexio.Services;
using Lexico.Core.Data;
using Xunit;

namespace Lexico.App.Tests;

public sealed class AutomaticBackupWorkerTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lexio-backup-worker-" + Guid.NewGuid().ToString("N"));
    private AppDatabase _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDatabase(Path.Combine(_directory, "worker.db"));
        await _db.MigrateAsync();
    }

    [Fact]
    public async Task DisposeCancelsThePeriodicWaitAfterWritingAnInitialBackup()
    {
        var backupPath = Path.Combine(_directory, "automatic.backup");
        var errors = new List<Exception>();
        var worker = new AutomaticBackupWorker(_db, backupPath, TimeSpan.FromHours(1), errors.Add);

        await worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(File.Exists(backupPath));
        Assert.Empty(errors);
        await using var snapshot = await AppDatabase.OpenWithRecoveryAsync(backupPath, backupPath + ".fallback");
        await snapshot.ValidateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
