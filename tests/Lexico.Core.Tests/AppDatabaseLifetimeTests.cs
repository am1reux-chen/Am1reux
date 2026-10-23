using System.Reflection;
using Lexico.Core.Data;
using Xunit;

namespace Lexico.Core.Tests;

public sealed class AppDatabaseLifetimeTests
{
    [Fact]
    public async Task DisposeDrainsAcceptedOperationsAndImmediatelyRejectsNewOnes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lexio-db-lifetime-" + Guid.NewGuid().ToString("N"));
        var database = new AppDatabase(Path.Combine(directory, "lifetime.db"));
        var operationGate = GetOperationGate(database);
        Task<int>? acceptedOperation = null;
        Task<int>? rejectedOperation = null;
        Task? disposal = null;
        bool ownsGate = false;

        try
        {
            await database.MigrateAsync();
            await operationGate.WaitAsync();
            ownsGate = true;

            acceptedOperation = database.CountWordsAsync();
            disposal = database.DisposeAsync().AsTask();
            rejectedOperation = database.CountWordsAsync();
            bool rejectedBeforeTheGateWasReleased = rejectedOperation.IsCompleted;

            operationGate.Release();
            ownsGate = false;

            Assert.Equal(0, await acceptedOperation.WaitAsync(TimeSpan.FromSeconds(5)));
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            var rejectedError = await Record.ExceptionAsync(() => rejectedOperation);

            Assert.True(rejectedBeforeTheGateWasReleased);
            Assert.IsType<ObjectDisposedException>(rejectedError);
        }
        finally
        {
            if (ownsGate)
                operationGate.Release();
            if (acceptedOperation is not null)
                try { await acceptedOperation.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (rejectedOperation is not null)
                try { await rejectedOperation.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            if (disposal is not null)
                try { await disposal.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            else
                try { await database.DisposeAsync(); } catch { }
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisposeAsyncIsIdempotent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lexio-db-dispose-" + Guid.NewGuid().ToString("N"));
        var database = new AppDatabase(Path.Combine(directory, "dispose.db"));

        try
        {
            await database.MigrateAsync();
            await database.DisposeAsync();
            await database.DisposeAsync();
        }
        finally
        {
            try { await database.DisposeAsync(); } catch { }
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static SemaphoreSlim GetOperationGate(AppDatabase database) =>
        (SemaphoreSlim)typeof(AppDatabase)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(database)!;
}
