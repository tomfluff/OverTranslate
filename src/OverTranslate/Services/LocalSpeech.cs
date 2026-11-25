using System.Globalization;
using System.IO;
using OverTranslate.Models;
using OverTranslate.Services.Providers;
using OverTranslate.Translation;
using OverTranslate.Translation.Speech;
using Sapi = System.Speech.Synthesis;
using OneCore = Windows.Media.SpeechSynthesis;

namespace OverTranslate.Services;

/// <summary>A voice installed on this PC.</summary>
/// <param name="Id">"sapi:" or "onecore:" and the voice's own name or id; what settings store.</param>
/// <param name="Display">The voice's name and which of the two Windows speech stacks it lives in.</param>
/// <param name="Language">The engine code it reads (en, ja, zh-TW) — see <see cref="EngineLanguage"/>.</param>
/// <param name="AppLanguage">The application's code for that language (EN, JA, ZH-HANT).</param>
/// <param name="Culture">The voice's own culture (he-IL), for the preview to read its name in.</param>
public sealed record LocalVoice(string Id, string Display, string Language, string AppLanguage, string Culture);

/// <summary>
/// Reads aloud with the voices Windows has installed: SAPI 5 (the desktop voices, and anything a
/// third party registers there, such as natural-voice adapters) and OneCore (the voices Windows'
/// own Settings lists). No network, and it answers with WAV rather than MP3.
/// </summary>
/// <remarks>
/// Sits first in <see cref="TtsService"/>'s list and claims a language only while
/// <see cref="Models.AppSettings.LocalSpeechEnabled"/> is on and there is a voice for it, so every
/// other language still falls through to the online engines.
/// </remarks>
public sealed class LocalSpeech : ISpeechEngine
{
    private const string SapiPrefix = "sapi:";
    private const string OneCorePrefix = "onecore:";

    private static IReadOnlyList<LocalVoice>? _voices;

    /// <summary>Every installed voice the application has a language for, SAPI first.</summary>
    public static IReadOnlyList<LocalVoice> Voices => _voices ??= ListVoices();

    /// <summary>
    /// Lists the voices again on next use, so one installed or removed while the application is
    /// open turns up without a restart. The settings page calls it each time it shows the voices.
    /// </summary>
    public static void Refresh() => _voices = null;

    public string Name => "LocalSpeech";

    public bool Supports(string language) =>
        SettingsService.Instance.Current.LocalSpeechEnabled && VoiceFor(language) is not null;

    /// <summary>The voice chosen for <paramref name="language"/> in settings, else the first one for it.</summary>
    public static LocalVoice? VoiceFor(string language)
    {
        var candidates = Voices.Where(v => v.Language.Equals(language, StringComparison.OrdinalIgnoreCase)).ToList();
        SettingsService.Instance.Current.LocalSpeechVoices.TryGetValue(language, out var chosen);
        return candidates.FirstOrDefault(v => v.Id == chosen) ?? candidates.FirstOrDefault();
    }

    /// <summary>The speeds the settings page offers per voice.</summary>
    public static readonly double[] Rates = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];

    /// <summary>The pitch slider's reach either way, in percent. Steps of 1: 10% is already a different voice.</summary>
    public const double MaxPitch = 20;

    public static VoiceTuning TuningOf(LocalVoice voice) =>
        SettingsService.Instance.Current.LocalSpeechTuning.GetValueOrDefault(voice.Id) ?? new VoiceTuning();

    public async Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken cancellationToken = default) =>
        await SynthesizeAsync(VoiceFor(language) ?? throw new TranslationEngineException(Name, $"no voice for {language}"),
            text, cancellationToken);

    /// <summary>Reads <paramref name="text"/> with <paramref name="voice"/> at its tuning; the settings page's preview.</summary>
    public async Task<byte[]> SynthesizeAsync(LocalVoice voice, string text, CancellationToken cancellationToken = default)
    {
        var tuning = TuningOf(voice);
        try
        {
            var audio = voice.Id.StartsWith(SapiPrefix, StringComparison.Ordinal)
                ? await Task.Run(() => SpeakSapi(voice.Id[SapiPrefix.Length..], text, tuning), cancellationToken)
                : await SpeakOneCoreAsync(voice.Id[OneCorePrefix.Length..], text, tuning, cancellationToken);

            // ponytail: SAPI cannot be interrupted mid-synthesis from here; a stop lands once it returns.
            cancellationToken.ThrowIfCancellationRequested();
            if (audio.Length == 0) throw new TranslationEngineException(Name, "empty audio");
            return audio;
        }
        catch (Exception ex) when (ex is not (TranslationEngineException or OperationCanceledException))
        {
            // A broken third-party voice is one engine failing, not the speaker: the online ones are next.
            throw new TranslationEngineException(Name, $"{voice.Display}: {ex.Message}", null, ex);
        }
    }

    /// <remarks>
    /// SAPI has a rate but ignores pitch — <c>&lt;prosody pitch&gt;</c> in SSML comes back as the very
    /// same audio from desktop and natural voices alike (measured 2026-11-25). So pitch is made here:
    /// the WAV is relabelled as recorded <c>p</c> times faster, which raises it by <c>p</c> and speeds it
    /// up by <c>p</c>, and it is spoken <c>p</c> times slower to start with so the speed comes out as asked.
    /// </remarks>
    private static byte[] SpeakSapi(string voiceName, string text, VoiceTuning tuning)
    {
        var pitch = PitchFactor(tuning.Pitch);
        using var synthesizer = new Sapi.SpeechSynthesizer();
        synthesizer.SelectVoice(voiceName);
        synthesizer.Rate = SapiRate(tuning.Rate / pitch);
        using var wav = new MemoryStream();
        synthesizer.SetOutputToWaveStream(wav);
        synthesizer.Speak(text);
        synthesizer.SetOutputToNull();
        var audio = wav.ToArray();
        ScaleSampleRate(audio, pitch);
        return audio;
    }

    private static async Task<byte[]> SpeakOneCoreAsync(string voiceId, string text, VoiceTuning tuning, CancellationToken cancellationToken)
    {
        using var synthesizer = new OneCore.SpeechSynthesizer
        {
            Voice = OneCore.SpeechSynthesizer.AllVoices.First(v => v.Id == voiceId),
        };
        // Both native here: 0.5–6 times the speed, and pitch from 0 to 2 around 1.
        synthesizer.Options.SpeakingRate = Math.Clamp(tuning.Rate, 0.5, 6);
        synthesizer.Options.AudioPitch = Math.Clamp(PitchFactor(tuning.Pitch), 0, 2);
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(cancellationToken);
        using var wav = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(wav, cancellationToken);
        return wav.ToArray();
    }

    private static double PitchFactor(double percent) => 1 + Math.Clamp(percent, -50, 50) / 100;

    /// <summary>SAPI's rate for <paramref name="speed"/> times normal: -10 is a third of it, 10 three times.</summary>
    internal static int SapiRate(double speed) =>
        Math.Clamp((int)Math.Round(10 * Math.Log(Math.Max(speed, 0.01), 3)), -10, 10);

    /// <summary>Multiplies the sample rate (and byte rate) a RIFF/WAVE header declares by <paramref name="factor"/>.</summary>
    internal static void ScaleSampleRate(byte[] wav, double factor)
    {
        if (factor == 1 || wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return;

        // Chunks are an id, a size and the data, padded to an even length.
        for (long at = 12; at + 20 <= wav.Length; at += (8L + BitConverter.ToUInt32(wav, (int)at + 4) + 1) & ~1L)
        {
            if (!wav.AsSpan((int)at, 4).SequenceEqual("fmt "u8)) continue;
            Scale((int)at + 12);   // sample rate
            Scale((int)at + 16);   // byte rate
            return;
        }

        void Scale(int offset) => BitConverter.TryWriteBytes(wav.AsSpan(offset, 4),
            (uint)Math.Round(BitConverter.ToUInt32(wav, offset) * factor));
    }

    private static IReadOnlyList<LocalVoice> ListVoices()
    {
        var voices = new List<LocalVoice>();

        try
        {
            using var synthesizer = new Sapi.SpeechSynthesizer();
            foreach (var installed in synthesizer.GetInstalledVoices().Where(v => v.Enabled))
                Add(SapiPrefix + installed.VoiceInfo.Name, installed.VoiceInfo.Name, installed.VoiceInfo.Culture, "SAPI");
        }
        catch (Exception ex) { NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Could not list SAPI voices"); }

        try
        {
            foreach (var installed in OneCore.SpeechSynthesizer.AllVoices)
                Add(OneCorePrefix + installed.Id, installed.DisplayName, new CultureInfo(installed.Language), "Windows");
        }
        catch (Exception ex) { NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Could not list Windows voices"); }

        return voices;

        void Add(string id, string name, CultureInfo culture, string stack)
        {
            var appLanguage = EngineLanguage.FromEngine(culture.Name);
            // Only languages the application reads in: a voice for anything else could never be asked for.
            if (!LanguageData.SourceLanguages.Any(l => l.Code.Equals(appLanguage, StringComparison.OrdinalIgnoreCase)))
                return;
            voices.Add(new LocalVoice(id, $"{name} ({stack})", EngineLanguage.ToEngine(appLanguage), appLanguage, culture.Name));
        }
    }
}
