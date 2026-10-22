using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunTrayModeTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", KeyboardTestMembers)!.SetValue(app, true);
        app.InitializeComponent();
        using var trace = new AuditTraceListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        MainWindow? main = null;
        try
        {
            TestTraySettingsPersistence(output);
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Tray);
            main = new MainWindow();
            app.MainWindow = main;
            // Exercise real hidden startup and shell registration without starting
            // device enumeration or the wireless receiver in a regression test.
            SetKeyboardField(main, "_startupServicesStarted", true);
            var everVisible = false;
            main.IsVisibleChanged += (_, _) => everVisible |= main.IsVisible;
            KeyboardCall(main, "StartInTray");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
            InteractionAssert(!everVisible && !main.ShowInTaskbar, "Tray startup flashed the workspace.");
            InteractionAssert(KeyboardField(main, "_trayIcon") is not null, "Tray icon missing.");
            var panel = (HomeWindow)KeyboardField(main, "_trayPanel");
            var vm = main.DataContext;
            KeyboardCall(main, "ShowTrayPanel");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
            InteractionAssert(panel.IsVisible && !main.IsVisible, "Tray icon did not open only home.");
            panel.Close(); // Same Closing path as Alt+F4.
            InteractionAssert(!panel.IsVisible, "Closing the panel must hide it.");
            KeyboardCall(main, "ShowTrayPanel");
            InteractionAssert(panel.IsVisible, "Panel could not reopen after Alt+F4.");
            panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            InteractionAssert(!panel.IsVisible, "Escape did not hide the panel.");

            KeyboardCall(main, "ShowTraySettings");
            var settings = (Window)KeyboardField(main, "_traySettingsWindow");
            var first = TrayTestDevice("tray-test-1", "Ray's iPhone — Long device name for layout validation");
            TrayDevices(vm).Add(first);
            vm.GetType().GetProperty("SelectedDevice")!.SetValue(vm, first);
            InteractionAssert(settings.IsVisible, "Device selection closed the global tray settings.");
            settings.Close();
            TestTrayAudioSettings(app, vm, first);
            TestHomeInteractionAndLayout(vm, output);

            TestTrayMediaStop(main, vm);

            foreach (var mode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            {
                vm.GetType().GetProperty("SelectedApplicationDisplayMode")!.SetValue(vm, mode);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(500));
                InteractionAssert(main.IsVisible && main.ShowInTaskbar && KeyboardField(main, "_trayIcon") is null,
                    $"Leaving tray for {mode} did not restore the workspace.");
                vm.GetType().GetProperty("SelectedApplicationDisplayMode")!.SetValue(vm, ApplicationDisplayMode.Tray);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
                InteractionAssert(!main.IsVisible && !main.ShowInTaskbar && KeyboardField(main, "_trayIcon") is not null,
                    $"Returning from {mode} to tray failed.");
            }
            TestTrayStartupUpdates(app, main, vm);
            InteractionAssert(trace.Messages.Count == 0, string.Join(Environment.NewLine, trace.Messages));
            Console.WriteLine("Tray tests passed: hidden startup, home reopening, Escape, busy/error handling, all features, audio settings, media stop notification, startup updates, mode switching, settings persistence, and 5 languages × 2 themes.");
            return 0;
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            File.WriteAllLines(Path.Combine(output, "binding-diagnostics.txt"), trace.Messages);
            if (main is not null) CloseWorkspaceTestWindow(main);
            app.Shutdown();
        }
    }

    private static DeviceViewModel TrayTestDevice(string udid, string name)
    {
        var constructor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        return (DeviceViewModel)constructor.Invoke([udid, name, "iPhone15,2", "18.0", "USB", "",
            Enum.Parse(constructor.GetParameters()[6].ParameterType, "Ready")]);
    }

    private static void TestTraySettingsPersistence(string output)
    {
        var type = typeof(App).Assembly.GetType("IPhoneMirror.App.Updater.UpdateSettingsStore")!;
        var store = Activator.CreateInstance(type, KeyboardTestMembers, null,
            [Path.GetFullPath(Path.Combine(output, "settings-roundtrip.json"))], null)!;
        var settings = KeyboardCall(store, "Load")!;
        var property = settings.GetType().GetProperty("ApplicationDisplayMode")!;
        property.SetValue(settings, ApplicationDisplayMode.Tray);
        KeyboardCall(store, "Save", settings);
        InteractionAssert((ApplicationDisplayMode)property.GetValue(KeyboardCall(store, "Load"))! == ApplicationDisplayMode.Tray,
            "Tray mode did not survive settings reload.");
    }

    private static ObservableCollection<DeviceViewModel> TrayDevices(object vm) =>
        (ObservableCollection<DeviceViewModel>)vm.GetType().GetProperty("Devices")!.GetValue(vm)!;
}
