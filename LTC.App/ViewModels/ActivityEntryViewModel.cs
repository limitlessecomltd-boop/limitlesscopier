using CommunityToolkit.Mvvm.ComponentModel;
using LTC.Core.Models;

namespace LTC.App.ViewModels;

/// <summary>
/// View model for one Activity tape entry. Pre-computes labels and color keys so
/// the XAML binds directly without converters.
/// </summary>
public partial class ActivityEntryViewModel : ObservableObject
{
    public ActivityEntry Model { get; }

    [ObservableProperty] private string kindLabel = "";
    [ObservableProperty] private string kindColorKey = "TextDimBrush";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string statusColorKey = "TextDimBrush";
    [ObservableProperty] private string symbol = "";
    [ObservableProperty] private string detailLine = "";
    [ObservableProperty] private string timestampText = "";
    [ObservableProperty] private string latencyText = "";
    [ObservableProperty] private string volumeText = "";
    [ObservableProperty] private bool showLatency;
    [ObservableProperty] private bool showVolume;
    [ObservableProperty] private bool showRetry;

    public ActivityEntryViewModel(ActivityEntry model)
    {
        Model = model;
        Refresh();
    }

    /// <summary>Re-read the model and update derived display fields. Called when an
    /// in-flight entry transitions to Success/Failed.</summary>
    public void Refresh()
    {
        Symbol = Model.Symbol ?? "";
        TimestampText = Model.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
        VolumeText = Model.Volume > 0 ? $"{Model.Volume:F2}" : "";
        ShowVolume = Model.Volume > 0;

        // Kind label and accent color
        (KindLabel, KindColorKey) = Model.Kind switch
        {
            ActivityKind.Open       => ("OPEN",       "StatusOkBrush"),
            ActivityKind.Close      => ("CLOSE",      "MasterBrush"),
            ActivityKind.Modify     => ("MODIFY",     "MasterBrush"),
            ActivityKind.Filtered   => ("FILTERED",   "TextDimBrush"),
            ActivityKind.Connect    => ("CONNECT",    "StatusOkBrush"),
            ActivityKind.Disconnect => ("DISCONNECT", "StatusErrBrush"),
            _                       => (Model.Kind.ToString().ToUpperInvariant(), "TextDimBrush")
        };

        // Override kind color when failed — failures are always red.
        if (Model.Status == ActivityStatus.Failed)
        {
            KindLabel = "FAILED";
            KindColorKey = "StatusErrBrush";
        }
        else if (Model.Status == ActivityStatus.Skipped)
        {
            // Filtered events keep their dimmed color
        }

        // Latency
        if (Model.InternalLatencyMicros > 0 && Model.Status == ActivityStatus.Success)
        {
            ShowLatency = true;
            LatencyText = $"{Model.InternalLatencyMicros / 1000.0:F2}ms";
        }
        else if (Model.Status == ActivityStatus.Skipped)
        {
            ShowLatency = true;
            LatencyText = "skipped";
        }
        else
        {
            ShowLatency = false;
            LatencyText = "";
        }

        // Detail line: "FTMO → Live A · Buy" or error message
        var arrow = Model.MasterAccountLabel is null && Model.SlaveAccountLabel is null
            ? null
            : $"{Model.MasterAccountLabel ?? "?"} → {Model.SlaveAccountLabel ?? "?"}";
        if (!string.IsNullOrEmpty(Model.ErrorMessage) && Model.Status != ActivityStatus.Success)
            DetailLine = arrow is null ? (Model.ErrorMessage ?? "") : $"{arrow} · {Model.ErrorMessage}";
        else if (arrow is not null && !string.IsNullOrEmpty(Model.OrderType))
            DetailLine = $"{arrow} · {Model.OrderType}";
        else if (arrow is not null)
            DetailLine = arrow;
        else
            DetailLine = Model.OrderType ?? "";

        // Status pill text
        StatusText = Model.Status switch
        {
            ActivityStatus.InFlight => "…",
            ActivityStatus.Success  => "OK",
            ActivityStatus.Failed   => "FAIL",
            ActivityStatus.Skipped  => "skipped",
            _                       => ""
        };
        StatusColorKey = Model.Status switch
        {
            ActivityStatus.Success  => "StatusOkBrush",
            ActivityStatus.Failed   => "StatusErrBrush",
            ActivityStatus.Skipped  => "TextDimBrush",
            ActivityStatus.InFlight => "StatusWarnBrush",
            _                       => "TextDimBrush"
        };

        // The Retry button used to show on Failed entries, but retrying a stale
        // trade event is risky (price moved, broker may reject again, position
        // may have closed in the meantime). For connection failures, the
        // auto-reconnect loop already retries continuously — no UI button needed.
        // We keep the property here in case a future retry strategy makes sense
        // (e.g. user-initiated reconnect-now), but for now it stays false.
        ShowRetry = false;
    }

    /// <summary>Re-fire ColorKey PropertyChanged events on theme change.</summary>
    public void NotifyColorKeysChanged()
    {
        OnPropertyChanged(nameof(KindColorKey));
        OnPropertyChanged(nameof(StatusColorKey));
    }
}
