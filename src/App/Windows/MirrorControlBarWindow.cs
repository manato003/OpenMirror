using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace IPhoneMirror.App.Windows;

/// <summary>What the control bar can do to the mirror window it belongs to.</summary>
internal interface IMirrorControlTarget
{
    nint Handle { get; }
    bool IsFullScreen { get; }
    bool IsTopMost { get; }
    bool? IsAudioEnabled { get; }
    // Reverse control hides the cursor and sends every click to the device;
    // the bar must not appear over a pointer the user cannot see.
    bool IsPointerCaptured { get; }
    bool CanCaptureScreenshot { get; }
    void ToggleTopMost();
    void RotateClockwise();
    void ToggleFullScreen();
    void ToggleAudio();
    void ShowMenu();
    void CloseMirror();
    string CaptureScreenshot();
}

// The mirror window is a borderless native DirectComposition surface, so WPF
// cannot draw on it (airspace). Like FullScreenExitOverlayWindow, the bar is
// an owned, non-activating HWND kept at the top centre of the mirror. It grows
// down from the top edge when the pointer moves over the mirror, the way the
// iPhone's Dynamic Island expands, and folds away when the pointer rests.
internal sealed class MirrorControlBarWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const double TopMargin = 10;
    private const double FullScreenTopMargin = 20;
    private const double CompactWidthAllowance = 24;
    private static readonly TimeSpan IdleHideDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IntroDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(4);

    private readonly IMirrorControlTarget _target;
    private readonly DispatcherTimer _pointerTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly StackPanel _root;
    private readonly Border _capsule;
    private readonly TranslateTransform _slide = new();
    private readonly Button _pinButton;
    private readonly Button _rotateButton;
    private readonly Button _screenshotButton;
    private readonly Button _fullScreenButton;
    private readonly Button _audioButton;
    private readonly Button _moreButton;
    private readonly Button _closeButton;
    private readonly Border _notice;
    private readonly TextBlock _noticeText;
    private string? _lastScreenshot;
    private CursorPoint _lastCursor;
    private DateTime _hideAfter = DateTime.MinValue;
    private DateTime _introUntil = DateTime.MinValue;
    private bool _shown;
    private bool _closing;

    private MirrorControlBarWindow(IMirrorControlTarget target)
    {
        _target = target;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = 0;
        SetResourceReference(FontFamilyProperty, "AppFontFamily");

        _pinButton = CreateButton(() => _target.ToggleTopMost());
        _rotateButton = CreateButton(() => _target.RotateClockwise());
        _screenshotButton = CreateButton(CaptureScreenshot);
        _fullScreenButton = CreateButton(() => _target.ToggleFullScreen());
        _audioButton = CreateButton(() => _target.ToggleAudio());
        _moreButton = CreateButton(() => _target.ShowMenu());
        _closeButton = CreateButton(() => _target.CloseMirror());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_pinButton);
        buttons.Children.Add(_rotateButton);
        buttons.Children.Add(_screenshotButton);
        buttons.Children.Add(_fullScreenButton);
        buttons.Children.Add(_audioButton);
        buttons.Children.Add(_moreButton);
        var divider = new Border { Width = 1, Height = 20, Margin = new Thickness(4, 0, 4, 0) };
        divider.SetResourceReference(Border.BackgroundProperty, "MediaPlayerTrackBrush");
        buttons.Children.Add(divider);
        buttons.Children.Add(_closeButton);

        _capsule = new Border
        {
            Child = buttons,
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _capsule.SetResourceReference(Border.BackgroundProperty, "MediaOverlayBrush");

        _noticeText = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _noticeText.SetResourceReference(TextBlock.ForegroundProperty, "MediaOverlayTextBrush");
        _noticeText.SetResourceReference(TextBlock.FontSizeProperty, "SecondaryFontSize");
        _notice = new Border
        {
            Child = _noticeText,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
        };
        _notice.SetResourceReference(Border.BackgroundProperty, "MediaOverlayBrush");
        _notice.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RevealLastScreenshot();
        };

        _root = new StackPanel { RenderTransform = _slide };
        _root.Children.Add(_capsule);
        _root.Children.Add(_notice);
        Content = _root;

        _pointerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _pointerTimer.Tick += (_, _) => UpdateVisibility();
        _noticeTimer = new DispatcherTimer { Interval = NoticeDuration };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _notice.Visibility = Visibility.Collapsed;
        };
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _pointerTimer.Stop();
            _noticeTimer.Stop();
            LocalizationService.LanguageChanged -= OnLanguageChanged;
        };
        Refresh();
    }

    internal static MirrorControlBarWindow Attach(IMirrorControlTarget target)
    {
        var bar = new MirrorControlBarWindow(target);
        var helper = new WindowInteropHelper(bar) { Owner = target.Handle };
        bar.SourceInitialized += (_, _) =>
        {
            var style = GetWindowLongPtrW(helper.Handle, GwlExStyle).ToInt64();
            _ = SetWindowLongPtrW(helper.Handle, GwlExStyle, (nint)(style | WsExNoActivate));
        };
        _ = GetCursorPos(out bar._lastCursor);
        bar._pointerTimer.Start();
        // Show once when the mirror opens, wherever the pointer is, so the
        // controls are discoverable; then fold away like any other reveal.
        bar.Reveal(IntroDuration);
        return bar;
    }

    /// <summary>Shows the bar now, as if the pointer had just moved over the mirror.</summary>
    internal void Reveal(TimeSpan? duration = null)
    {
        if (_closing) return;
        _introUntil = DateTime.UtcNow + (duration ?? IdleHideDelay);
        UpdateVisibility();
    }

    internal bool IsShown => _shown;

    internal void Refresh()
    {
        SetButton(_pinButton, _target.IsTopMost ? SymbolRegular.PinOff20 : SymbolRegular.Pin20,
            _target.IsTopMost ? "IndependentWindowUnpin" : "IndependentWindowPin", _target.IsTopMost);
        SetButton(_rotateButton, SymbolRegular.ArrowRotateClockwise20, "IndependentWindowRotateRight");
        SetButton(_screenshotButton, SymbolRegular.Screenshot20, "Screenshot");
        _screenshotButton.IsEnabled = _target.CanCaptureScreenshot;
        SetButton(_fullScreenButton,
            _target.IsFullScreen ? SymbolRegular.FullScreenMinimize20 : SymbolRegular.FullScreenMaximize20,
            _target.IsFullScreen ? "MirrorBarExitFullScreen" : "IndependentWindowEnterFullScreen");
        var audio = _target.IsAudioEnabled;
        _audioButton.Visibility = audio is null ? Visibility.Collapsed : Visibility.Visible;
        SetButton(_audioButton, audio == false ? SymbolRegular.SpeakerMute20 : SymbolRegular.Speaker220,
            audio == false ? "IndependentWindowUnmuteThis" : "IndependentWindowMuteThis", audio == false);
        SetButton(_moreButton, SymbolRegular.MoreHorizontal20, "MirrorBarMore");
        SetButton(_closeButton, SymbolRegular.Dismiss20, "MirrorBarStop");
    }

    internal void CloseBar()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private Button CreateButton(Action action)
    {
        var button = new Button
        {
            Width = 36,
            Height = 36,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = false,
            Template = CreateButtonTemplate(),
        };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception error)
            {
                DiagnosticLogger.Exception("mirror_bar", "action_failed", error);
                ShowNotice(LocalizationService.Get("MirrorBarActionFailed"));
            }
            Refresh();
            _hideAfter = DateTime.UtcNow + IdleHideDelay;
        };
        return button;
    }

    private static ControlTemplate CreateButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var surface = new FrameworkElementFactory(typeof(Border), "Surface");
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(content);
        template.VisualTree = surface;

        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            new DynamicResourceExtension("MediaPlayerControlHoverBrush"), "Surface"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty,
            new DynamicResourceExtension("MediaPlayerControlPressedBrush"), "Surface"));
        template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.4));
        template.Triggers.Add(disabled);
        return template;
    }

    private static void SetButton(Button button, SymbolRegular symbol, string labelKey,
        bool active = false)
    {
        var label = LocalizationService.Get(labelKey);
        var icon = new SymbolIcon { Symbol = symbol, FontSize = 18, Filled = active };
        icon.SetResourceReference(SymbolIcon.ForegroundProperty, "MediaOverlayTextBrush");
        button.Content = icon;
        button.ToolTip = label;
        AutomationProperties.SetName(button, label);
    }

    private void CaptureScreenshot()
    {
        var path = _target.CaptureScreenshot();
        _lastScreenshot = path;
        ShowNotice(LocalizationService.Get("MirrorBarScreenshotSaved"));
    }

    private void ShowNotice(string text)
    {
        _noticeText.Text = text;
        _notice.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
        _hideAfter = DateTime.UtcNow + NoticeDuration;
    }

    private void RevealLastScreenshot()
    {
        if (_lastScreenshot is null || !File.Exists(_lastScreenshot)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                "/select," + CompactLaunchOptions.QuoteArgument(_lastScreenshot))
            {
                UseShellExecute = false,
            })?.Dispose();
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("mirror_bar", "reveal_screenshot_failed", error);
        }
    }

    private void UpdateVisibility()
    {
        if (_closing) return;
        var owner = _target.Handle;
        if (owner == 0 || !IsWindowVisible(owner) || IsIconic(owner) ||
            _target.IsPointerCaptured || !GetWindowRect(owner, out var rect))
        {
            SetShown(false);
            return;
        }
        if (!GetCursorPos(out var cursor)) return;
        var overOwner = cursor.X >= rect.Left && cursor.X < rect.Right &&
            cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
        if (overOwner && (cursor.X != _lastCursor.X || cursor.Y != _lastCursor.Y))
            _hideAfter = DateTime.UtcNow + IdleHideDelay;
        _lastCursor = cursor;
        // Stay while the pointer rests on the bar itself, or while the
        // window's menu is open from the bar.
        var now = DateTime.UtcNow;
        var visible = _root.IsMouseOver || now < _introUntil ||
            (overOwner || _notice.IsVisible) && now < _hideAfter;
        if (visible) UpdateBounds(rect);
        SetShown(visible);
    }

    private void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        if (shown)
        {
            Refresh();
            if (!IsVisible) Show();
        }
        // Reduced motion keeps the fade but drops the movement.
        var motion = SystemParameters.ClientAreaAnimation;
        var duration = TimeSpan.FromMilliseconds(shown ? 160 : 120);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(shown ? 1 : 0, duration) { EasingFunction = ease };
        if (!shown)
            fade.Completed += (_, _) =>
            {
                if (!_shown && !_closing) Hide();
            };
        BeginAnimation(OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.YProperty, motion
            ? new DoubleAnimation(shown ? -8 : 0, shown ? 0 : -6, duration) { EasingFunction = ease }
            : null);
    }

    private void UpdateBounds(WindowRect rect)
    {
        var dpi = GetDpiForWindow(_target.Handle);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var ownerWidth = (rect.Right - rect.Left) / scale;
        // A narrow phone window keeps the essentials; the rest stay in "…".
        var compact = ownerWidth < 7 * 36 + 8 + 9 + CompactWidthAllowance;
        var secondary = compact ? Visibility.Collapsed : Visibility.Visible;
        _rotateButton.Visibility = secondary;
        _screenshotButton.Visibility = secondary;
        if (_target.IsAudioEnabled is not null) _audioButton.Visibility = secondary;
        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : _root.DesiredSize.Width;
        Left = rect.Left / scale + (ownerWidth - width) / 2;
        Top = rect.Top / scale + (_target.IsFullScreen ? FullScreenTopMargin : TopMargin);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Refresh();

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
}
