using FluentAssertions;
using LTC.Core.Symbols;
using Xunit;

namespace LTC.Tests.Symbols;

public class SuffixPrefixSymbolMapperTests
{
    private readonly SuffixPrefixSymbolMapper _mapper = new();

    [Fact]
    public void NoAffixes_PassThrough()
    {
        var catalog = new[] { "EURUSD" };
        _mapper.Resolve("EURUSD", "", "", "", "", catalog).Should().Be("EURUSD");
    }

    [Fact]
    public void MasterToSlave_SuffixApplied()
    {
        // Master fires XAUUSD (no affixes); slave wants suffix "ecn"
        var slaveCatalog = new[] { "XAUUSDecn", "EURUSDecn" };
        _mapper.Resolve("XAUUSD",
            sourcePrefix: "", sourceSuffix: "",
            targetPrefix: "", targetSuffix: "ecn",
            targetCatalog: slaveCatalog).Should().Be("XAUUSDecn");
    }

    [Fact]
    public void SlaveToMaster_SuffixStripped()
    {
        // Now reverse: slave fires XAUUSDecn (suffix "ecn"); master has no suffix
        var masterCatalog = new[] { "XAUUSD", "EURUSD" };
        _mapper.Resolve("XAUUSDecn",
            sourcePrefix: "", sourceSuffix: "ecn",
            targetPrefix: "", targetSuffix: "",
            targetCatalog: masterCatalog).Should().Be("XAUUSD");
    }

    [Fact]
    public void BothSidesHaveAffixes_CrossTranslation()
    {
        // Source uses prefix "m."; target uses suffix ".m"
        var targetCatalog = new[] { "XAUUSD.m", "EURUSD.m" };
        _mapper.Resolve("m.XAUUSD",
            sourcePrefix: "m.", sourceSuffix: "",
            targetPrefix: "",   targetSuffix: ".m",
            targetCatalog: targetCatalog).Should().Be("XAUUSD.m");
    }

    [Fact]
    public void SourceSymbolDoesNotMatchSourceAffix_StripGracefullyDoesNothing()
    {
        // Master account is configured with suffix "ecn" but happens to fire a
        // symbol that doesn't have that suffix (e.g. legacy symbol or different
        // category). We strip nothing and pass it through.
        var targetCatalog = new[] { "BTCUSD" };
        _mapper.Resolve("BTCUSD",
            sourcePrefix: "", sourceSuffix: "ecn",
            targetPrefix: "", targetSuffix: "",
            targetCatalog: targetCatalog).Should().Be("BTCUSD");
    }

    [Fact]
    public void TargetCatalogMissingSymbol_ReturnsNull()
    {
        // User-configured slave suffix doesn't actually exist in slave catalog.
        var targetCatalog = new[] { "XAUUSD.m" };
        _mapper.Resolve("XAUUSD",
            sourcePrefix: "", sourceSuffix: "",
            targetPrefix: "", targetSuffix: "ecn",
            targetCatalog: targetCatalog).Should().BeNull();
    }

    [Fact]
    public void EmptyTargetCatalog_TrustsUser()
    {
        // Slave hasn't reported its symbols yet — return the candidate verbatim
        // so the engine can attempt the trade rather than skipping it.
        _mapper.Resolve("XAUUSD",
            sourcePrefix: "", sourceSuffix: "",
            targetPrefix: "", targetSuffix: "ecn",
            targetCatalog: null).Should().Be("XAUUSDecn");
    }

    [Fact]
    public void EmptySourceSymbol_ReturnsNull()
    {
        _mapper.Resolve("",
            sourcePrefix: "", sourceSuffix: "",
            targetPrefix: "", targetSuffix: "",
            targetCatalog: null).Should().BeNull();
    }

    [Fact]
    public void StripStrippedEverything_ReturnsNull()
    {
        // Pathological: source symbol IS the affix. Stripping leaves empty core.
        _mapper.Resolve("ecn",
            sourcePrefix: "", sourceSuffix: "ecn",
            targetPrefix: "", targetSuffix: "",
            targetCatalog: null).Should().BeNull();
    }

    [Fact]
    public void AffixCaseInsensitive()
    {
        // Master fires "XAUUSDECN" but configured with lowercase suffix "ecn"
        _mapper.Resolve("XAUUSDECN",
            sourcePrefix: "", sourceSuffix: "ecn",
            targetPrefix: "", targetSuffix: "",
            targetCatalog: null).Should().Be("XAUUSD");
    }
}
