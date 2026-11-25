using System.Net.Http;
using OverTranslate.Translation.DeepL;

namespace OverTranslate.Services.Providers;

/// <summary>
/// 「DeepL」 as the application asks for it: the engine in <see cref="OverTranslate.Translation"/>,
/// with the key the caller was given.
/// </summary>
/// <remarks>
/// The key arrives with each call rather than once, so the engine is built per call — it holds
/// nothing but the key and the client. Everything else (codes, packing, what a failure looks like)
/// is <see cref="EngineProvider"/>'s, the same as for the free engines.
/// </remarks>
public sealed class DeepLProvider(HttpClient http) : ITranslationProvider
{
    public bool RequiresApiKey => true;

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey,
        CancellationToken cancellationToken = default, TranslationContext? context = null)
    {
        try
        {
            return await new EngineProvider(new DeepLTranslator(http, apiKey))
                .TranslateAsync(blocks, sourceLang, targetLang, apiKey, cancellationToken);
        }
        // DeepL's own statuses, in what to do about them rather than "DeepL: HTTP 456".
        catch (OverTranslate.Translation.TranslationEngineException ex) when (MessageFor(ex.StatusCode) is { } key)
        {
            throw new InvalidOperationException(LocalizationService.Get(key), ex);
        }
    }

    private static string? MessageFor(System.Net.HttpStatusCode? status) => (int?)status switch
    {
        401 or 403 => "S.Error.DeepLKey",
        456 => "S.Error.DeepLQuota",
        429 => "S.Error.DeepLTooMany",
        _ => null,
    };
}
