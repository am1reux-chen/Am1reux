using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Lexio.Services;

/// <summary>Offline TTS via the Windows speech engine. En-US voice when available.</summary>
public sealed class Speech : IDisposable
{
    private SpeechSynthesizer? _synth;
    private MediaPlayer? _player;
    private readonly Lock _lock = new();
    private readonly Func<string, Task<IDisposable>> _synthesize;
    private readonly Action<IDisposable> _play;
    private readonly Action _disposeSynthesizer;
    private readonly Action _disposePlayer;
    private readonly Action _disposeResources;
    private readonly Action<Exception> _reportError;
    private long _requestVersion;
    private bool _disposed;

    public Speech()
    {
        _synthesize = SynthesizeAsync;
        _play = Play;
        _disposeSynthesizer = DisposeSynthesizer;
        _disposePlayer = DisposePlayer;
        _disposeResources = DisposeResources;
        _reportError = _ => { };
    }

    internal Speech(Action<Exception> reportError)
    {
        _synthesize = SynthesizeAsync;
        _play = Play;
        _disposeSynthesizer = DisposeSynthesizer;
        _disposePlayer = DisposePlayer;
        _disposeResources = DisposeResources;
        _reportError = reportError;
    }

    internal Speech(Func<string, Task<IDisposable>> synthesize, Action<IDisposable> play,
        Action disposeResources, Action<Exception>? reportError = null)
        : this(synthesize, play, disposeResources, static () => { }, reportError)
    {
    }

    internal Speech(Func<string, Task<IDisposable>> synthesize, Action<IDisposable> play,
        Action disposeSynthesizer, Action disposePlayer, Action<Exception>? reportError = null)
    {
        _synthesize = synthesize;
        _play = play;
        _disposeSynthesizer = disposeSynthesizer;
        _disposePlayer = disposePlayer;
        _disposeResources = DisposeResources;
        _reportError = reportError ?? (_ => { });
    }

    public async Task SpeakAsync(string text) => _ = await TrySpeakAsync(text).ConfigureAwait(false);

    public async Task<bool> TrySpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        IDisposable? stream = null;
        long requestVersion;
        try
        {
            Task<IDisposable> synthesis;
            lock (_lock)
            {
                if (_disposed)
                    return false;
                requestVersion = ++_requestVersion;
                synthesis = _synthesize(text);
            }

            stream = await synthesis.ConfigureAwait(false);
            lock (_lock)
            {
                if (_disposed || requestVersion != _requestVersion)
                    return false;
                _play(stream);
                stream = null; // Ownership transfers to the playback source.
            }
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
            return false;
        }
        finally
        {
            try { stream?.Dispose(); }
            catch (Exception ex) { ReportFailure(ex); }
        }
    }

    private void ReportFailure(Exception error)
    {
        System.Diagnostics.Debug.WriteLine($"[Lexio] TTS failed: {error.Message}");
        try { _reportError(error); }
        catch (Exception reportError)
        {
            System.Diagnostics.Debug.WriteLine($"[Lexio] TTS diagnostics failed: {reportError.Message}");
        }
    }

    private async Task<IDisposable> SynthesizeAsync(string text)
    {
        _synth ??= CreateSynthesizer();
        return await _synth.SynthesizeTextToStreamAsync(text).AsTask().ConfigureAwait(false);
    }

    private void Play(IDisposable stream)
    {
        _player ??= new MediaPlayer();
        _player.Source = MediaSource.CreateFromStream((SpeechSynthesisStream)stream, "audio/wav");
        _player.Play();
    }

    private static SpeechSynthesizer CreateSynthesizer()
    {
        var synth = new SpeechSynthesizer();
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("en-US", StringComparison.OrdinalIgnoreCase))
                    ?? SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        if (voice is not null)
            synth.Voice = voice;
        return synth;
    }

    private void DisposeResources()
    {
        DisposeResource(_disposeSynthesizer);
        DisposeResource(_disposePlayer);
    }

    private void DisposeSynthesizer() => _synth?.Dispose();

    private void DisposePlayer() => _player?.Dispose();

    private void DisposeResource(Action dispose)
    {
        try { dispose(); }
        catch (Exception error) { ReportFailure(error); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _disposeResources();
        }
    }
}
