using PicForLater.Core.Analysis;

namespace PicForLater.Analysis;

public sealed class FallbackOcrProvider : IOcrProvider
{
    private readonly IReadOnlyList<IOcrProvider> _providers;

    public FallbackOcrProvider(IEnumerable<IOcrProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
        if (_providers.Count == 0)
        {
            throw new ArgumentException("At least one local OCR provider is required.", nameof(providers));
        }

        Descriptor = new OcrProviderDescriptor(
            "local.ocr.fallback-chain",
            "Local OCR fallback chain",
            _providers.SelectMany(provider => provider.Descriptor.SupportedLanguageTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _providers.SelectMany(provider => provider.Descriptor.SupportedScripts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _providers.Any(provider => provider.Descriptor.SupportsMixedLanguages));
    }

    public OcrProviderDescriptor Descriptor { get; }

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            if (await provider.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<OcrDocument> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fallbackWarnings = new List<string>();
        OcrProviderException? lastFailure = null;
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await provider.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                fallbackWarnings.Add($"ocr-provider-unavailable:{provider.Descriptor.ProviderId}");
                continue;
            }

            try
            {
                var result = await provider.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
                return result with
                {
                    Warnings = fallbackWarnings.Concat(result.Warnings).ToArray(),
                };
            }
            catch (OcrProviderUnavailableException exception)
            {
                fallbackWarnings.Add($"ocr-provider-fallback:{provider.Descriptor.ProviderId}:{exception.ErrorCode}");
            }
            catch (OcrProviderException exception)
            {
                lastFailure = exception;
                fallbackWarnings.Add($"ocr-provider-fallback:{provider.Descriptor.ProviderId}:{exception.ErrorCode}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = new OcrProviderException("ocr.provider-failed", isRetryable: true, exception);
                fallbackWarnings.Add($"ocr-provider-fallback:{provider.Descriptor.ProviderId}:ocr.provider-failed");
            }
        }

        if (lastFailure is not null)
        {
            throw lastFailure;
        }

        throw new OcrProviderUnavailableException("ocr.no-local-provider");
    }
}
