using CommunityToolkit.Mvvm.ComponentModel;
using LTC.Core.Logging;

namespace LTC.App.ViewModels;

/// <summary>One row in the in-app Logs view. Wraps a <see cref="PlainLogEntry"/>
/// and pre-formats display strings so XAML doesn't need converters.</summary>
public sealed partial class PlainLogEntryViewModel : ObservableObject
{
    public PlainLogEntry Model { get; }
    public string TimestampText { get; }
    public string Message { get; }
    public string LevelLabel { get; }
    public string LevelColorKey { get; }

    public PlainLogEntryViewModel(PlainLogEntry model)
    {
        Model = model;
        TimestampText = model.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
        Message = model.Message;
        (LevelLabel, LevelColorKey) = model.Level switch
        {
            PlainLogLevel.Info    => ("INFO",  "StatusOkBrush"),
            PlainLogLevel.Warning => ("WARN",  "StatusWarnBrush"),
            PlainLogLevel.Error   => ("ERROR", "StatusErrBrush"),
            _                     => ("",      "TextDimBrush"),
        };
    }

    /// <summary>Force the brush-key binding to re-resolve. Called when the
    /// app theme changes — the string value didn't change, but the brush it
    /// resolves to did, so we have to nudge the binding.</summary>
    public void NotifyColorKeysChanged()
    {
        OnPropertyChanged(nameof(LevelColorKey));
    }
}
