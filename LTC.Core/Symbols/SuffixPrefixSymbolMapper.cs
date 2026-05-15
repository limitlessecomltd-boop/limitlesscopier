namespace LTC.Core.Symbols;

/// <summary>
/// Resolves a symbol from one broker's naming convention to another's, using
/// only user-supplied prefixes and suffixes on each account.
///
/// Bidirectional algorithm:
///   1. Start with the source symbol (e.g. <c>XAUUSDecn</c> from a master with suffix "ecn").
///   2. Strip the source's prefix and suffix → canonical core (e.g. <c>XAUUSD</c>).
///   3. Apply the target's prefix and suffix → final symbol (e.g. <c>XAUUSD.m</c> for
///      a slave with suffix ".m").
///
/// Symmetric: works master→slave OR slave→master with the same code path. The
/// routing engine just passes whichever account is the "source" (where the trade
/// originated) and whichever is the "target" (where it's being copied).
/// </summary>
public sealed class SuffixPrefixSymbolMapper : ISymbolMapper
{
    public string? Resolve(
        string sourceSymbol,
        string? sourcePrefix,
        string? sourceSuffix,
        string? targetPrefix,
        string? targetSuffix,
        IReadOnlyCollection<string>? targetCatalog)
    {
        if (string.IsNullOrWhiteSpace(sourceSymbol)) return null;

        // 1. Strip the source's prefix/suffix to get the canonical core. We strip
        //    case-insensitively (brokers can be inconsistent).
        var core = sourceSymbol;
        if (!string.IsNullOrEmpty(sourcePrefix)
            && core.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            core = core.Substring(sourcePrefix.Length);
        }
        if (!string.IsNullOrEmpty(sourceSuffix)
            && core.EndsWith(sourceSuffix, StringComparison.OrdinalIgnoreCase))
        {
            core = core.Substring(0, core.Length - sourceSuffix.Length);
        }
        if (string.IsNullOrEmpty(core)) return null;

        // 2. Apply the target's affixes.
        var candidate = (targetPrefix ?? "") + core + (targetSuffix ?? "");

        // 3. Verify against the target's catalog if we have one. Empty catalog =
        //    "trust the user" (slave hasn't reported its symbols yet).
        if (targetCatalog is null || targetCatalog.Count == 0) return candidate;

        // Catalog is case-insensitive, but return the user's expected casing.
        return targetCatalog.Contains(candidate, StringComparer.OrdinalIgnoreCase)
            ? candidate
            : null;
    }
}

/// <summary>
/// Bidirectional symbol mapper. The "source" is whichever side fired the event;
/// "target" is wherever it's being copied to. Both sides may have their own
/// broker-naming affixes.
/// </summary>
public interface ISymbolMapper
{
    /// <summary>
    /// Strip the source's affixes from the source symbol and apply the target's
    /// affixes to produce a symbol that the target broker recognizes.
    /// Returns null if the resulting symbol isn't in the target's catalog.
    /// </summary>
    string? Resolve(
        string sourceSymbol,
        string? sourcePrefix,
        string? sourceSuffix,
        string? targetPrefix,
        string? targetSuffix,
        IReadOnlyCollection<string>? targetCatalog);
}
