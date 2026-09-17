using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.Extensions.Logging;
using RecallOS.Core.Common;

namespace RecallOS.Core.Storage;

/// <summary>
/// Owns the image files on disk: naming, foldering, thumbnailing and deletion.
/// </summary>
/// <remarks>
/// Images live outside SQLite deliberately. A month of continuous capture is tens of
/// gigabytes of PNG; putting that in the database would make every backup, vacuum and
/// integrity check proportional to the pixel volume rather than the text volume. The
/// database stores store-relative paths, so the whole folder stays movable.
/// </remarks>
public sealed class FrameFileStore
{
    private readonly RecallPaths _paths;
    private readonly ILogger<FrameFileStore> _logger;

    public FrameFileStore(RecallPaths paths, ILogger<FrameFileStore>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FrameFileStore>.Instance;
        _paths.EnsureCreated();
    }

    /// <summary>
    /// Write a capture and its thumbnail. Returns store-relative paths and the byte size
    /// of the full image.
    /// </summary>
    public async Task<StoredImage> SaveAsync(
        Bitmap bitmap,
        DateTimeOffset capturedAt,
        int thumbnailMaxEdge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        return await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Date-foldered so a single directory never holds a million entries, which
                // is where NTFS enumeration starts to hurt.
                var local = capturedAt.ToLocalTime();
                var relativeDirectory = Path.Combine(
                    local.ToString("yyyy"),
                    local.ToString("MM"),
                    local.ToString("dd"));

                // The GUID suffix makes the name unique without needing to probe the disk:
                // several captures can legitimately land in the same second.
                var fileName = $"frame-{local:HHmmss}-{Guid.NewGuid():N}.png";

                Directory.CreateDirectory(Path.Combine(_paths.Frames, relativeDirectory));
                Directory.CreateDirectory(Path.Combine(_paths.Thumbnails, relativeDirectory));

                var relativeImage = Path.Combine(relativeDirectory, fileName);
                var absoluteImage = Path.Combine(_paths.Frames, relativeImage);
                bitmap.Save(absoluteImage, ImageFormat.Png);

                string? relativeThumbnail = null;
                try
                {
                    relativeThumbnail = Path.Combine(relativeDirectory, Path.ChangeExtension(fileName, ".jpg"));
                    using var thumbnail = CreateThumbnail(bitmap, thumbnailMaxEdge);
                    SaveJpeg(thumbnail, Path.Combine(_paths.Thumbnails, relativeThumbnail), 80L);
                }
                catch (Exception ex)
                {
                    // A missing thumbnail costs the UI a little speed; it must never cost
                    // the user the frame itself.
                    _logger.LogWarning(ex, "Failed to write a thumbnail; the full image was still stored.");
                    relativeThumbnail = null;
                }

                var size = new FileInfo(absoluteImage).Length;
                return new StoredImage(relativeImage, relativeThumbnail, size, bitmap.Width, bitmap.Height);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete image and thumbnail files, then prune the directories they emptied.</summary>
    public void Delete(IEnumerable<string> relativeImagePaths)
    {
        ArgumentNullException.ThrowIfNull(relativeImagePaths);

        var touchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in relativeImagePaths)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            TryDelete(Path.Combine(_paths.Frames, relative), touchedDirectories);
            TryDelete(Path.Combine(_paths.Thumbnails, Path.ChangeExtension(relative, ".jpg")), touchedDirectories);
        }

        foreach (var directory in touchedDirectories)
        {
            TryPruneEmptyDirectories(directory);
        }
    }

    /// <summary>Total bytes held in the image and thumbnail trees.</summary>
    public long CalculateSizeBytes()
    {
        return DirectorySize(_paths.Frames) + DirectorySize(_paths.Thumbnails);
    }

    public string ResolveImage(string relativePath) => _paths.ResolveFrame(relativePath);

    public string ResolveThumbnail(string relativePath) => _paths.ResolveThumbnail(relativePath);

    /// <summary>
    /// Best available on-disk preview: the thumbnail if it exists, otherwise the full image.
    /// Returns null when neither file survives, which happens if the store was pruned
    /// externally while rows remained.
    /// </summary>
    public string? ResolveBestPreview(string? relativeImage, string? relativeThumbnail)
    {
        if (!string.IsNullOrEmpty(relativeThumbnail))
        {
            var thumbnail = ResolveThumbnail(relativeThumbnail);
            if (File.Exists(thumbnail))
            {
                return thumbnail;
            }
        }

        if (!string.IsNullOrEmpty(relativeImage))
        {
            var image = ResolveImage(relativeImage);
            if (File.Exists(image))
            {
                return image;
            }
        }

        return null;
    }

    private static Bitmap CreateThumbnail(Bitmap source, int maxEdge)
    {
        var scale = Math.Min(1d, (double)maxEdge / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var thumbnail = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(thumbnail);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return thumbnail;
    }

    private static void SaveJpeg(Bitmap bitmap, string path, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

        if (encoder is null)
        {
            bitmap.Save(path, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        using var parameter = new EncoderParameter(Encoder.Quality, quality);
        parameters.Param[0] = parameter;
        bitmap.Save(path, encoder, parameters);
    }

    private void TryDelete(string absolutePath, HashSet<string> touchedDirectories)
    {
        try
        {
            if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }

            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory))
            {
                touchedDirectories.Add(directory);
            }
        }
        catch (IOException ex)
        {
            // Typically a viewer still has the file mapped. The row is already gone, so the
            // file is orphaned rather than leaked back into search results.
            _logger.LogWarning(ex, "Could not delete {Path}; it will be retried on the next sweep.", absolutePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied deleting {Path}.", absolutePath);
        }
    }

    private void TryPruneEmptyDirectories(string directory)
    {
        try
        {
            var current = directory;
            var roots = new[] { _paths.Frames, _paths.Thumbnails };

            // Walk up while the directory is empty, stopping at the store roots so we never
            // delete the store structure itself.
            while (!string.IsNullOrEmpty(current)
                   && !roots.Contains(current, StringComparer.OrdinalIgnoreCase)
                   && Directory.Exists(current)
                   && !Directory.EnumerateFileSystemEntries(current).Any())
            {
                var parent = Path.GetDirectoryName(current);
                Directory.Delete(current);
                current = parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Directory prune stopped early at {Directory}.", directory);
        }
    }

    private static long DirectorySize(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        try
        {
            return new DirectoryInfo(root)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>Where a capture landed on disk.</summary>
public readonly record struct StoredImage(
    string RelativeImagePath,
    string? RelativeThumbnailPath,
    long FileSizeBytes,
    int Width,
    int Height);
