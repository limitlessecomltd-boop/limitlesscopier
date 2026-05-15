using FluentAssertions;
using LTC.Core.Symbols;
using Xunit;

namespace LTC.Tests.Symbols;

public class AffixSuggesterTests
{
    [Fact]
    public void Empty_NoSuggestion()
    {
        var s = AffixSuggester.Suggest(Array.Empty<string>());
        s.HasSuggestion.Should().BeFalse();
    }

    [Fact]
    public void AllSymbolsHaveSuffix_SuggestsThatSuffix()
    {
        var symbols = new[] { "EURUSDecn", "GBPUSDecn", "XAUUSDecn", "USDJPYecn" };
        var s = AffixSuggester.Suggest(symbols);
        s.SuggestedSuffix.Should().Be("ecn");
        s.SuggestedPrefix.Should().Be("");
        s.HasSuggestion.Should().BeTrue();
    }

    [Fact]
    public void AllSymbolsHavePrefix_SuggestsThatPrefix()
    {
        var symbols = new[] { "m.EURUSD", "m.GBPUSD", "m.XAUUSD", "m.USDJPY" };
        var s = AffixSuggester.Suggest(symbols);
        s.SuggestedPrefix.Should().Be("m.");
        s.SuggestedSuffix.Should().Be("");
        s.HasSuggestion.Should().BeTrue();
    }

    [Fact]
    public void NoAffixes_SuggestsEmpty()
    {
        var symbols = new[] { "EURUSD", "GBPUSD", "XAUUSD", "USDJPY" };
        var s = AffixSuggester.Suggest(symbols);
        s.SuggestedPrefix.Should().Be("");
        s.SuggestedSuffix.Should().Be("");
    }

    [Fact]
    public void MixedCatalog_NoConsistentSuggestion()
    {
        // Half use suffix "ecn", half use suffix ".m" — neither has > 50% coverage
        // when split out, so we suggest empty.
        var symbols = new[] {
            "EURUSDecn", "GBPUSDecn",
            "XAUUSD.m", "USDJPY.m",
            "BTCUSDpro", "ETHUSDpro"
        };
        var s = AffixSuggester.Suggest(symbols);
        s.HasSuggestion.Should().BeFalse();
    }

    [Fact]
    public void DominantSuffixWithSomeOutliers_StillSuggestsIt()
    {
        // 4 of 5 use "ecn" → coverage > 50%
        var symbols = new[] { "EURUSDecn", "GBPUSDecn", "XAUUSDecn", "USDJPYecn", "BTCUSD" };
        var s = AffixSuggester.Suggest(symbols);
        s.SuggestedSuffix.Should().Be("ecn");
        s.HasSuggestion.Should().BeTrue();
        s.Coverage.Should().BeGreaterThan(0.5);
    }
}
