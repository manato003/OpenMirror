using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace IPhoneMirror.App.Windows;

// The full-screen preview forwards every mouse event to the device, so the
// main window has no clickable chrome. Like ProtectedContentOverlayWindow,
// use an owned HWND above the native D3D airspace. It appears while the
// cursor moves and never takes activation away from the main window.
internal sealed class FullScreenExitOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const double TopMargin = 24;
    private static readonly TimeSpan IdleHideDelay = TimeSpan.FromSeconds(2.5);
    private readonly nint _owner;
    private readonly Action _exit;
    private readonly DispatcherTimer _pointerTimer;
    private readonly Border _surface;
    private TextBlock _labelText = null!;
    private CursorPoint _lastCursor;
    private DateTime _hideAfter = DateTime.MinValue;

    private FullScreenExitOverlayWindow(nint owner, Action exit)
    {
        _owner = owner;
        _exit = exit;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _surface = BuildContent();
        Content = _surface;
        _pointerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _pointerTimer.Tick += (_, _) => UpdateVisibility();
        Closed += (_, _) => _pointerTimer.Stop();
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;
    }

    internal static FullScreenExitOverlayWindow ShowFor(nint owner, Action exit)
    {
        var overlay = new FullScreenExitOverlayWindow(owner, exit);
        var helper = new WindowInteropHelper(overlay) { Owner = owner };
        overlay.SourceInitialized += (_, _) =>
        {
            var style = GetWindowLongPtrW(helper.Handle, GwlExStyle).ToInt64();
            _ = SetWindowLongPtrW(helper.Handle, GwlExStyle, (nint)(style | WsExNoActivate));
        };
        _ = GetCursorPos(out overlay._lastCursor);
        // Show once on entry so the exit path is discoverable, then hide
        // after the same idle delay as later pointer movement.
        overlay._hideAfter = DateTime.UtcNow + IdleHideDelay;
        overlay.Show();
        overlay.UpdateBounds();
        overlay._pointerTimer.Start();
        return overlay;
    }

    private Border BuildContent()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new SymbolIcon
        {
            Symbol = SymbolRegular.FullScreenMinimize20,
            FontSize = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(SymbolIcon.ForegroundProperty, "PreviewTextBrush");
        panel.Children.Add(icon);
        _labelText = new TextBlock
        {
            Text = LabelText(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _labelText.SetResourceReference(TextBlock.ForegroundProperty, "PreviewTextBrush");
        _labelText.SetResourceReference(TextBlock.FontSizeProperty, "BodyFontSize");
        panel.Children.Add(_labelText);

        var surface = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 8, 16, 8),
            Cursor = Cursors.Hand,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "PreviewPanelAltBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "PreviewBorderBrush");
        surface.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _exit();
        };
        System.Windows.Automation.AutomationProperties.SetName(surface, _labelText.Text);
        return surface;
    }

    private static string LabelText() =>
        LocalizationService.Get("IndependentWindowExitFullScreen") + " (Esc)";

    private void UpdateVisibility()
    {
        if (_owner == 0 || !IsWindowVisible(_owner) || IsIconic(_owner))
        {
            Hide();
            return;
        }
        if (GetCursorPos(out var cursor) &&
            (cursor.X != _lastCursor.X || cursor.Y != _lastCursor.Y))
        {
            _lastCursor = cursor;
            _hideAfter = DateTime.UtcNow + IdleHideDelay;
        }
        // Keep the button while the pointer rests on it.
        var visible = _surface.IsMouseOver || DateTime.UtcNow < _hideAfter;
        if (visible && !IsVisible) Show();
        else if (!visible && IsVisible) Hide();
        if (visible) UpdateBounds();
    }

    private void UpdateBounds()
    {
        if (!GetWindowRect(_owner, out var rect)) return;
        var dpi = GetDpiForWindow(_owner);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var width = ActualWidth > 0 ? ActualWidth : _surface.DesiredSize.Width;
        Left = rect.Left / scale + ((rect.Right - rect.Left) / scale - width) / 2;
        Top = rect.Top / scale + TopMargin;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _labelText.Text = LabelText();
        System.Windows.Automation.AutomationProperties.SetName(_surface, _labelText.Text);
    }

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
