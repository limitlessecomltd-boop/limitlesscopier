namespace LTC.Core.Symbols;

/// <summary>
/// Inspects a broker's symbol catalog and suggests the most likely prefix
/// and/or suffix pattern. Useful for the "Suggest" button in the symbols
/// explorer dialog.
///
/// Heuristic:
/// - Group symbols by their non-alphabetic affix (a run of dots, hyphens, lowercase
///   letters, etc) at the start or end.
/// - The dominant group's affix is the suggestion, as long as it covers more than
///   half the catalog and isn't trivially short.
/// - Otherwise we suggest empty affixes (the broker uses standard names).
/// </summary>
public static class AffixSuggester
{
    public static AffixSuggestion Suggest(IReadOnlyCollection<string> symbols)
    {
        if (symbols is null || symbols.Count == 0)
            return new AffixSuggestion("", "", 0, 0);

        // Look at FX-style 6-letter cores (most reliable signal) for affix detection.
        // We consider a symbol to "fit" if removing some prefix and/or suffix leaves
        // a 6-character alphabetic core. Then we group by the (prefix, suffix) pair
        // and pick the dominant grouping.
        var candidates = new Dictionary<(string Prefix, string Suffix), int>();

        foreach (var sym in symbols)
        {
            if (string.IsNullOrEmpty(sym)) continue;

            // Try every (prefix-length, suffix-length) split that yields a
            // 6-character alphabetic core. Prefix 0..3 chars, suffix 0..6 chars.
            for (int p = 0; p <= Math.Min(3, sym.Length); p++)
            {
                for (int s = 0; s <= Math.Min(6, sym.Length - p); s++)
                {
                    int coreLen = sym.Length - p - s;
                    if (coreLen != 6) continue;

                    var core = sym.Substring(p, 6);
                    if (!IsAllAlpha(core)) continue;

                    var prefix = sym.Substring(0, p);
                    var suffix = sym.Substring(p + 6, s);

                    // Skip nonsensical splits where the affix contains a letter that
                    // looks like it's part of the symbol itself (e.g. "EU" prefix
                    // turning RUSD into a "core" — heuristic guard).
                    if (LooksLikeNoise(prefix) || LooksLikeNoise(suffix)) continue;

                    var key = (prefix, suffix);
                    candidates[key] = candidates.GetValueOrDefault(key) + 1;
                }
            }
        }

        if (candidates.Count == 0)
            return new AffixSuggestion("", "", 0, symbols.Count);

        // Pick the affix pair that covers the most symbols. Tie-break: prefer the
        // shortest affixes (Occam's razor — a broker is more likely to add ".m"
        // than ".micropro_extended").
        var best = candidates
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key.Prefix.Length + kv.Key.Suffix.Length)
            .First();

        // Confidence: only return non-empty affixes if they cover > 50% of the
        // catalog. Otherwise the broker probably uses mixed naming and the
        // suggestion would be misleading.
        var coverage = (double)best.Value / symbols.Count;
        if (best.Key.Prefix.Length == 0 && best.Key.Suffix.Length == 0)
        {
            // Most symbols are plain; suggest empty affixes.
            return new AffixSuggestion("", "", coverage, symbols.Count);
        }
        if (coverage < 0.5)
        {
            return new AffixSuggestion("", "", 0, symbols.Count);
        }

        return new AffixSuggestion(best.Key.Prefix, best.Key.Suffix, coverage, symbols.Count);
    }

    private static bool IsAllAlpha(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (!char.IsLetter(s[i])) return false;
        }
        return true;
    }

    /// <summary>An affix is "noise" if it's mixed-case or contains digits/symbols
    /// inconsistently — those are likely random splits, not real broker affixes.
    /// Currently a permissive guard: real broker affixes can be lowercase ("ecn"),
    /// uppercase ("ECN"), or contain punctuation (".m"). We don't reject any of
    /// these — the >50% coverage gate in <see cref="Suggest"/> is what filters
    /// out spurious matches.</summary>
    private static bool LooksLikeNoise(string s) => false;
}

/// <summary>
/// Affix suggestion result. Coverage is 0.0 to 1.0 — the fraction of catalog
/// symbols that match the suggested pattern.
/// </summary>
public sealed record AffixSuggestion(
    string SuggestedPrefix,
    string SuggestedSuffix,
    double Coverage,
    int CatalogSize)
{
    public bool HasSuggestion =>
        (SuggestedPrefix.Length > 0 || SuggestedSuffix.Length > 0) && Coverage >= 0.5;
}
