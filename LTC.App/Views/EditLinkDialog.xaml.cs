using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTC.App.ViewModels;
using LTC.Core.Models;

namespace LTC.App.Views;

public partial class EditLinkDialog : Window
{
    public CopyLink? Result { get; private set; }
    private readonly CopyLink? _editing;

    public EditLinkDialog(IEnumerable<AccountViewModel> masters, IEnumerable<AccountViewModel> slaves)
        : this(masters, slaves, null) { }

    /// <summary>Edit-mode constructor. Pass an existing link to pre-fill the form.</summary>
    public EditLinkDialog(IEnumerable<AccountViewModel> masters, IEnumerable<AccountViewModel> slaves,
        CopyLink? existing)
    {
        InitializeComponent();
        _editing = existing;

        var masterList = new List<AccountViewModel>(masters);
        var slaveList  = new List<AccountViewModel>(slaves);
        MasterBox.ItemsSource = masterList;
        SlaveBox.ItemsSource  = slaveList;

        if (_editing is not null)
        {
            Title = "Edit copy link";
            // Pre-select master/slave from the existing link.
            var m = masterList.FirstOrDefault(a => a.Model.Id == _editing.MasterAccountId);
            if (m != null) MasterBox.SelectedItem = m;
            else if (masterList.Count > 0) MasterBox.SelectedIndex = 0;

            var s = slaveList.FirstOrDefault(a => a.Model.Id == _editing.SlaveAccountId);
            if (s != null) SlaveBox.SelectedItem = s;
            else if (slaveList.Count > 0) SlaveBox.SelectedIndex = 0;

            // Lot sizing mode + value
            var modeTag = _editing.LotSizing.Mode.ToString();
            for (int i = 0; i < LotModeBox.Items.Count; i++)
            {
                if (LotModeBox.Items[i] is ComboBoxItem cbi && (cbi.Tag as string) == modeTag)
                {
                    LotModeBox.SelectedIndex = i;
                    break;
                }
            }
            LotValueBox.Text = _editing.LotSizing.Value.ToString("0.##");
            MinLotBox.Text = _editing.LotSizing.MinLot.ToString("0.##");
            MaxLotBox.Text = _editing.LotSizing.MaxLot.ToString("0.##");
            ReverseBox.IsChecked = _editing.ReverseCopy;
        }
        else
        {
            if (masterList.Count > 0) MasterBox.SelectedIndex = 0;
            if (slaveList.Count > 0)  SlaveBox.SelectedIndex = 0;
        }

        LotModeBox.SelectionChanged += (_, _) => UpdateValueLabel();
        UpdateValueLabel();
    }

    private void UpdateValueLabel()
    {
        var tag = (LotModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Multiplier";
        ValueLabel.Text = tag switch
        {
            "Fixed"        => "Fixed lots",
            "Multiplier"   => "Multiplier",
            "RiskPercent"  => "Risk percent (%)",
            "EquityRatio"  => "Equity ratio (auto)",
            "BalanceRatio" => "Balance ratio (auto)",
            _              => "Value"
        };
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        var master = MasterBox.SelectedItem as AccountViewModel;
        var slave  = SlaveBox.SelectedItem as AccountViewModel;
        if (master is null) { ShowError("Select a master account."); return; }
        if (slave  is null) { ShowError("Select a slave account."); return; }
        if (master.Model.Id == slave.Model.Id)
        { ShowError("Master and slave must be different accounts."); return; }

        var modeTag = (LotModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Multiplier";
        if (!Enum.TryParse<LotSizingMode>(modeTag, out var mode))
        { ShowError("Invalid lot sizing mode."); return; }

        if (!double.TryParse(LotValueBox.Text, out var value) || value < 0)
        { ShowError("Value must be a non-negative number."); return; }

        if (!double.TryParse(MinLotBox.Text, out var minLot) || minLot < 0)
        { ShowError("Min lot must be a non-negative number."); return; }

        if (!double.TryParse(MaxLotBox.Text, out var maxLot) || maxLot < 0)
        { ShowError("Max lot must be a non-negative number."); return; }

        Result = new CopyLink
        {
            // Preserve Id when editing so persistence updates instead of inserts.
            Id = _editing?.Id ?? Guid.NewGuid(),
            CreatedAt = _editing?.CreatedAt ?? DateTime.UtcNow,
            MasterAccountId = master.Model.Id,
            SlaveAccountId  = slave.Model.Id,
            Enabled = true,
            LotSizing = new LotSizingConfig
            {
                Mode = mode,
                Value = value,
                MinLot = minLot,
                MaxLot = maxLot,
            },
            ReverseCopy = ReverseBox.IsChecked == true,
            CopyPending = true,
            CopySLTP = true,
            CopyModifications = true,
        };

        DialogResult = true;
    }

    private void ShowError(string msg) => ErrorText.Text = msg;
}
