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

    public async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            SpeechSynthesizer synth;
            lock (_lock)
            {
                _synth ??= CreateSynthesizer();
                synth = _synth;
            }
            var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask().ConfigureAwait(false);
            lock (_lock)
            {
                _player ??= new MediaPlayer();
                _player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
                _player.Play();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Lexio] TTS failed: {ex.Message}");
        }
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

    public void Dispose()
    {
        _synth?.Dispose();
        _player?.Dispose();
    }
}
