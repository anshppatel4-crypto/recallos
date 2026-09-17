using System.IO;

namespace RecallOS.Core.Common;

/// <summary>
/// Every path RecallOS touches, derived from one root. Keeping this in a single place
/// means "where is my data?" has exactly one answer, and relocating the store is a
/// one-line change rather than a hunt through the codebase.
/// </summary>
public sealed class RecallPaths
{
    public const string StoreFolderName = "RecallOS";

    public RecallPaths(string? root = null)
    {
        Root = root ?? DefaultRoot();
        Frames = Path.Combine(Root, "frames");
        Thumbnails = Path.Combine(Root, "thumbnails");
        Logs = Path.Combine(Root, "logs");
        TessData = Path.Combine(Root, "tessdata");
        DatabaseFile = Path.Combine(Root, "recallos.db");
    }

    /// <summary>The single folder that holds the whole store.</summary>
    public string Root { get; }

    /// <summary>Full-resolution captures, under <c>yyyy/MM/dd</c> subfolders.</summary>
    public string Frames { get; }

    /// <summary>Downscaled previews, mirroring the <see cref="Frames"/> layout.</summary>
    public string Thumbnails { get; }

    public string Logs { get; }

    /// <summary>Tesseract language data. Kept in the store so it survives app reinstalls.</summary>
    public string TessData { get; }

    public string DatabaseFile { get; }

    /// <summary><c>%LOCALAPPDATA%\RecallOS</c> -- local, not roaming: this data must not follow the user onto a server.</summary>
    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StoreFolderName);

    /// <summary>Create every directory in the store. Safe to call repeatedly.</summary>
    public RecallPaths EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Frames);
        Directory.CreateDirectory(Thumbnails);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(TessData);
        return this;
    }

    /// <summary>Resolve a store-relative image path to an absolute one.</summary>
    public string ResolveFrame(string relativePath) => Path.Combine(Frames, relativePath);

    public string ResolveThumbnail(string relativePath) => Path.Combine(Thumbnails, relativePath);
}
