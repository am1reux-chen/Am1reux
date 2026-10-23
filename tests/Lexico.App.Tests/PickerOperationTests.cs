using Lexio.Services;
using Xunit;

namespace Lexico.App.Tests;

public sealed class PickerOperationTests
{
    [Fact]
    public async Task SelectedFileDoesNotReportNoSelection()
    {
        Exception? reported = null;
        var noSelectionReported = false;

        var file = await PickerOperation.TryRunAsync<string>(
            () => Task.FromResult<string?>("selected.csv"),
            error =>
            {
                reported = error;
                return Task.CompletedTask;
            },
            () => noSelectionReported = true);

        Assert.Equal("selected.csv", file);
        Assert.Null(reported);
        Assert.False(noSelectionReported);
    }

    [Fact]
    public async Task FailedPickerReturnsNoFileAndReportsTheException()
    {
        var expected = new InvalidOperationException("picker initialization failed");
        Exception? reported = null;
        var noSelectionReported = false;

        var file = await PickerOperation.TryRunAsync<string>(
            () => Task.FromException<string?>(expected),
            error =>
            {
                reported = error;
                return Task.CompletedTask;
            },
            () => noSelectionReported = true);

        Assert.Null(file);
        Assert.Same(expected, reported);
        Assert.False(noSelectionReported);
    }

    [Fact]
    public async Task UserCancellationReportsNoSelectionWithoutReportingAnError()
    {
        Exception? reported = null;
        var noSelectionReported = false;

        var file = await PickerOperation.TryRunAsync<string>(
            () => Task.FromResult<string?>(null),
            error =>
            {
                reported = error;
                return Task.CompletedTask;
            },
            () => noSelectionReported = true);

        Assert.Null(file);
        Assert.Null(reported);
        Assert.True(noSelectionReported);
    }

    [Fact]
    public async Task GuardedAsyncActionReportsFailureWithoutRethrowing()
    {
        var expected = new InvalidOperationException("confirmation dialog failed");
        Exception? reported = null;

        await PickerOperation.TryRunAsync(async () =>
        {
            await Task.Yield();
            throw expected;
        }, error =>
        {
            reported = error;
            return Task.CompletedTask;
        });

        Assert.Same(expected, reported);
    }

    [Fact]
    public async Task GuardedAsyncActionDoesNotReportErrorAfterSuccess()
    {
        Exception? reported = null;
        var completed = false;

        await PickerOperation.TryRunAsync(async () =>
        {
            await Task.Yield();
            completed = true;
        }, error =>
        {
            reported = error;
            return Task.CompletedTask;
        });

        Assert.True(completed);
        Assert.Null(reported);
    }

    [Fact]
    public void GuardedSynchronousActionReportsFailureWithoutRethrowing()
    {
        var expected = new InvalidOperationException("clipboard unavailable");
        Exception? reported = null;
        var status = "";

        var escaped = Record.Exception(() => PickerOperation.TryRun(
            () => throw expected,
            error =>
            {
                reported = error;
                status = "无法复制恢复指引。";
            }));

        Assert.Null(escaped);
        Assert.Same(expected, reported);
        Assert.Equal("无法复制恢复指引。", status);
    }
}
