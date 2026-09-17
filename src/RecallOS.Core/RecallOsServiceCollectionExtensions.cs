using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Capture;
using RecallOS.Core.Common;
using RecallOS.Core.Intelligence;
using RecallOS.Core.Maintenance;
using RecallOS.Core.Ocr;
using RecallOS.Core.Pipeline;
using RecallOS.Core.Search;
using RecallOS.Core.Storage;

namespace RecallOS.Core;

/// <summary>
/// Wires the whole engine into a container.
/// </summary>
/// <remarks>
/// Everything is registered against an interface and resolved as a singleton: there is one
/// store, one capture device and one OCR engine per process, and the lifetimes are the
/// application's. <c>TryAdd</c> is used throughout so a host -- or a test -- can register
/// its own implementation first and have it win, which is how the composability the system
/// promises is actually delivered rather than just described.
/// </remarks>
public static class RecallOsServiceCollectionExtensions
{
    public static IServiceCollection AddRecallOs(this IServiceCollection services, string? storeRoot = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ---- foundation --------------------------------------------------------------
        services.TryAddSingleton(new RecallPaths(storeRoot).EnsureCreated());
        services.TryAddSingleton<SqliteConnectionFactory>();
        services.TryAddSingleton<DatabaseBootstrapper>();
        services.TryAddSingleton<FrameFileStore>();

        // ---- storage -----------------------------------------------------------------
        services.TryAddSingleton<IFrameRepository, FrameRepository>();
        services.TryAddSingleton<ISettingsStore, SqliteSettingsStore>();

        // ---- capture -----------------------------------------------------------------
        services.TryAddSingleton<IScreenCaptureService, GdiScreenCaptureService>();

        // ---- text --------------------------------------------------------------------
        services.TryAddSingleton<TessDataLocator>();
        services.TryAddSingleton<TextChunker>();

        // The configured language is only known after settings load, so the engine is built
        // from the resolved settings rather than from a constant.
        services.TryAddSingleton<IOcrEngine>(provider =>
        {
            var settings = provider.GetRequiredService<ISettingsStore>().Current;
            return new TesseractOcrEngine(
                provider.GetRequiredService<TessDataLocator>(),
                settings.OcrLanguages,
                provider.GetService<Microsoft.Extensions.Logging.ILogger<TesseractOcrEngine>>());
        });

        // ---- intelligence ------------------------------------------------------------
        // Swap either of these for a learned model and nothing else in the system changes.
        services.TryAddSingleton<IEmbeddingProvider>(_ => new HashingEmbeddingProvider(256));
        services.TryAddSingleton<IIntentClassifier, HeuristicIntentClassifier>();

        // ---- retrieval ---------------------------------------------------------------
        services.TryAddSingleton<ISearchService, HybridSearchService>();

        // ---- pipeline ----------------------------------------------------------------
        services.TryAddSingleton<CapturePipeline>();
        services.TryAddSingleton<OcrQueueProcessor>();
        services.TryAddSingleton<CaptureScheduler>();
        services.TryAddSingleton<RetentionService>();

        return services;
    }

    /// <summary>
    /// Bring a resolved container up: migrate the schema, load settings, and connect the
    /// capture pipeline to the OCR queue. Must be awaited before the first capture.
    /// </summary>
    public static async Task<IServiceProvider> InitializeRecallOsAsync(
        this IServiceProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        await provider.GetRequiredService<DatabaseBootstrapper>()
            .InitializeAsync(cancellationToken)
            .ConfigureAwait(false);

        await provider.GetRequiredService<ISettingsStore>()
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);

        var pipeline = provider.GetRequiredService<CapturePipeline>();
        var ocr = provider.GetRequiredService<OcrQueueProcessor>();

        // The pipeline does not know the OCR queue exists; the host connects them. That is
        // what keeps the capture stage independently testable and lets a headless build
        // capture without ever starting a recogniser.
        pipeline.FrameCaptured += (_, frame) => ocr.Enqueue(frame.Id);
        ocr.Start();

        return provider;
    }
}
