using CommunityToolkit.Mvvm.ComponentModel;
using LTC.Core.Models;

namespace LTC.App.ViewModels;

/// <summary>
/// View model for a CopyLink. Pre-computes display strings so the XAML binds to
/// simple properties rather than running formatters per render.
/// </summary>
public partial class CopyLinkViewModel : ObservableObject
{
    public CopyLink Model { get; }

    [ObservableProperty] private Guid masterAccountId;
    [ObservableProperty] private Guid slaveAccountId;
    [ObservableProperty] private string masterName = "";
    [ObservableProperty] private string slaveName = "";
    [ObservableProperty] private bool enabled;
    [ObservableProperty] private string lotSizingSummary = "";

    // Live counters: shown as a small badge on the link row.
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private int successCount;
    [ObservableProperty] private int skippedCount;
    [ObservableProperty] private int failedCount;
    [ObservableProperty] private string countersText = "no copies yet";
    [ObservableProperty] private string countersColorKey = "TextDimBrush";

    public CopyLinkViewModel(CopyLink model, string masterName, string slaveName)
    {
        Model = model;
        MasterAccountId = model.MasterAccountId;
        SlaveAccountId = model.SlaveAccountId;
        MasterName = masterName;
        SlaveName = slaveName;
        Enabled = model.Enabled;
        LotSizingSummary = FormatLotSizing(model.LotSizing);
    }

    /// <summary>Apply a fresh counter snapshot. Called by MainViewModel on the UI thread.</summary>
    public void ApplyCounters(LTC.Core.Routing.LinkCounters c)
    {
        TotalCount   = c.Total;
        SuccessCount = c.Successful;
        SkippedCount = c.Skipped;
        FailedCount  = c.Failed;

        if (c.Total == 0)
        {
            CountersText = "no copies yet";
            CountersColorKey = "TextDimBrush";
            return;
        }

        var rate = c.SuccessRate * 100;
        CountersText = $"{c.Total} copies · {rate:0}% ok";
        CountersColorKey = rate >= 90 ? "StatusOkBrush"
                         : rate >= 50 ? "StatusWarnBrush"
                         :              "StatusErrBrush";
    }

    private static string FormatLotSizing(LotSizingConfig cfg) => cfg.Mode switch
    {
        LotSizingMode.Fixed         => $"Fixed {cfg.Value:F2} lots",
        LotSizingMode.Multiplier    => $"×{cfg.Value:F2}",
        LotSizingMode.RiskPercent   => $"Risk {cfg.Value:F2}%",
        LotSizingMode.EquityRatio   => "Equity ratio",
        LotSizingMode.BalanceRatio  => "Balance ratio",
        _ => "—"
    };

    /// <summary>Refresh display fields after the model has been mutated.</summary>
    public void Refresh()
    {
        Enabled = Model.Enabled;
        LotSizingSummary = FormatLotSizing(Model.LotSizing);
    }

    /// <summary>Re-fire ColorKey PropertyChanged events on theme change.</summary>
    public void NotifyColorKeysChanged()
    {
        OnPropertyChanged(nameof(CountersColorKey));
    }
}
