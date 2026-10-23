using Lexio.Services;
using Xunit;

namespace Lexico.App.Tests;

public sealed class AsyncOperationGateTests
{
    [Fact]
    public async Task StopWaitsForRunningOperationAndRejectsNewWork()
    {
        var gate = new AsyncOperationGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = gate.RunAsync(async () =>
        {
            started.SetResult();
            await release.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = gate.StopAndWaitAsync();
        Assert.False(stop.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => gate.RunAsync(() => Task.CompletedTask));

        release.SetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StopDrainsQueuedOperationsAndGateSerializesThem()
    {
        var gate = new AsyncOperationGate();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var maximumActiveLock = new object();

        Task EnterAsync(TaskCompletionSource started, Task release)
        {
            var current = Interlocked.Increment(ref active);
            lock (maximumActiveLock)
                maximumActive = Math.Max(maximumActive, current);
            started.SetResult();
            return ReleaseAsync();

            async Task ReleaseAsync()
            {
                try { await release; }
                finally { Interlocked.Decrement(ref active); }
            }
        }

        var first = gate.RunAsync(() => EnterAsync(firstStarted, releaseFirst.Task));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = gate.RunAsync(() => EnterAsync(secondStarted, releaseSecond.Task));
        var stop = gate.StopAndWaitAsync();

        try
        {
            Assert.False(secondStarted.Task.IsCompleted);
            Assert.False(stop.IsCompleted);
            releaseFirst.SetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted);
            releaseSecond.SetResult();

            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
        }
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task FailedOperationDoesNotPreventDrainOrLaterQueuedWork()
    {
        var gate = new AsyncOperationGate();
        var expected = new InvalidOperationException("restore failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync(() => Task.FromException(expected)));
        var result = await gate.RunAsync(async () =>
        {
            await Task.Yield();
            return 42;
        });

        await gate.StopAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, result);
    }
}
