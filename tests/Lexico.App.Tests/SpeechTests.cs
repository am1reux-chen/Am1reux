using Lexio.Services;
using Xunit;

namespace Lexico.App.Tests;

public sealed class SpeechTests
{
    [Fact]
    public async Task SynthesisFinishingAfterDisposalDoesNotPlayAndDisposesItsStream()
    {
        var synthesisResult = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var synthesisStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new CountingDisposable();
        bool played = false;
        var speech = new Speech(_ =>
        {
            synthesisStarted.SetResult();
            return synthesisResult.Task;
        }, _ => played = true, () => { });

        var speaking = speech.SpeakAsync("ephemeral");
        await synthesisStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        speech.Dispose();
        synthesisResult.SetResult(stream);
        await speaking.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(played);
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task DisposedSpeechDoesNotStartNewSynthesisAndCanBeDisposedAgain()
    {
        int synthesisCount = 0;
        int disposeCount = 0;
        var speech = new Speech(_ =>
        {
            synthesisCount++;
            return Task.FromResult<IDisposable>(new CountingDisposable());
        }, _ => { }, () => disposeCount++);

        speech.Dispose();
        speech.Dispose();
        await speech.SpeakAsync("serendipity");

        Assert.Equal(0, synthesisCount);
        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public void DisposeAttemptsBothResourcesAndReportsBothFailures()
    {
        var synthesizerFailure = new InvalidOperationException("synthesizer disposal failed");
        var playerFailure = new IOException("player disposal failed");
        var attempts = new List<string>();
        var reported = new List<Exception>();
        var speech = new Speech(_ => Task.FromResult<IDisposable>(new CountingDisposable()), _ => { },
            () => { attempts.Add("synthesizer"); throw synthesizerFailure; },
            () => { attempts.Add("player"); throw playerFailure; }, reported.Add);

        var error = Record.Exception(speech.Dispose);
        speech.Dispose();

        Assert.Null(error);
        Assert.Equal(new[] { "synthesizer", "player" }, attempts);
        Assert.Collection(reported,
            exception => Assert.Same(synthesizerFailure, exception),
            exception => Assert.Same(playerFailure, exception));
    }

    [Fact]
    public async Task NewerRequestIsNotOverriddenByEarlierSynthesisFinishingLast()
    {
        var firstResult = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResult = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new List<IDisposable>();
        var speech = new Speech(text => text == "first" ? firstResult.Task : secondResult.Task,
            played.Add, () => { });
        var firstStream = new CountingDisposable();
        var secondStream = new CountingDisposable();

        var firstRequest = speech.SpeakAsync("first");
        var secondRequest = speech.SpeakAsync("second");
        secondResult.SetResult(secondStream);
        await secondRequest.WaitAsync(TimeSpan.FromSeconds(5));
        firstResult.SetResult(firstStream);
        await firstRequest.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Collection(played, stream => Assert.Same(secondStream, stream));
        Assert.Equal(1, firstStream.DisposeCount);
        Assert.Equal(0, secondStream.DisposeCount);
    }

    [Fact]
    public async Task SynthesisFailureIsReportedWithoutEscapingTheSpeechCall()
    {
        var failure = new InvalidOperationException("synthesis unavailable");
        var reported = new List<Exception>();
        var speech = new Speech(_ => Task.FromException<IDisposable>(failure), _ => { }, () => { }, reported.Add);

        await speech.SpeakAsync("serendipity");

        Assert.Collection(reported, error => Assert.Same(failure, error));
    }

    [Fact]
    public async Task PlaybackFailureIsReportedAndTheStreamIsDisposed()
    {
        var failure = new InvalidOperationException("playback unavailable");
        var stream = new CountingDisposable();
        var reported = new List<Exception>();
        var speech = new Speech(_ => Task.FromResult<IDisposable>(stream),
            _ => throw failure, () => { }, reported.Add);

        await speech.SpeakAsync("ephemeral");

        Assert.Collection(reported, error => Assert.Same(failure, error));
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public async Task ErrorReporterFailureDoesNotEscapeTheSpeechCall()
    {
        var speech = new Speech(_ => Task.FromException<IDisposable>(new InvalidOperationException("synthesis failed")),
            _ => { }, () => { }, _ => throw new IOException("log unavailable"));

        await speech.SpeakAsync("word");
    }

    [Fact]
    public async Task PlaybackAndStreamDisposalFailuresAreReportedWithoutEscaping()
    {
        var playbackFailure = new InvalidOperationException("playback unavailable");
        var disposalFailure = new IOException("stream disposal failed");
        var reported = new List<Exception>();
        var speech = new Speech(_ => Task.FromResult<IDisposable>(new ThrowingDisposable(disposalFailure)),
            _ => throw playbackFailure, () => { }, reported.Add);

        await speech.SpeakAsync("ephemeral");

        Assert.Collection(reported,
            error => Assert.Same(playbackFailure, error),
            error => Assert.Same(disposalFailure, error));
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingDisposable(Exception failure) : IDisposable
    {
        public void Dispose() => throw failure;
    }
}
