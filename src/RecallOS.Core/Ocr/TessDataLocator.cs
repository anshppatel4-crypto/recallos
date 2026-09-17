using System.IO;
using RecallOS.Core.Common;

namespace RecallOS.Core.Ocr;

/// <summary>
/// Finds the Tesseract language data, and installs it on explicit request.
/// </summary>
/// <remarks>
/// Language data is a 4-15 MB download that cannot be redistributed in every build, so
/// RecallOS treats it as an optional, user-installed asset. Nothing here reaches the
/// network unless <see cref="DownloadLanguageAsync"/> is called, which only happens when
/// the user presses the button in Settings: a local-first tool does not quietly fetch
/// things on launch.
/// </remarks>
public sealed class TessDataLocator
{
    /// <summary>
    /// The "fast" variants: roughly four times quicker than the legacy models at an accuracy
    /// cost that does not matter for screen text, which is rendered rather than photographed.
    /// </summary>
    private const string DownloadRoot = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main";

    private readonly RecallPaths _paths;

    public TessDataLocator(RecallPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// The directory Tesseract should be pointed at, or null when no language data exists
    /// anywhere we know to look.
    /// </summary>
    public string? Resolve(string languages)
    {
        foreach (var candidate in CandidateDirectories())
        {
            if (HasAllLanguages(candidate, languages))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The directory downloads are written to. Inside the store, so it survives reinstalls.</summary>
    public string InstallDirectory => _paths.TessData;

    /// <summary>Language codes already present in the store, e.g. <c>eng</c>, <c>deu</c>.</summary>
    public IReadOnlyList<string> GetInstalledLanguages()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in CandidateDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.traineddata"))
            {
                found.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        return found.ToArray();
    }

    /// <summary>
    /// Fetch one language file into the store. Downloads to a temporary name and moves it
    /// into place, so an interrupted transfer can never leave a truncated file that
    /// Tesseract would fail on in a much more confusing way.
    /// </summary>
    public async Task DownloadLanguageAsync(
        string language,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        // The code goes straight into a URL; refuse anything that is not a plain code.
        if (!language.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException($"'{language}' is not a valid Tesseract language code.", nameof(language));
        }

        Directory.CreateDirectory(_paths.TessData);

        var destination = Path.Combine(_paths.TessData, $"{language}.traineddata");
        var temporary = destination + ".download";

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await client
            .GetAsync($"{DownloadRoot}/{language}.traineddata", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0L;
        var written = 0L;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = File.Create(temporary))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                if (total > 0)
                {
                    progress?.Report((double)written / total);
                }
            }
        }

        File.Move(temporary, destination, overwrite: true);
        progress?.Report(1d);
    }

    /// <summary>
    /// Where language data might live, in priority order: the user store first so a
    /// downloaded file always wins, then the app folder for a bundled copy, then the
    /// environment variable a system-wide Tesseract install would set.
    /// </summary>
    private IEnumerable<string> CandidateDirectories()
    {
        yield return _paths.TessData;
        yield return Path.Combine(AppContext.BaseDirectory, "tessdata");

        var prefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            yield return prefix;

            // TESSDATA_PREFIX is documented inconsistently: some installers point it at the
            // parent of tessdata, others at tessdata itself. Try both.
            yield return Path.Combine(prefix, "tessdata");
        }
    }

    private static bool HasAllLanguages(string directory, string languages)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        foreach (var language in languages.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!File.Exists(Path.Combine(directory, $"{language.Trim()}.traineddata")))
            {
                return false;
            }
        }

        return true;
    }
}
