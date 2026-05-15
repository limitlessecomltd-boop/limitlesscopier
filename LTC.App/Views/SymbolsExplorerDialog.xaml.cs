using System.Windows;
using System.Windows.Controls;
using LTC.Core.Symbols;

namespace LTC.App.Views;

public partial class SymbolsExplorerDialog : Window
{
    private readonly List<string> _allSymbols;

    /// <summary>If the user clicks Apply on a suggestion, these hold the result.
    /// Null when not applied.</summary>
    public string? AppliedPrefix { get; private set; }
    public string? AppliedSuffix { get; private set; }

    public SymbolsExplorerDialog(string accountLabel, IReadOnlyCollection<string>? symbols)
    {
        InitializeComponent();

        _allSymbols = (symbols ?? Array.Empty<string>())
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SubtitleText.Text = _allSymbols.Count == 0
            ? $"{accountLabel} hasn't reported any symbols yet. Connect the account first."
            : $"{accountLabel} · {_allSymbols.Count} symbols";

        StatusText.Text = $"{_allSymbols.Count} symbols total";
        SymbolList.ItemsSource = _allSymbols;

        if (_allSymbols.Count > 0)
        {
            // AI suggestion: scan catalog for the dominant prefix/suffix pattern.
            var sug = AffixSuggester.Suggest(_allSymbols);
            if (sug.HasSuggestion)
            {
                var summary = "";
                if (sug.SuggestedPrefix.Length > 0)
                    summary += $"prefix \"{sug.SuggestedPrefix}\"";
                if (sug.SuggestedSuffix.Length > 0)
                {
                    if (summary.Length > 0) summary += " and ";
                    summary += $"suffix \"{sug.SuggestedSuffix}\"";
                }
                SuggestionText.Text =
                    $"This broker appears to use {summary}. " +
                    $"({(int)(sug.Coverage * 100)}% of {sug.CatalogSize} symbols match this pattern.) " +
                    $"Click Apply to use these in the account settings.";
                SuggestionBanner.Visibility = Visibility.Visible;

                _suggestedPrefix = sug.SuggestedPrefix;
                _suggestedSuffix = sug.SuggestedSuffix;
            }
        }
    }

    private string _suggestedPrefix = "";
    private string _suggestedSuffix = "";

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = (SearchBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(q))
        {
            SymbolList.ItemsSource = _allSymbols;
            StatusText.Text = $"{_allSymbols.Count} symbols total";
            return;
        }
        var filtered = _allSymbols
            .Where(s => s.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SymbolList.ItemsSource = filtered;
        StatusText.Text = $"{filtered.Count} of {_allSymbols.Count} match";
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        AppliedPrefix = _suggestedPrefix;
        AppliedSuffix = _suggestedSuffix;
        DialogResult = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
