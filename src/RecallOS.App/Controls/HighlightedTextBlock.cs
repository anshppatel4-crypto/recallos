using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using RecallOS.Core.Models;

// System.Drawing is imported globally by UseWindowsForms, so these two names have to be
// pinned to their WPF meanings.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace RecallOS.App.Controls;

/// <summary>
/// Attached properties that render a search snippet with its matches highlighted.
/// </summary>
/// <remarks>
/// Highlighting is done by building <see cref="Run"/> inlines rather than by injecting
/// markup into the text. The search layer returns match positions as structured
/// <see cref="TextSpan"/> values, so the view never has to parse marker characters back out
/// of a string -- and text that happens to contain those markers cannot corrupt the display.
/// </remarks>
public static class HighlightedTextBlock
{
    public static readonly DependencyProperty SourceTextProperty =
        DependencyProperty.RegisterAttached(
            "SourceText",
            typeof(string),
            typeof(HighlightedTextBlock),
            new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty HighlightsProperty =
        DependencyProperty.RegisterAttached(
            "Highlights",
            typeof(IEnumerable),
            typeof(HighlightedTextBlock),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty HighlightBrushProperty =
        DependencyProperty.RegisterAttached(
            "HighlightBrush",
            typeof(Brush),
            typeof(HighlightedTextBlock),
            new PropertyMetadata(null, OnChanged));

    public static void SetSourceText(DependencyObject element, string value) =>
        element.SetValue(SourceTextProperty, value);

    public static string GetSourceText(DependencyObject element) =>
        (string)element.GetValue(SourceTextProperty);

    public static void SetHighlights(DependencyObject element, IEnumerable? value) =>
        element.SetValue(HighlightsProperty, value);

    public static IEnumerable? GetHighlights(DependencyObject element) =>
        (IEnumerable?)element.GetValue(HighlightsProperty);

    public static void SetHighlightBrush(DependencyObject element, Brush? value) =>
        element.SetValue(HighlightBrushProperty, value);

    public static Brush? GetHighlightBrush(DependencyObject element) =>
        (Brush?)element.GetValue(HighlightBrushProperty);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not TextBlock textBlock)
        {
            return;
        }

        var text = GetSourceText(textBlock) ?? string.Empty;
        textBlock.Inlines.Clear();

        if (text.Length == 0)
        {
            return;
        }

        var spans = NormalizeSpans(GetHighlights(textBlock), text.Length);

        if (spans.Count == 0)
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        var brush = GetHighlightBrush(textBlock) ?? Brushes.Yellow;
        var cursor = 0;

        foreach (var span in spans)
        {
            if (span.Start > cursor)
            {
                textBlock.Inlines.Add(new Run(text[cursor..span.Start]));
            }

            textBlock.Inlines.Add(new Run(text[span.Start..span.End])
            {
                Background = brush,
                FontWeight = FontWeights.SemiBold
            });

            cursor = span.End;
        }

        if (cursor < text.Length)
        {
            textBlock.Inlines.Add(new Run(text[cursor..]));
        }
    }

    /// <summary>
    /// Clamp spans to the string and drop any that overlap an earlier one. The snippet the
    /// UI shows can be shorter than the text the spans were computed against, and an
    /// out-of-range slice here would throw inside a layout pass, which is very hard to trace.
    /// </summary>
    private static List<TextSpan> NormalizeSpans(IEnumerable? source, int length)
    {
        var result = new List<TextSpan>();
        if (source is null)
        {
            return result;
        }

        foreach (var item in source)
        {
            if (item is not TextSpan span)
            {
                continue;
            }

            var start = Math.Clamp(span.Start, 0, length);
            var end = Math.Clamp(span.End, start, length);

            if (end > start)
            {
                result.Add(new TextSpan(start, end - start));
            }
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));

        var merged = new List<TextSpan>(result.Count);
        foreach (var span in result)
        {
            if (merged.Count > 0 && span.Start < merged[^1].End)
            {
                continue;
            }

            merged.Add(span);
        }

        return merged;
    }
}
