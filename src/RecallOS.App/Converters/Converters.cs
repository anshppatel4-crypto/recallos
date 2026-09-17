using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RecallOS.Core.Maintenance;

// System.Drawing is imported globally by UseWindowsForms; these converters mean the WPF
// types throughout.
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Binding = System.Windows.Data.Binding;

namespace RecallOS.App.Converters;

/// <summary>
/// Loads a screenshot from disk into an image source.
/// </summary>
/// <remarks>
/// Three details matter and all three are easy to get wrong. <c>OnLoad</c> caching reads the
/// file fully and closes the handle, without which WPF keeps the PNG locked and retention
/// sweeps cannot delete it. <c>DecodePixelWidth</c> decodes straight to the display size, so
/// a list of 4K captures does not materialise 30 MB of pixels per row. <c>Freeze</c> makes
/// the result shareable across threads and cheap for WPF to render.
/// </remarks>
public sealed class ImagePathConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

            if (parameter is not null
                && int.TryParse(parameter.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                && width > 0)
            {
                image.DecodePixelWidth = width;
            }

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UriFormatException)
        {
            // A truncated or mid-write file is not worth an error dialog; the row simply
            // renders without a preview.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Renders a byte count the way a person would say it.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? "0 B" : RetentionService.FormatBytes(System.Convert.ToInt64(value, culture));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Relative time for recent moments, absolute for older ones. "12 minutes ago" is how
/// someone locates a memory; "last Tuesday 14:03" is how they locate a date.
/// </summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset moment)
        {
            return string.Empty;
        }

        var local = moment.ToLocalTime();
        var elapsed = DateTimeOffset.Now - local;

        return elapsed switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes} min ago",
            { TotalHours: < 24 } => $"{(int)elapsed.TotalHours} h ago",
            { TotalDays: < 2 } => $"yesterday {local:HH:mm}",
            { TotalDays: < 7 } => local.ToString("dddd HH:mm", culture),
            _ => local.ToString("d MMM yyyy HH:mm", culture)
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when its bound value is null or an empty string.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Set true to show on null instead of hiding.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        return isEmpty ^ Invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when a count is zero.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is null ? 0 : System.Convert.ToInt32(value, culture);
        return (count == 0) ^ Invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a boolean, for "enabled when not busy" bindings.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>
/// Binds a set of radio buttons or toggle buttons to one enum property: the converter
/// parameter names the value each control represents.
/// </summary>
public sealed class EnumEqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null
        && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Only the control being switched on should write; the one switching off would
        // otherwise race it and clobber the new value.
        if (value is not true || parameter is null)
        {
            return Binding.DoNothing;
        }

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return enumType.IsEnum ? Enum.Parse(enumType, parameter.ToString()!, ignoreCase: true) : Binding.DoNothing;
    }
}

/// <summary>
/// Turns an enum value into the words a person would use for it.
/// </summary>
/// <remarks>
/// Enum members are named for the code that reads them, not for the user who sees them:
/// "AllScreens" and "Keyword" are precise but they are not English. Rather than duplicate
/// every enum into a parallel list of display strings in the view model, the mapping lives
/// here, in the layer whose job is presentation.
/// </remarks>
public sealed class EnumDisplayConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        // CaptureTarget
        ["AllScreens"] = "All screens",
        ["ActiveScreen"] = "The active screen",
        ["ActiveWindow"] = "The active window",

        // SearchMode — matched to the labels on the segmented control, so the setting and
        // the toolbar never disagree about what a mode is called.
        ["Hybrid"] = "Hybrid  ·  words and meaning",
        ["Keyword"] = "Words  ·  exact matching",
        ["Semantic"] = "Meaning  ·  similarity"
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        if (Names.TryGetValue(name, out var friendly))
        {
            return friendly;
        }

        // Unmapped values still read sensibly: split PascalCase into words.
        var builder = new System.Text.StringBuilder(name.Length + 6);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                builder.Append(' ').Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                builder.Append(name[i]);
            }
        }

        return builder.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a timeline bucket's frame count to a bar height, on a logarithmic curve.
/// </summary>
/// <remarks>
/// Activity is extremely uneven: an idle hour has one or two frames, an active one has
/// hundreds. Scaled linearly, every quiet period would flatten to an invisible sliver and
/// the strip would only show the single busiest stretch. A log curve keeps quiet periods
/// legible while still ranking busy ones above them.
/// </remarks>
public sealed class ActivityHeightConverter : IValueConverter
{
    public double MaximumHeight { get; set; } = 34;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is null ? 0 : System.Convert.ToDouble(value, culture);
        if (count <= 0)
        {
            return 1d;
        }

        // Saturates near 120 frames in a bucket, which is a thoroughly busy slice.
        var scaled = Math.Log(1 + count) / Math.Log(1 + 120);
        return Math.Max(2d, Math.Min(1d, scaled) * MaximumHeight);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Gives each activity label a stable colour, so the timeline reads at a glance.</summary>
public sealed class IntentColorConverter : IValueConverter
{
    private static readonly Dictionary<string, Color> Palette = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Coding"] = Color.FromRgb(0x5A, 0x9B, 0xF5),
        ["Terminal"] = Color.FromRgb(0x4E, 0xC9, 0xB0),
        ["Communicating"] = Color.FromRgb(0xE8, 0x8B, 0x4C),
        ["Writing"] = Color.FromRgb(0xC5, 0x8A, 0xF0),
        ["Designing"] = Color.FromRgb(0xF0, 0x6E, 0xA8),
        ["Analyzing"] = Color.FromRgb(0xE8, 0xC4, 0x4C),
        ["Watching"] = Color.FromRgb(0xE5, 0x64, 0x5E),
        ["Reading"] = Color.FromRgb(0x7F, 0xC5, 0x6B),
        ["Browsing"] = Color.FromRgb(0x8A, 0x93, 0xA6)
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var label = value?.ToString();

        var color = !string.IsNullOrEmpty(label) && Palette.TryGetValue(label, out var known)
            ? known
            : Color.FromRgb(0x4A, 0x51, 0x60);

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
