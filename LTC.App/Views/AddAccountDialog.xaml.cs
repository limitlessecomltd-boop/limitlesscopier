using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LTC.App.Services;
using LTC.Core.Models;

namespace LTC.App.Views;

public partial class AddAccountDialog : Window
{
    public Account? Result { get; private set; }

    private readonly Mt5IpDetector _detector = new();
    private readonly DispatcherTimer _detectTimer;
    private DateTime _detectStartedAt;
    private bool _detectedSuccessfully;
    private string? _lastAutoFilledHost;

    /// <summary>How long to keep retrying before giving up and showing manual instructions.</summary>
    private static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(30);

    private readonly Account? _editing;

    public AddAccountDialog() : this(null) { }

    /// <summary>Edit-mode constructor. Pass an existing account to pre-fill the
    /// form. The user can change any field; leaving the password blank keeps
    /// the existing one.</summary>
    public AddAccountDialog(Account? existing)
    {
        InitializeComponent();
        _editing = existing;

        _detectTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        _detectTimer.Tick += (_, _) => RunDetect();

        Loaded += (_, _) =>
        {
            if (_editing is not null)
            {
                Title = "Edit account";
                NameBox.Text   = _editing.DisplayName;
                LoginBox.Text  = _editing.Login.ToString();
                HostBox.Text   = _editing.Server;
                _lastAutoFilledHost = _editing.Server;  // suppress auto-detect overwrites
                PortBox.Text   = _editing.Port.ToString();
                BrokerBox.Text = _editing.BrokerLabel ?? "";
                PrefixBox.Text = _editing.SymbolPrefix ?? "";
                SuffixBox.Text = _editing.SymbolSuffix ?? "";
                RoleBox.SelectedIndex = _editing.Role == AccountRole.Master ? 0 : 1;

                // Account kind: select the matching radio. Setting IsChecked
                // fires OnAccountKindChanged which shows/hides PropSection.
                switch (_editing.Kind)
                {
                    case AccountKind.PropChallenge: KindChallengeRadio.IsChecked = true; break;
                    case AccountKind.PropFunded:    KindFundedRadio.IsChecked = true;    break;
                    default:                        KindPersonalRadio.IsChecked = true;  break;
                }

                // If editing a prop account, repopulate the prop fields.
                if (_editing.PropConfig is not null)
                {
                    var pc = _editing.PropConfig;
                    FirmNameBox.Text          = pc.FirmName;
                    PhaseBox.SelectedIndex    = (int)pc.Phase;
                    StartingBalanceBox.Text   = pc.StartingBalance.ToString("0.##");
                    DailyLimitBox.Text        = pc.DailyLossLimit.ToString("0.##");
                    MaxLimitBox.Text          = pc.MaxLossLimit.ToString("0.##");
                    DrawdownTypeBox.SelectedIndex = (int)pc.DrawdownType;
                    ProfitTargetBox.Text      = pc.ProfitTarget?.ToString("0.##") ?? "";
                    CloseAllOnTargetHitBox.IsChecked = pc.CloseAllOnTargetHit;
                    MinDaysBox.Text           = pc.MinTradingDays?.ToString() ?? "";
                    MaxLossPerTradeBox.Text   = pc.MaxLossPerTradePercent?.ToString("0.##") ?? "";
                    AutoPauseEnabled.IsChecked = pc.AutoPauseAtPercent is not null;
                    AutoPausePercentBox.Text   = pc.AutoPauseAtPercent?.ToString() ?? "80";
                    AutoCloseEnabled.IsChecked = pc.AutoCloseAtPercent is not null;
                    AutoClosePercentBox.Text   = pc.AutoCloseAtPercent?.ToString() ?? "95";
                }

                // In edit mode the IP is already known — collapse the detector banner.
                _detectedSuccessfully = true;
                DetectIcon.Text = "✓";
                DetectTitle.Text = "Editing existing account";
                DetectBody.Text = "Leave the password blank to keep the current password. Other fields can be changed freely.";
                DetectBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("BrandBrush");
                ManualHelpButton.Visibility = Visibility.Collapsed;
                return;
            }

            _detectStartedAt = DateTime.UtcNow;
            RunDetect();
            _detectTimer.Start();
        };
        Closed += (_, _) => _detectTimer.Stop();
    }

    private void RunDetect()
    {
        // If user already typed an IP themselves, stop polling.
        if (_detectedSuccessfully) return;
        if (!string.IsNullOrEmpty(HostBox.Text?.Trim())
            && !HostBox.Text!.Equals(_lastAutoFilledHost, StringComparison.Ordinal))
        {
            // User edited the field — back off so we don't overwrite their input.
            _detectTimer.Stop();
            return;
        }

        var result = _detector.Detect();
        if (result.Success && !string.IsNullOrEmpty(result.Ip))
        {
            HostBox.Text = result.Ip;
            _lastAutoFilledHost = result.Ip;
            if (result.Port is int p && p != 443) PortBox.Text = p.ToString();

            DetectIcon.Text = "✓";
            DetectTitle.Text = "Broker IP detected";
            DetectBody.Text = $"Found connection to {result.Ip}. You can now fill in the rest of the account details.";
            DetectBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("StatusOkBrush");
            ManualHelpButton.Visibility = Visibility.Collapsed;
            _detectedSuccessfully = true;
            _detectTimer.Stop();
            return;
        }

        // Still searching — keep the message helpful but distinct between
        // "no MT5 running yet" and "we tried and gave up".
        var elapsed = DateTime.UtcNow - _detectStartedAt;
        if (elapsed > DetectTimeout)
        {
            // Give up; show manual instructions button.
            DetectIcon.Text = "⚠";
            DetectTitle.Text = "Couldn't auto-detect the IP";
            DetectBody.Text = result.Reason
                ?? "We couldn't find an active MetaTrader 5 connection. Please enter the IP manually.";
            DetectBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("StatusWarnBrush");
            ManualHelpButton.Visibility = Visibility.Visible;
            _detectTimer.Stop();
        }
        else
        {
            // Update the body with the latest reason while we keep trying.
            DetectIcon.Text = "⏳";
            DetectTitle.Text = "Detecting broker IP from MetaTrader 5…";
            DetectBody.Text = result.Reason
                ?? "Please open MetaTrader 5, log into the account you want to add, and keep the window active. Close any other MT5 windows.";
        }
    }

    private void OnManualHelpClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Manual IP lookup:\n\n" +
            "1. Open MetaTrader 5 and log into the account you want to add.\n" +
            "2. Press Win+R, type 'resmon', press Enter.\n" +
            "3. Click the Network tab.\n" +
            "4. Expand 'TCP Connections'.\n" +
            "5. Find the row for terminal64.exe.\n" +
            "6. Copy the Remote Address (an IP like 47.91.105.29).\n" +
            "7. Paste it into the Host field below.",
            "Manual IP lookup",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// Refresh button next to the detect banner — re-runs the terminal64.exe
    /// IP scan. Useful when:
    ///   - The user just opened MT5 after this dialog appeared
    ///   - They switched to a different terminal instance
    ///   - The previous detection failed and they want to retry without
    ///     closing and reopening the dialog
    /// We reset detection state and immediately fire one synchronous scan,
    /// then re-start the timer in case the first scan misses (MT5 just
    /// starting up sometimes needs a couple of seconds to establish TCP).
    /// </summary>
    private void OnRefreshDetectClick(object sender, RoutedEventArgs e)
    {
        _detectedSuccessfully = false;
        _lastAutoFilledHost = null;
        _detectStartedAt = DateTime.UtcNow;

        DetectIcon.Text = "⏳";
        DetectTitle.Text = "Re-scanning MetaTrader 5…";
        DetectBody.Text = "Looking for the running terminal64.exe and its current broker connection. Please wait a moment.";
        DetectBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("BrandBrush");
        ManualHelpButton.Visibility = Visibility.Collapsed;

        // If the host box is currently empty or matches our last auto-fill,
        // clear it so RunDetect doesn't bail on the "user edited" guard.
        if (string.IsNullOrWhiteSpace(HostBox.Text)
            || HostBox.Text.Equals(_lastAutoFilledHost, StringComparison.Ordinal))
        {
            HostBox.Text = "";
        }

        RunDetect();
        if (!_detectedSuccessfully) _detectTimer.Start();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    // -----------------------------------------------------------
    // ACCOUNT KIND TOGGLE
    // Shows or hides the prop firm section based on the radio
    // selection. Personal = section hidden. Prop = section shown.
    // -----------------------------------------------------------

    private void OnAccountKindChanged(object sender, RoutedEventArgs e)
    {
        // Loaded fires Checked events before our XAML elements exist on
        // first construction; guard against null references.
        if (PropSection is null) return;

        var kind = GetSelectedAccountKind();
        PropSection.Visibility = kind == AccountKind.Personal
            ? Visibility.Collapsed
            : Visibility.Visible;

        // On Funded, profit target rarely applies — hide the field to
        // keep the form short. Trader can still uncheck Funded if needed.
        if (ProfitTargetSection is not null)
        {
            ProfitTargetSection.Visibility = kind == AccountKind.PropFunded
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void OnPhaseChanged(object sender, SelectionChangedEventArgs e)
    {
        // When phase is Funded, hide profit target. Mirror logic from kind
        // toggle. This lets the trader switch phases without leaving stale
        // values visible.
        if (ProfitTargetSection is null) return;
        if (PhaseBox.SelectedIndex == 2)  // 0=Phase1, 1=Phase2, 2=Funded
            ProfitTargetSection.Visibility = Visibility.Collapsed;
        else
            ProfitTargetSection.Visibility = Visibility.Visible;
    }

    private AccountKind GetSelectedAccountKind()
    {
        if (KindChallengeRadio?.IsChecked == true) return AccountKind.PropChallenge;
        if (KindFundedRadio?.IsChecked == true)    return AccountKind.PropFunded;
        return AccountKind.Personal;
    }

    private PropFirmPhase GetSelectedPhase()
    {
        return PhaseBox.SelectedIndex switch
        {
            1 => PropFirmPhase.Phase2,
            2 => PropFirmPhase.Funded,
            _ => PropFirmPhase.Phase1,
        };
    }

    private DrawdownType GetSelectedDrawdownType()
    {
        return DrawdownTypeBox.SelectedIndex switch
        {
            1 => DrawdownType.Trailing,
            2 => DrawdownType.HighWaterMark,
            _ => DrawdownType.StaticBalance,
        };
    }

    // -----------------------------------------------------------
    // PERCENTAGE HELPER BUTTONS
    // The "4% / 5% / 6%" buttons next to the daily limit field
    // and the "5% / 10% / 12%" next to the max limit. They read
    // the starting balance, multiply, and write the result into
    // the corresponding box. Generic — not preset-y.
    // -----------------------------------------------------------

    private void OnDailyPercentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string pctStr) return;
        if (!int.TryParse(pctStr, out var pct)) return;
        if (!decimal.TryParse(StartingBalanceBox.Text, out var bal) || bal <= 0)
        {
            ErrorText.Text = "Enter a starting balance first, then click a % helper.";
            return;
        }
        ErrorText.Text = "";
        var amount = Math.Round(bal * (decimal)pct / 100m, 2);
        DailyLimitBox.Text = amount.ToString("0.##");
    }

    private void OnMaxPercentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string pctStr) return;
        if (!int.TryParse(pctStr, out var pct)) return;
        if (!decimal.TryParse(StartingBalanceBox.Text, out var bal) || bal <= 0)
        {
            ErrorText.Text = "Enter a starting balance first, then click a % helper.";
            return;
        }
        ErrorText.Text = "";
        var amount = Math.Round(bal * (decimal)pct / 100m, 2);
        MaxLimitBox.Text = amount.ToString("0.##");
    }

    private void OnStartingBalanceChanged(object sender, TextChangedEventArgs e)
    {
        // Clear any stale "enter a balance first" error when the trader
        // starts typing. Doesn't auto-update the limit fields though —
        // we don't want to overwrite their custom-entered numbers.
        if (!string.IsNullOrWhiteSpace(StartingBalanceBox.Text))
            ErrorText.Text = "";
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        var name = (NameBox.Text ?? "").Trim();
        var loginText = (LoginBox.Text ?? "").Trim();
        var pwd = PasswordBox.Password ?? "";
        var host = (HostBox.Text ?? "").Trim();
        var portText = (PortBox.Text ?? "").Trim();
        var roleStr = (RoleBox.SelectedItem as ComboBoxItem)?.Content as string ?? "Master";
        var brokerLabel = (BrokerBox.Text ?? "").Trim();
        var prefix = (PrefixBox.Text ?? "").Trim();
        var suffix = (SuffixBox.Text ?? "").Trim();

        if (string.IsNullOrEmpty(name))      { ShowError("Display name is required."); return; }
        if (!ulong.TryParse(loginText, out var login) || login == 0)
                                              { ShowError("Login must be a positive number."); return; }
        // In edit mode, blank password = keep existing.
        if (_editing is null && string.IsNullOrEmpty(pwd))
                                              { ShowError("Password is required."); return; }
        if (string.IsNullOrEmpty(host))      { ShowError("Host (broker IP) is required."); return; }
        if (!int.TryParse(portText, out var port) || port <= 0 || port > 65535)
                                              { ShowError("Port must be 1-65535."); return; }
        if (!Enum.TryParse<AccountRole>(roleStr, true, out var role))
                                              { ShowError("Pick Master or Slave."); return; }

        // -------- Prop firm validation --------
        // Only run if the trader picked Challenge or Funded. Personal
        // accounts skip all of this and PropConfig stays null.
        PropFirmConfig? propConfig = null;
        var kind = GetSelectedAccountKind();
        if (kind != AccountKind.Personal)
        {
            propConfig = TryBuildPropConfig(out var propError);
            if (propConfig is null)
            {
                ShowError(propError);
                return;
            }
        }

        var passwordToUse = string.IsNullOrEmpty(pwd) && _editing is not null
            ? _editing.Password
            : pwd;

        Result = new Account
        {
            // Preserve the original Id when editing so persistence updates the existing row
            // instead of creating a new one.
            Id = _editing?.Id ?? Guid.NewGuid(),
            CreatedAt = _editing?.CreatedAt ?? DateTime.UtcNow,
            DisplayName = name,
            Login = login,
            Password = passwordToUse,
            Server = host,
            Port = port,
            Role = role,
            BrokerLabel = string.IsNullOrEmpty(brokerLabel) ? null : brokerLabel,
            SymbolPrefix = prefix,
            SymbolSuffix = suffix,
            Enabled = true,
            Kind = kind,
            PropConfig = propConfig,
        };
        DialogResult = true;
    }

    /// <summary>
    /// Validate and build a PropFirmConfig from the prop section fields.
    /// Returns null + sets error message if validation fails.
    /// </summary>
    private PropFirmConfig? TryBuildPropConfig(out string error)
    {
        error = "";

        if (!decimal.TryParse(StartingBalanceBox.Text, out var balance) || balance <= 0)
        {
            error = "Prop accounts need a starting balance (e.g. 100000).";
            return null;
        }
        if (!decimal.TryParse(DailyLimitBox.Text, out var dailyLimit) || dailyLimit <= 0)
        {
            error = "Daily loss limit must be a positive dollar amount.";
            return null;
        }
        if (!decimal.TryParse(MaxLimitBox.Text, out var maxLimit) || maxLimit <= 0)
        {
            error = "Maximum total loss must be a positive dollar amount.";
            return null;
        }
        if (dailyLimit > maxLimit)
        {
            // Sanity: daily limit should be less than overall limit. If the
            // trader entered something nonsensical (e.g. daily 10k, max 5k)
            // it's likely a typo — flag it but let them re-enter.
            error = "Daily limit looks larger than the maximum total loss — please double-check.";
            return null;
        }

        decimal? profitTarget = null;
        if (!string.IsNullOrWhiteSpace(ProfitTargetBox.Text))
        {
            if (!decimal.TryParse(ProfitTargetBox.Text, out var pt) || pt < 0)
            {
                error = "Profit target must be a positive dollar amount, or leave it blank.";
                return null;
            }
            profitTarget = pt;
        }

        int? minDays = null;
        if (!string.IsNullOrWhiteSpace(MinDaysBox.Text))
        {
            if (!int.TryParse(MinDaysBox.Text, out var md) || md < 0)
            {
                error = "Minimum trading days must be a whole number, or leave blank.";
                return null;
            }
            minDays = md;
        }

        decimal? maxLossPerTradePct = null;
        if (!string.IsNullOrWhiteSpace(MaxLossPerTradeBox.Text))
        {
            if (!decimal.TryParse(MaxLossPerTradeBox.Text,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var mlpt) || mlpt <= 0 || mlpt > 100)
            {
                error = "Max loss per trade % must be between 0 and 100, or leave blank.";
                return null;
            }
            maxLossPerTradePct = mlpt;
        }

        // Safety automations — opt-in. Read percent values only if the
        // corresponding checkbox is ticked.
        int? autoPausePct = null;
        if (AutoPauseEnabled.IsChecked == true)
        {
            if (!int.TryParse(AutoPausePercentBox.Text, out var ap) || ap < 1 || ap > 99)
            {
                error = "Auto-pause percent must be between 1 and 99.";
                return null;
            }
            autoPausePct = ap;
        }
        int? autoClosePct = null;
        if (AutoCloseEnabled.IsChecked == true)
        {
            if (!int.TryParse(AutoClosePercentBox.Text, out var ac) || ac < 1 || ac > 99)
            {
                error = "Auto-close percent must be between 1 and 99.";
                return null;
            }
            autoClosePct = ac;
        }
        if (autoPausePct is not null && autoClosePct is not null && autoClosePct < autoPausePct)
        {
            error = "Auto-close should fire AFTER auto-pause (set its percent higher).";
            return null;
        }

        return new PropFirmConfig
        {
            FirmName = (FirmNameBox.Text ?? "").Trim(),
            Phase = GetSelectedPhase(),
            StartingBalance = balance,
            DailyLossLimit = dailyLimit,
            MaxLossLimit = maxLimit,
            DrawdownType = GetSelectedDrawdownType(),
            DailyResetUtc = null,           // auto-detect from broker server time
            ProfitTarget = profitTarget,
            CloseAllOnTargetHit = CloseAllOnTargetHitBox.IsChecked == true,
            MinTradingDays = minDays,
            MaxLossPerTradePercent = maxLossPerTradePct,
            AutoPauseAtPercent = autoPausePct,
            AutoCloseAtPercent = autoClosePct,
        };
    }

    private void ShowError(string msg) => ErrorText.Text = msg;
}
