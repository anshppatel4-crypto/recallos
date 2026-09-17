using RecallOS.Core.Models;
using RecallOS.Core.Search;
using Xunit;

namespace RecallOS.Core.Tests;

public class SearchQueryParserTests
{
    [Fact]
    public void PlainWords_BecomeTermsAndPrefixMatches()
    {
        var query = SearchQueryParser.Parse("invoice acme");

        Assert.Equal(["invoice", "acme"], query.Terms);
        Assert.Equal("\"invoice\"* \"acme\"*", query.MatchExpression);
        Assert.False(query.IsEmpty);
    }

    [Fact]
    public void QuotedText_BecomesAPhraseWithoutAWildcard()
    {
        var query = SearchQueryParser.Parse("\"pull request\"");

        Assert.Equal(["pull request"], query.Terms);
        Assert.Equal("\"pull request\"", query.MatchExpression);
    }

    [Fact]
    public void AppFilter_IsExtractedAndStrippedFromTheFreeText()
    {
        var query = SearchQueryParser.Parse("budget app:chrome");

        Assert.Equal(["chrome"], query.Apps);
        Assert.Equal(["budget"], query.Terms);
        Assert.Equal("budget", query.Text);
    }

    [Fact]
    public void TitleFilter_AcceptsQuotedValues()
    {
        var query = SearchQueryParser.Parse("title:\"Pull Request\"");

        Assert.Equal(["Pull Request"], query.Titles);
        Assert.Empty(query.Terms);
    }

    [Fact]
    public void OnFilter_SetsBothBoundsToASingleDay()
    {
        var query = SearchQueryParser.Parse("on:2026-03-04");

        Assert.NotNull(query.After);
        Assert.NotNull(query.Before);
        Assert.Equal(new DateTime(2026, 3, 4), query.After!.Value.DateTime);
        Assert.Equal(TimeSpan.FromDays(1), query.Before!.Value - query.After!.Value);
    }

    [Theory]
    [InlineData("after:today")]
    [InlineData("after:yesterday")]
    [InlineData("after:7d")]
    [InlineData("after:24h")]
    public void RelativeDates_AreUnderstood(string input)
    {
        Assert.NotNull(SearchQueryParser.Parse(input).After);
    }

    [Fact]
    public void ABareUrl_StaysFreeTextRatherThanBecomingAFilter()
    {
        // "http://..." contains a colon but "http" is not a filter key.
        var query = SearchQueryParser.Parse("http://example.com/report");

        Assert.Empty(query.Apps);
        Assert.Single(query.Terms);
        Assert.NotNull(query.MatchExpression);
    }

    [Fact]
    public void FtsOperators_AreNeutralisedRatherThanExecuted()
    {
        // Left raw, these would be parsed by FTS5 as operators and throw.
        var query = SearchQueryParser.Parse("NEAR(a b) OR* ^start");

        Assert.NotNull(query.MatchExpression);
        Assert.DoesNotContain("^", query.MatchExpression);
        Assert.DoesNotContain("(", query.MatchExpression);
    }

    [Fact]
    public void EmbeddedQuotes_CannotBreakOutOfThePhrase()
    {
        var expression = SearchQueryParser.BuildMatchExpression(["say \"hi\" now"]);

        Assert.NotNull(expression);

        // The sanitizer drops quote characters before the phrase is assembled, so the
        // result is a single well-formed phrase. What matters is the invariant, not the
        // mechanism: the only quotes left are the ones the parser put there itself.
        Assert.Equal("\"say hi now\"", expression);
        Assert.Equal(2, expression!.Count(c => c == '"'));
    }

    [Fact]
    public void AnEmptyQuery_IsReportedAsEmptySoTheCallerCanPageTheTimeline()
    {
        var query = SearchQueryParser.Parse("   ");

        Assert.True(query.IsEmpty);
        Assert.Null(query.MatchExpression);
    }

    [Fact]
    public void FiltersAloneAreNotEmpty_EvenWithNoSearchTerms()
    {
        var query = SearchQueryParser.Parse("app:code");

        Assert.False(query.IsEmpty);
        Assert.Null(query.MatchExpression);
    }

    [Fact]
    public void ModeAndLimit_AreCarriedThrough()
    {
        var query = SearchQueryParser.Parse("x", SearchMode.Keyword, limit: 5000);

        Assert.Equal(SearchMode.Keyword, query.Mode);
        // Clamped to the documented ceiling.
        Assert.Equal(2000, query.Limit);
    }
}
