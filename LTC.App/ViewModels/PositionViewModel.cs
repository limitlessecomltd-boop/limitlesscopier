using CommunityToolkit.Mvvm.ComponentModel;
using LTC.Core.Connections;
using LTC.Core.Models;

namespace LTC.App.ViewModels;

/// <summary>One row in the live positions table.</summary>
public sealed partial class PositionViewModel : ObservableObject
{
    /// <summary>Account this position lives on. Used by Close handlers to know
    /// which broker to send the close request to.</summary>
    public Guid AccountId { get; }

    [ObservableProperty] private ulong ticket;
    [ObservableProperty] private string symbol = "";
    [ObservableProperty] private string sideText = "";        // "BUY"/"SELL"
    [ObservableProperty] private string sideColorKey = "TextDimBrush";
    [ObservableProperty] private double volume;
    [ObservableProperty] private double openPrice;
    [ObservableProperty] private double currentPrice;
    [ObservableProperty] private double profit;
    [ObservableProperty] private string profitText = "";
    [ObservableProperty] private string profitColorKey = "TextDimBrush";
    [ObservableProperty] private double stopLoss;
    [ObservableProperty] private double takeProfit;

    public CopyOrderType OrderType { get; private set; }

    public PositionViewModel(Guid accountId, OpenPosition p)
    {
        AccountId = accountId;
        Apply(p);
    }

    /// <summary>Update fields from a fresh poll. Called every 1.5s by the poller.</summary>
    public void Apply(OpenPosition p)
    {
        Ticket = p.Ticket;
        Symbol = p.Symbol;
        OrderType = p.OrderType;
        (SideText, SideColorKey) = p.OrderType switch
        {
            CopyOrderType.Buy or CopyOrderType.BuyLimit or CopyOrderType.BuyStop
                => ("BUY",  "StatusOkBrush"),
            CopyOrderType.Sell or CopyOrderType.SellLimit or CopyOrderType.SellStop
                => ("SELL", "StatusErrBrush"),
            _   => ("?", "TextDimBrush"),
        };
        Volume = p.Volume;
        OpenPrice = p.OpenPrice;
        CurrentPrice = p.CurrentPrice;
        StopLoss = p.StopLoss;
        TakeProfit = p.TakeProfit;
        // Net profit = floating + swap + commission. Brokers usually report
        // commission as a negative; swap can swing either way.
        Profit = p.Profit + p.Swap + p.Commission;
        ProfitText = (Profit >= 0 ? "+" : "") + Profit.ToString("0.00");
        ProfitColorKey = Profit >= 0 ? "StatusOkBrush"
                       : Profit < 0  ? "StatusErrBrush"
                       :               "TextDimBrush";
    }

    /// <summary>Re-fire ColorKey PropertyChanged events on theme change so
    /// KeyToBrush bindings re-resolve.</summary>
    public void NotifyColorKeysChanged()
    {
        OnPropertyChanged(nameof(SideColorKey));
        OnPropertyChanged(nameof(ProfitColorKey));
    }
}
