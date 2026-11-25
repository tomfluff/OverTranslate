using System.IO;
using System.Net.Http;
using Windows.Media.Core;
using Windows.Media.Playback;
using OverTranslate.Services.Providers;
using OverTranslate.Translation;
using OverTranslate.Translation.Speech;
using NLog;

namespace OverTranslate.Services;

public class TtsService : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Every speaker in the application reads through one set of engines, so the Bing credentials
    // and the Microsoft token are fetched once rather than once per window. The request timeout is
    // translation's (TranslationTiming): one slow engine hands over to the next after ten seconds,
    // where GTranslate's own clients waited a hundred.
    private static readonly HttpClient Http = EngineHttp.CreateClient(TranslationTiming.Request);

    // Google first, as before; RPC ahead of the old translate_tts address, which is the order the old
    // service used. Microsoft and Bing are the same Azure voices by two routes, and between them
    // cover the one language the application offers that Google has no voice for (Slovenian).
    // Yandex, the old fifth, was dropped: nothing reached it that the four above could not read.
    //
    // The voices on this PC go first, and only take the languages they have a voice for while the
    // setting is on (LocalSpeech.Supports); everything else still reads online.
    private static readonly LocalSpeech Local = new();

    private static readonly SpeechSynthesizer Synthesizer = new(
    [
        Local,
        new GoogleRpcSpeech(Http),
        new GoogleWebSpeech(Http),
        new MicrosoftSpeech(Http),
        new BingSpeech(Http),
    ]);

    private MediaPlayer? _player;
    private CancellationTokenSource? _cts;
    private bool _active;

    /// <summary>True while fetching audio or playing. Lets the UI toggle a play/stop button.</summary>
    public bool IsActive => _active;

    /// <summary>Raised (on the UI thread) whenever playback starts, ends, fails, or is stopped.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Creates the player on demand. Windows.Media.Playback's player, not WPF's: WPF's MediaPlayer is
    /// Windows Media Player underneath and fails with InvalidWmpVersionException on every press where
    /// the optional "Windows Media Player Legacy" feature is not installed — the default on current
    /// Windows 11. This one is Media Foundation, which every desktop edition has. A player that has
    /// raised MediaFailed is still thrown away, as before, and the next request gets a fresh one.
    /// UI thread only.
    /// </summary>
    private MediaPlayer EnsurePlayer()
    {
        if (_player != null) return _player;

        var player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech };
        // A read-aloud is not a song: keep it off the media keys and out of the volume flyout.
        player.CommandManager.IsEnabled = false;

        // Both events arrive on a worker thread.
        player.MediaEnded += (_, _) => OnUi(() =>
        {
            if (!ReferenceEquals(_player, player)) return;
            player.Source = null;
            SetActive(false);
        });
        player.MediaFailed += (_, e) => OnUi(() =>
        {
            Log.Warn(e.ExtendedErrorCode, "TTS playback failed ({Error}: {Message}), discarding player", e.Error, e.ErrorMessage);
            if (ReferenceEquals(_player, player)) _player = null;
            player.Dispose();
            SetActive(false);
        });

        _player = player;
        return player;
    }

    private static void OnUi(Action action) => System.Windows.Application.Current?.Dispatcher.Invoke(action);

    /// <summary>Stops playback. UI thread only.</summary>
    private void ClosePlayer()
    {
        if (_player == null) return;
        _player.Pause();
        _player.Source = null;
    }

    private void SetActive(bool value)
    {
        if (_active == value) return;
        _active = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops any in-flight fetch and playback.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        System.Windows.Application.Current.Dispatcher.Invoke(ClosePlayer);
        SetActive(false);
    }

    public Task SpeakAsync(string text, string langCode)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;

        // The same codes translation speaks, so every language the pickers offer has its voice —
        // the old table knew fifteen and read the rest in English. 自動 is no language to read in;
        // the callers never offer it, and if one ever does, nothing is better than a wrong voice.
        var language = EngineLanguage.SourceToEngine(langCode);
        if (language is null)
        {
            Log.Debug("TTS skipped: no language to read {Lang} in", langCode);
            return Task.CompletedTask;
        }

        // The token reaches the request itself, so 停止 aborts the download rather than only
        // discarding it when it arrives.
        return PlayAsync(async token => (await Synthesizer.SynthesizeAsync(text, language, token)).Audio);
    }

    /// <summary>
    /// Reads <paramref name="text"/> with <paramref name="voice"/> at its current speed and pitch,
    /// whether or not voices on this PC are switched on — the settings page's preview.
    /// </summary>
    public Task PreviewAsync(LocalVoice voice, string text) =>
        PlayAsync(token => Local.SynthesizeAsync(voice, text, token));

    private async Task PlayAsync(Func<CancellationToken, Task<byte[]>> synthesize)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        System.Windows.Application.Current.Dispatcher.Invoke(ClosePlayer);
        SetActive(true);

        try
        {
            var audio = await synthesize(token);
            token.ThrowIfCancellationRequested();

            // Online engines answer with MP3, the voices on this PC with WAV.
            var contentType = audio.AsSpan().StartsWith("RIFF"u8) ? "audio/wav" : "audio/mpeg";

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var player = EnsurePlayer();
                player.Source = MediaSource.CreateFromStream(new MemoryStream(audio).AsRandomAccessStream(), contentType);
                player.Play();
            });
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Stopped, or replaced by a newer press — which owns the button state now.
        }
        catch
        {
            // Every engine failed — clear state so the button resets, and let the caller say so.
            SetActive(false);
            throw;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            ClosePlayer();
            _player?.Dispose();
            _player = null;
        });
    }
}
