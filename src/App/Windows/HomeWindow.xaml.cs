using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.UI.Controls;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace IPhoneMirror.App.Windows;

/// <summary>What home asks of the shell that owns the mirror windows.</summary>
internal sealed record HomeWindowActions(
    Func<DeviceViewModel, Task> ShowDevice,
    Func<DeviceViewModel, Task> StopDevice,
    Func<DeviceViewModel, bool> IsDeviceShown,
    Action OpenAllFeatures,
    Action Exit,
    Action Hidden);

/// <summary>
/// The everyday surface of the tray receiver: how to start from the phone,
/// what is mirrored now, and the few settings worth changing in place.
/// Closing it keeps the receiver running in the tray.
/// </summary>
public partial class HomeWindow : RoundedWindow
{
    // Ordered light → best so the segments read left to right like iOS.
    private static readonly string[] QualityProfileIds = ["720p", "1080p", "maximum"];

    private readonly MainViewModel _viewModel;
    private readonly HomeWindowActions _actions;
    private readonly ObservableCollection<HomeDeviceRow> _rows = [];
    private bool _closingPermanently;
    private bool _updatingQuality;
    private bool _editingName;
    private bool _applying;
    private bool _updatingSwitch;

    internal HomeWindow(MainViewModel viewModel, HomeWindowActions actions)
    {
        _viewModel = viewModel;
        _actions = actions;
        InitializeComponent();
        DeviceRows.ItemsSource = _rows;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Devices.CollectionChanged += OnDevicesChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closing += (_, args) =>
        {
            if (_closingPermanently) return;
            args.Cancel = true;
            HideToTray();
        };
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelChanged;
            viewModel.Devices.CollectionChanged -= OnDevicesChanged;
            LocalizationService.LanguageChanged -= OnLanguageChanged;
        };
        RefreshAll();
    }

    internal void ShowAndActivate()
    {
        RefreshAll();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        Activate();
    }

    internal void ClosePermanently()
    {
        _closingPermanently = true;
        Close();
    }

    internal void ReportError(string message)
    {
        FeedbackText.Text = message;
        FeedbackText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void RefreshDevices()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshDevices);
            return;
        }
        var devices = _viewModel.Devices.ToArray();
        for (var index = _rows.Count - 1; index >= 0; --index)
            if (!devices.Any(device => DeviceViewModel.UdidEquals(device.Udid, _rows[index].Device.Udid)))
                _rows.RemoveAt(index);
        foreach (var device in devices)
        {
            var row = _rows.FirstOrDefault(item => DeviceViewModel.UdidEquals(item.Device.Udid, device.Udid));
            if (row is null)
            {
                row = new HomeDeviceRow(device);
                _rows.Add(row);
            }
            else if (!ReferenceEquals(row.Device, device)) row.Device = device;
            row.Update(_actions.IsDeviceShown(device),
                _viewModel.GetDeviceSessionHandle(device.Udid) != 0);
        }
        EmptyDevices.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshAll()
    {
        RefreshStatus();
        RefreshReceiverName();
        RefreshQuality();
        RefreshDevices();
        SetLaunchAtSignInSwitch(SignInLaunch.IsEnabled);
    }

    private void SetLaunchAtSignInSwitch(bool enabled)
    {
        _updatingSwitch = true;
        try { LaunchAtSignInSwitch.IsChecked = enabled; }
        finally { _updatingSwitch = false; }
    }

    private void RefreshStatus()
    {
        var tone = _viewModel.WirelessStatusTone;
        string key;
        if (_applying) key = "HomeStatusApplying";
        else if (tone == StatusTone.Error) key = "HomeStatusError";
        else if (_viewModel.IsWirelessReceiverReady) key = "HomeStatusReady";
        else key = "HomeStatusStarting";
        StatusText.Text = LocalizationService.Get(key);
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key switch
        {
            "HomeStatusReady" => "SuccessBrush",
            "HomeStatusError" => "ErrorBrush",
            _ => "WarningBrush",
        });
        var showDetail = key == "HomeStatusError" && !string.IsNullOrWhiteSpace(_viewModel.WirelessStatus);
        StatusDetail.Text = showDetail ? _viewModel.WirelessStatus : string.Empty;
        StatusDetail.Visibility = showDetail ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshReceiverName()
    {
        if (_editingName) return;
        ReceiverNameText.Text = _viewModel.AppliedWirelessReceiverName;
    }

    private void RefreshQuality()
    {
        _updatingQuality = true;
        try
        {
            var applied = _viewModel.AppliedWirelessDisplayProfile;
            var index = Array.IndexOf(QualityProfileIds, applied.Id);
            QualityLight.IsChecked = index == 0;
            QualityStandard.IsChecked = index == 1;
            QualityBest.IsChecked = index == 2;
            QualityDetail.Text = LocalizationService.Get(index switch
            {
                0 => "HomeQualityLightDetail",
                2 => "HomeQualityBestDetail",
                1 => "HomeQualityStandardDetail",
                // A profile chosen in the full workspace (540p) has no segment.
                _ => "HomeQualityOtherDetail",
            });
            QualityLight.IsEnabled = QualityStandard.IsEnabled = QualityBest.IsEnabled = !_applying;
        }
        finally { _updatingQuality = false; }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.WirelessStatus) or
            nameof(MainViewModel.WirelessStatusTone) or nameof(MainViewModel.IsBusy))
        {
            Dispatcher.BeginInvoke(() =>
            {
                RefreshStatus();
                RefreshReceiverName();
                RefreshQuality();
                RefreshDevices();
            });
        }
    }

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshDevices();

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        foreach (var row in _rows) row.NotifyLanguageChanged();
        RefreshAll();
    });

    private async Task ApplyReceiverAsync(string name, WirelessDisplayProfile profile)
    {
        if (_applying) return;
        _applying = true;
        RefreshStatus();
        RefreshQuality();
        try { await _viewModel.ApplyReceiverSettingsAsync(name, profile); }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("home", "receiver_apply_failed", error);
            ReportError(LocalizationService.Format("StartFailedFormat", error.Message));
        }
        finally
        {
            _applying = false;
            // A cancelled confirmation leaves the applied values in place.
            RefreshStatus();
            RefreshReceiverName();
            RefreshQuality();
        }
    }

    private void OnQualityChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingQuality || sender is not RadioButton button) return;
        var index = button == QualityLight ? 0 : button == QualityBest ? 2 : 1;
        var profile = _viewModel.WirelessDisplayProfiles.FirstOrDefault(item =>
            item.Id == QualityProfileIds[index]);
        if (profile is null || ReferenceEquals(profile, _viewModel.AppliedWirelessDisplayProfile)) return;
        _ = ApplyReceiverAsync(_viewModel.AppliedWirelessReceiverName, profile);
    }

    private void OnEditNameClick(object sender, RoutedEventArgs e)
    {
        if (_editingName)
        {
            CommitName();
            return;
        }
        _editingName = true;
        ReceiverNameBox.Text = _viewModel.AppliedWirelessReceiverName;
        ReceiverNameButton.Visibility = Visibility.Collapsed;
        ReceiverNameBox.Visibility = Visibility.Visible;
        EditNameIcon.Symbol = SymbolRegular.Checkmark20;
        EditNameButton.ToolTip = LocalizationService.Get("Save");
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            ReceiverNameBox.Focus();
            ReceiverNameBox.SelectAll();
        });
    }

    private void EndNameEdit()
    {
        _editingName = false;
        ReceiverNameBox.Visibility = Visibility.Collapsed;
        ReceiverNameButton.Visibility = Visibility.Visible;
        EditNameIcon.Symbol = SymbolRegular.Edit20;
        EditNameButton.ToolTip = LocalizationService.Get("HomeRenameToolTip");
        RefreshReceiverName();
    }

    private void CommitName()
    {
        var name = WirelessReceiverConfiguration.SanitizeReceiverName(ReceiverNameBox.Text);
        EndNameEdit();
        if (string.IsNullOrWhiteSpace(name) ||
            string.Equals(name, _viewModel.AppliedWirelessReceiverName, StringComparison.Ordinal)) return;
        ReceiverNameText.Text = name;
        _ = ApplyReceiverAsync(name, _viewModel.AppliedWirelessDisplayProfile);
    }

    private void OnNameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitName();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndNameEdit();
        }
    }

    private void OnNameBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Moving to the check mark (pointer, Tab or UI Automation) commits
        // through its own Click; committing here too would reopen the editor.
        if (_editingName && !ReferenceEquals(e.NewFocus, EditNameButton) &&
            !EditNameButton.IsMouseOver) CommitName();
    }

    private async void OnDevicePrimaryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HomeDeviceRow row) return;
        await RunDeviceActionAsync(row, _actions.ShowDevice);
    }

    private async void OnDeviceStopClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HomeDeviceRow row) return;
        await RunDeviceActionAsync(row, _actions.StopDevice);
    }

    private async Task RunDeviceActionAsync(HomeDeviceRow row, Func<DeviceViewModel, Task> action)
    {
        // One action per row at a time: a second click while a window is
        // opening would start a duplicate session.
        if (!row.IsIdle) return;
        row.IsIdle = false;
        ReportError(string.Empty);
        try { await action(row.Device); }
        catch (Exception error) { ReportError(error.Message); }
        finally
        {
            row.IsIdle = true;
            RefreshDevices();
        }
    }

    // Checked/Unchecked rather than Click, so the keyboard and accessibility
    // tools change the setting exactly like a pointer does.
    private void OnLaunchAtSignInChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingSwitch) return;
        var wanted = LaunchAtSignInSwitch.IsChecked == true;
        if (!SignInLaunch.SetEnabled(wanted))
            ReportError(LocalizationService.Get("HomeLaunchAtSignInFailed"));
        SetLaunchAtSignInSwitch(SignInLaunch.IsEnabled);
    }

    private void OnAllFeaturesClick(object sender, RoutedEventArgs e) => _actions.OpenAllFeatures();
    private void OnExitClick(object sender, RoutedEventArgs e) => _actions.Exit();
    private void OnHideClick(object sender, RoutedEventArgs e) => HideToTray();
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void HideToTray()
    {
        if (_editingName) EndNameEdit();
        Hide();
        _actions.Hidden();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _editingName) return;
        e.Handled = true;
        HideToTray();
    }
}

/// <summary>One mirrored or connectable device in home's list.</summary>
public sealed class HomeDeviceRow : INotifyPropertyChanged
{
    private bool _shown;
    private bool _hasSession;
    private bool _idle = true;

    internal HomeDeviceRow(DeviceViewModel device) => Device = device;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal DeviceViewModel Device { get; set; }
    public string Name => Device.DisplayName;
    public SymbolRegular Icon => Device.IsMediaCast ? SymbolRegular.Cast20 : SymbolRegular.Phone20;
    public string Detail => Device.IsMediaCast
        ? LocalizationService.Get("HomeDeviceMediaCast")
        : LocalizationService.Format("HomeDeviceDetailFormat", Device.ModelDisplay,
            LocalizationService.Get(Device.IsWireless ? "HomeConnectionWireless" : "HomeConnectionUsb"));
    // Wired phones are listed while plugged in but mirror only when asked.
    public string PrimaryLabel => LocalizationService.Get(_shown ? "HomeBringForward"
        : _hasSession || Device.IsWireless || Device.IsMediaCast ? "HomeShowWindow" : "HomeStartUsb");
    public Visibility StopVisibility => _shown || _hasSession ? Visibility.Visible : Visibility.Collapsed;
    public bool IsIdle
    {
        get => _idle;
        set
        {
            if (_idle == value) return;
            _idle = value;
            Notify(nameof(IsIdle));
        }
    }

    internal void Update(bool shown, bool hasSession)
    {
        _shown = shown;
        _hasSession = hasSession;
        NotifyLanguageChanged();
    }

    internal void NotifyLanguageChanged()
    {
        Notify(nameof(Name));
        Notify(nameof(Icon));
        Notify(nameof(Detail));
        Notify(nameof(PrimaryLabel));
        Notify(nameof(StopVisibility));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
