using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Models;
using RecallOS.Core.Ocr;
using RecallOS.Core.Storage;

namespace RecallOS.Core.Pipeline;

/// <summary>
/// The background half of the pipeline: turns stored images into searchable text.
/// </summary>
/// <remarks>
/// <para>
/// Work arrives two ways. New captures are signalled through a channel, so a frame the user
/// just took is processed immediately. A periodic sweep also picks up anything still marked
/// <see cref="OcrStatus.Pending"/>, which is what recovers a backlog left by a crash, a
/// close mid-queue, or the stretch before language data was installed. The sweep makes the
/// channel an optimisation rather than a correctness requirement: losing a signal delays a
/// frame, it does not strand it.
/// </para>
/// <para>
/// The channel is bounded and drops its oldest entry when full. Under a burst, the newest
/// frames are the ones the user is most likely to search for, and anything dropped is
/// recovered by the next sweep anyway.
/// </para>
/// <para>
/// Each frame is processed to completion -- text, chunks, embeddings, intent -- before the
/// next is started. OCR already saturates a core, and running several in parallel on a
/// machine the user is actively working on trades their responsiveness for throughput they
/// will not notice.
/// </para>
/// </remarks>
public sealed class OcrQueueProcessor : IAsyncDisposable
{
    private const int SweepBatchSize = 25;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(20);

    private readonly IFrameRepository _repository;
    private readonly IOcrEngine _engine;
    private readonly FrameFileStore _files;
    private readonly TextChunker _chunker;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IIntentClassifier _intent;
    private readonly ISettingsStore _settings;
    private readonly ILogger<OcrQueueProcessor> _logger;

    private readonly Channel<long> _queue = Channel.CreateBounded<long>(
        new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private int _pendingCount;

    public OcrQueueProcessor(
        IFrameRepository repository,
        IOcrEngine engine,
        FrameFileStore files,
        TextChunker chunker,
        IEmbeddingProvider embeddings,
        IIntentClassifier intent,
        ISettingsStore settings,
        ILogger<OcrQueueProcessor>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _intent = intent ?? throw new ArgumentNullException(nameof(intent));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OcrQueueProcessor>.Instance;
    }

    /// <summary>Raised after a frame's text has been written back, so the UI can refresh it.</summary>
    public event EventHandler<CaptureFrame>? FrameProcessed;

    /// <summary>Raised when the backlog size changes, for the status bar.</summary>
    public event EventHandler<int>? QueueDepthChanged;

    public int QueueDepth => Volatile.Read(ref _pendingCount);

    public void Start()
    {
        if (_worker is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_cancellation.Token));
        _logger.LogInformation("OCR queue processor started (engine: {Engine}).", _engine.Name);
    }

    /// <summary>Signal that a frame is ready for text extraction.</summary>
    public void Enqueue(long frameId)
    {
        if (_queue.Writer.TryWrite(frameId))
        {
            UpdateDepth(Interlocked.Increment(ref _pendingCount));
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var lastSweep = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for a signal, but never longer than the sweep interval, so a backlog
                // is still drained on a quiet system where nothing is being captured.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SweepInterval);

                try
                {
                    while (_queue.Reader.TryRead(out var frameId))
                    {
                        UpdateDepth(Interlocked.Decrement(ref _pendingCount));
                        await ProcessFrameAsync(frameId, cancellationToken).ConfigureAwait(false);
                    }

                    await _queue.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The sweep timer elapsed rather than a real shutdown.
                }

                if (DateTimeOffset.UtcNow - lastSweep >= SweepInterval)
                {
                    lastSweep = DateTimeOffset.UtcNow;
                    await SweepAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // The worker must outlive any single failure; otherwise one unreadable
                // frame silently ends all text extraction for the session.
                _logger.LogError(ex, "OCR worker loop error; continuing.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Pick up frames the channel never saw, or never finished.</summary>
    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var pending = await _repository.GetPendingOcrAsync(SweepBatchSize, cancellationToken).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            await BackfillEmbeddingsAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogDebug("Sweep found {Count} frames awaiting text extraction.", pending.Count);

        foreach (var frame in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessFrameAsync(frame.Id, cancellationToken, frame).ConfigureAwait(false);
        }
    }

    private async Task ProcessFrameAsync(
        long frameId,
        CancellationToken cancellationToken,
        CaptureFrame? known = null)
    {
        var frame = known ?? await _repository.GetAsync(frameId, cancellationToken).ConfigureAwait(false);
        if (frame is null || frame.IsDeleted)
        {
            return;
        }

        // Already handled by the other path, or by a previous run.
        if (frame.OcrStatus is OcrStatus.Completed or OcrStatus.Failed)
        {
            return;
        }

        if (!_settings.Current.OcrEnabled)
        {
            await _repository
                .UpdateOcrAsync(frameId, OcrStatus.Skipped, string.Empty, 0, null, null, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!_engine.IsAvailable)
        {
            // Left Pending, not Skipped: the moment language data is installed, the sweep
            // picks these up and the user's whole backlog becomes searchable.
            _logger.LogDebug("OCR engine unavailable ({Reason}); leaving frame {FrameId} queued.",
                _engine.UnavailableReason,
                frameId);
            return;
        }

        var imagePath = _files.ResolveImage(frame.ImagePath);
        if (!File.Exists(imagePath))
        {
            await _repository.UpdateOcrAsync(
                frameId,
                OcrStatus.Failed,
                string.Empty,
                0,
                _engine.Name,
                "The captured image is missing from the store.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await _engine.RecognizeAsync(imagePath, cancellationToken).ConfigureAwait(false);

            await _repository.UpdateOcrAsync(
                frameId,
                OcrStatus.Completed,
                result.Text,
                result.Confidence,
                result.Engine,
                null,
                cancellationToken).ConfigureAwait(false);

            var chunks = _chunker.Chunk(frameId, result.Text, result.Words);
            await _repository.ReplaceChunksAsync(frameId, chunks, cancellationToken).ConfigureAwait(false);

            if (_settings.Current.SemanticIndexingEnabled && chunks.Count > 0)
            {
                await IndexChunksAsync(frameId, cancellationToken).ConfigureAwait(false);
            }

            // Intent needs the text, so it can only run now, not at capture time.
            var enriched = await _repository.GetAsync(frameId, cancellationToken).ConfigureAwait(false);
            if (enriched is not null)
            {
                var prediction = _intent.Classify(enriched);
                if (prediction.Confidence > 0)
                {
                    await _repository
                        .UpdateIntentAsync(frameId, prediction.Label, prediction.Confidence, cancellationToken)
                        .ConfigureAwait(false);
                }

                FrameProcessed?.Invoke(this, enriched);
            }

            _logger.LogDebug(
                "Extracted {Characters} characters from frame {FrameId} in {Duration}ms.",
                result.Text.Length,
                frameId,
                (int)result.Duration.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Text extraction failed for frame {FrameId}.", frameId);

            // Recorded as Failed with the reason, so the frame is not retried forever and
            // the user can see why it has no text.
            await _repository.UpdateOcrAsync(
                frameId,
                OcrStatus.Failed,
                string.Empty,
                0,
                _engine.Name,
                ex.Message,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Embed every chunk of one frame that does not yet have a vector.</summary>
    private async Task IndexChunksAsync(long frameId, CancellationToken cancellationToken)
    {
        var chunks = await _repository.GetChunksAsync(frameId, cancellationToken).ConfigureAwait(false);
        if (chunks.Count == 0)
        {
            return;
        }

        var vectors = await _embeddings
            .EmbedBatchAsync(chunks.Select(c => c.Text).ToArray(), cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < chunks.Count; i++)
        {
            await _repository
                .SaveEmbeddingAsync(chunks[i].Id, _embeddings.ModelId, vectors[i], cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Catch up chunks with no vector under the current model. This is what makes switching
    /// embedding models a background task rather than a migration: point the provider at a
    /// new model id and the store refills itself while remaining fully searchable.
    /// </summary>
    private async Task BackfillEmbeddingsAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Current.SemanticIndexingEnabled)
        {
            return;
        }

        var ids = await _repository
            .GetChunkIdsWithoutEmbeddingsAsync(_embeddings.ModelId, SweepBatchSize, cancellationToken)
            .ConfigureAwait(false);

        foreach (var chunkId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = await _repository.GetChunkAsync(chunkId, cancellationToken).ConfigureAwait(false);
            if (chunk is null)
            {
                continue;
            }

            await _repository
                .SaveEmbeddingAsync(chunkId, _embeddings.ModelId, _embeddings.Embed(chunk.Text), cancellationToken)
                .ConfigureAwait(false);
        }

        if (ids.Count > 0)
        {
            _logger.LogDebug("Backfilled {Count} embeddings.", ids.Count);
        }
    }

    private void UpdateDepth(int depth) => QueueDepthChanged?.Invoke(this, Math.Max(0, depth));

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();

        if (_worker is not null)
        {
            try
            {
                // Bounded wait: a frame mid-OCR should not hold up application shutdown.
                await _worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                _logger.LogDebug("OCR worker did not stop cleanly within the shutdown window.");
            }
        }

        _cancellation.Dispose();
        _cancellation = null;
        _worker = null;
    }
}
