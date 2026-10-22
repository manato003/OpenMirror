using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static readonly string[] HomeCultures = ["zh-CN", "zh-HK", "zh-TW", "en-US", "ja-JP"];

    private static void ApplyTestLanguage(string culture) =>
        typeof(App).Assembly.GetType("IPhoneMirror.App.Localization.LocalizationService")!
            .GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [culture, false, true]);

    private static void TestHomeInteractionAndLayout(object vm, string output)
    {
        var viewModel = (MainViewModel)vm;
        var pending = new TaskCompletionSource();
        var showCount = 0;
        var stopCount = 0;
        var allFeatures = 0;
        var actions = new HomeWindowActions(
            _ => { showCount++; return pending.Task; },
            _ => { stopCount++; return Task.CompletedTask; },
            _ => false, () => allFeatures++, () => { }, () => { });
        var home = new HomeWindow(viewModel, actions);
        try
        {
            var layoutFindings = new HashSet<string>();
            foreach (var culture in HomeCultures)
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ApplyTestLanguage(culture);
                InteractionAssert(Application.Current.TryFindResource("HomeSheetTitle") is string,
                    $"Missing home localization for {culture}.");
                ApplyTheme(typeof(App).Assembly, theme);
                home.ShowAndActivate();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(300));
                home.UpdateLayout();
                AssertSelfDrawnWindowCorners(home);
                AssertVisibleButtonsFit(home);
                var status = (TextBlock)home.FindName("StatusText");
                InteractionAssert(!string.IsNullOrWhiteSpace(status.Text), $"Home status is empty for {culture}.");
                SaveWindowRender(home, Path.Combine(output, $"{culture}-{theme}-home.png"));
                ExerciseWorkAreaSizes(home, $"home/{culture}/{theme}", layoutFindings);
            }
            InteractionAssert(layoutFindings.Count == 0, string.Join(Environment.NewLine, layoutFindings));

            // A plugged-in USB phone is listed with an explicit start; a click
            // while it is starting must not start a second session.
            ApplyTestLanguage("ja-JP");
            var device = TrayTestDevice("home-test-1", "Ray's iPhone — Long device name for layout validation");
            TrayDevices(vm).Clear();
            TrayDevices(vm).Add(device);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
            var rows = (ItemsControl)home.FindName("DeviceRows");
            InteractionAssert(rows.Items.Count == 1 &&
                ((FrameworkElement)home.FindName("EmptyDevices")).Visibility == Visibility.Collapsed,
                "Home did not list the connected device.");
            var row = (HomeDeviceRow)rows.Items[0]!;
            InteractionAssert(row.PrimaryLabel == (string)Application.Current.FindResource("HomeStartUsb") &&
                row.StopVisibility == Visibility.Collapsed, "A USB phone without a session must offer to start only.");
            home.UpdateLayout();
            AssertVisibleButtonsFit(home);
            SaveWindowRender(home, Path.Combine(output, "home-with-device.png"));
            var primary = Visuals(home).OfType<Button>().Single(button =>
                button.DataContext == row && button.Content is string);
            primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(showCount == 1 && !row.IsIdle, "Rapid clicks started duplicate projections.");
            pending.SetException(new InvalidOperationException("Expected connection failure for regression test."));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            var feedback = (TextBlock)home.FindName("FeedbackText");
            InteractionAssert(row.IsIdle && feedback.IsVisible && feedback.Text.Contains("Expected connection failure"),
                "Projection failure did not restore the row and display feedback.");
            pending = new TaskCompletionSource();
            primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pending.SetResult();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            InteractionAssert(row.IsIdle && !feedback.IsVisible, "A successful start left stale feedback.");

            KeyboardCall(home, "OnAllFeaturesClick", home, new RoutedEventArgs());
            InteractionAssert(allFeatures == 1, "All features did not reach the shell.");

            TrayDevices(vm).Clear();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            InteractionAssert(rows.Items.Count == 0 &&
                ((FrameworkElement)home.FindName("EmptyDevices")).Visibility == Visibility.Visible,
                "Removing the device did not restore the empty state.");
            InteractionAssert(stopCount == 0, "Listing devices must not stop anything.");
        }
        finally { home.ClosePermanently(); }
    }

    private static int RunMirrorControlBarTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", KeyboardTestMembers)!.SetValue(app, true);
        app.InitializeComponent();
        try
        {
            foreach (var culture in HomeCultures)
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ApplyTestLanguage(culture);
                ApplyTheme(typeof(App).Assembly, theme);
                foreach (var width in new[] { 420.0, 260.0 })
                    TestMirrorControlBar(output, $"{culture}-{theme}-{width:0}", width);
            }
            Console.WriteLine("Mirror control bar tests passed: reveal, compact width, pin, full screen, audio, screenshot notice and failure, close, 5 languages × 2 themes.");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void TestMirrorControlBar(string output, string name, double width)
    {
        var owner = new Window
        {
            Style = new Style(typeof(Window)), WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Width = width, Height = 860, Left = 40, Top = 40, Background = Brushes.Black, ShowInTaskbar = false,
        };
        owner.Show();
        var target = new FakeMirrorTarget(new WindowInteropHelper(owner).Handle);
        var bar = MirrorControlBarWindow.Attach(target);
        try
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(300));
            InteractionAssert(bar.IsShown && bar.IsVisible, $"{name}: the bar did not appear when the mirror opened.");
            var buttons = Visuals(bar).OfType<Button>().ToDictionary(button =>
                System.Windows.Automation.AutomationProperties.GetName(button));
            string Label(string key) => (string)Application.Current.FindResource(key);
            // Matches the bar's own threshold: seven 36-pixel buttons, divider and padding.
            var compact = width < 7 * 36 + 8 + 9 + 24;
            InteractionAssert(buttons[Label("IndependentWindowRotateRight")].IsVisible == !compact &&
                buttons[Label("Screenshot")].IsVisible == !compact,
                $"{name}: secondary buttons did not follow the window width.");
            InteractionAssert(bar.ActualWidth <= width, $"{name}: the bar ({bar.ActualWidth:0}) is wider than the mirror.");
            SaveWindowRender(bar, Path.Combine(output, $"{name}-bar.png"));

            buttons[Label("IndependentWindowPin")].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(target.TopMost && Visuals(bar).OfType<Button>().Any(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == Label("IndependentWindowUnpin")),
                $"{name}: pinning did not toggle and relabel.");
            Find(bar, Label("IndependentWindowEnterFullScreen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(target.FullScreen && Find(bar, Label("MirrorBarExitFullScreen")) is not null,
                $"{name}: full screen did not toggle and relabel.");
            Find(bar, Label("IndependentWindowMuteThis")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(target.Audio == false && Find(bar, Label("IndependentWindowUnmuteThis")) is not null,
                $"{name}: muting did not toggle and relabel.");
            if (!compact)
            {
                Find(bar, Label("Screenshot")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                bar.UpdateLayout();
                InteractionAssert(Visuals(bar).OfType<TextBlock>().Any(text =>
                    text.IsVisible && text.Text == Label("MirrorBarScreenshotSaved")),
                    $"{name}: the screenshot notice did not appear.");
                SaveWindowRender(bar, Path.Combine(output, $"{name}-bar-notice.png"));
                target.FailScreenshot = true;
                Find(bar, Label("Screenshot")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                bar.UpdateLayout();
                InteractionAssert(Visuals(bar).OfType<TextBlock>().Any(text =>
                    text.IsVisible && text.Text == Label("MirrorBarActionFailed")),
                    $"{name}: a failed screenshot was not reported.");
            }
            target.Audio = null;
            bar.Refresh();
            bar.UpdateLayout();
            InteractionAssert(!Find(bar, Label("IndependentWindowMuteThis"))?.IsVisible ?? true,
                $"{name}: the audio button stayed for a window without audio control.");
            Find(bar, Label("MirrorBarStop")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(target.Closed, $"{name}: stop did not close the mirror.");
        }
        finally
        {
            bar.CloseBar();
            owner.Close();
        }
    }

    private static Button Find(Window bar, string label) => Visuals(bar).OfType<Button>().Single(button =>
        System.Windows.Automation.AutomationProperties.GetName(button) == label);

    private sealed class FakeMirrorTarget(nint handle) : IMirrorControlTarget
    {
        internal bool TopMost;
        internal bool FullScreen;
        internal bool? Audio = true;
        internal bool Closed;
        internal bool FailScreenshot;
        public nint Handle => handle;
        public bool IsFullScreen => FullScreen;
        public bool IsTopMost => TopMost;
        public bool? IsAudioEnabled => Audio;
        public bool IsPointerCaptured => false;
        public bool CanCaptureScreenshot => true;
        public void ToggleTopMost() => TopMost = !TopMost;
        public void RotateClockwise() { }
        public void ToggleFullScreen() => FullScreen = !FullScreen;
        public void ToggleAudio() => Audio = !Audio;
        public void ShowMenu() { }
        public void CloseMirror() => Closed = true;
        public string CaptureScreenshot() => FailScreenshot
            ? throw new InvalidOperationException("Expected screenshot failure for regression test.")
            : Path.Combine(Path.GetTempPath(), "mirror-bar-test.png");
    }
}
