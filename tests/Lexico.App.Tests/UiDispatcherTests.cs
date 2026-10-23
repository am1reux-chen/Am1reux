using Lexio.Services;
using Xunit;

namespace Lexico.App.Tests;

public sealed class UiDispatcherTests
{
    [Fact]
    public async Task RejectedDispatchSkipsActionInsteadOfRunningItOnTheCallerThread()
    {
        int calls = 0;

        await UiDispatcher.DispatchAsync(false, _ => false, () => calls++);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AcceptedDispatchRunsActionWhenTheQueueExecutesIt()
    {
        var queued = new Queue<Action>();
        int calls = 0;

        var dispatched = UiDispatcher.DispatchAsync(false, action =>
        {
            queued.Enqueue(action);
            return true;
        }, () => calls++);

        Assert.Equal(0, calls);
        Assert.False(dispatched.IsCompleted);

        queued.Dequeue()();
        await dispatched;

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AcceptedDispatchPropagatesActionExceptions()
    {
        var queued = new Queue<Action>();
        var expected = new InvalidOperationException("UI update failed");
        var dispatched = UiDispatcher.DispatchAsync(false, action =>
        {
            queued.Enqueue(action);
            return true;
        }, () => throw expected);

        queued.Dequeue()();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatched);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task RejectedAsyncDispatchDoesNotStartTheWork()
    {
        bool started = false;

        await UiDispatcher.DispatchAsync(false, _ => false, () =>
        {
            started = true;
            return Task.CompletedTask;
        });

        Assert.False(started);
    }

    [Fact]
    public async Task AcceptedAsyncDispatchStartsWorkOnTheQueueAndAwaitsCompletion()
    {
        var queued = new Queue<Action>();
        var finishWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;

        var dispatched = UiDispatcher.DispatchAsync(false, action =>
        {
            queued.Enqueue(action);
            return true;
        }, () =>
        {
            started = true;
            return finishWork.Task;
        });

        Assert.False(started);
        queued.Dequeue()();
        Assert.True(started);
        Assert.False(dispatched.IsCompleted);

        finishWork.SetResult();
        await dispatched;
    }

    [Fact]
    public async Task AcceptedAsyncDispatchPropagatesWorkExceptions()
    {
        var queued = new Queue<Action>();
        var expected = new InvalidOperationException("UI work failed");
        var dispatched = UiDispatcher.DispatchAsync(false, action =>
        {
            queued.Enqueue(action);
            return true;
        }, () => Task.FromException(expected));

        queued.Dequeue()();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatched);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task CurrentThreadAccessRunsActionInlineWithoutQueueing()
    {
        bool enqueued = false;
        int calls = 0;

        var dispatched = UiDispatcher.DispatchAsync(true, _ =>
        {
            enqueued = true;
            return false;
        }, () => calls++);

        Assert.True(dispatched.IsCompletedSuccessfully);
        Assert.Equal(1, calls);
        Assert.False(enqueued);
        await dispatched;
    }

    [Fact]
    public async Task CurrentThreadAccessStartsAsyncWorkInlineAndAwaitsIt()
    {
        bool enqueued = false;
        bool started = false;
        var finishWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var dispatched = UiDispatcher.DispatchAsync(true, _ =>
        {
            enqueued = true;
            return false;
        }, () =>
        {
            started = true;
            return finishWork.Task;
        });

        Assert.True(started);
        Assert.False(enqueued);
        Assert.False(dispatched.IsCompleted);

        finishWork.SetResult();
        await dispatched;
    }

    [Fact]
    public async Task CurrentThreadAccessPropagatesActionExceptionsWithoutQueueing()
    {
        var expected = new InvalidOperationException("inline UI update failed");

        var dispatched = UiDispatcher.DispatchAsync(true, _ => throw new Xunit.Sdk.XunitException("Must not enqueue."),
            () => throw expected);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatched);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task CurrentThreadAccessPropagatesAsyncWorkExceptionsWithoutQueueing()
    {
        var expected = new InvalidOperationException("inline UI work failed");

        var dispatched = UiDispatcher.DispatchAsync(true, _ => throw new Xunit.Sdk.XunitException("Must not enqueue."),
            () => Task.FromException(expected));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatched);
        Assert.Same(expected, actual);
    }
}
