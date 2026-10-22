using Microsoft.Win32;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Starts the receiver in the tray when the user signs in to Windows. The Run
/// value is the single source of truth, so moving the app or removing the
/// entry from Task Manager is reflected without a second stored flag.
/// </summary>
internal static class SignInLaunch
{
    internal const string BackgroundArgument = "--background";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "iPhoneMirror";

    internal static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string command &&
                    string.Equals(command, CreateCommand(), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error)
            {
                DiagnosticLogger.Exception("sign_in_launch", "read_failed", error);
                return false;
            }
        }
    }

    internal static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled) key.SetValue(ValueName, CreateCommand(), RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            DiagnosticLogger.Info("sign_in_launch", "changed", ("enabled", enabled));
            return true;
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("sign_in_launch", "write_failed", error,
                ("enabled", enabled));
            return false;
        }
    }

    internal static bool IsBackgroundLaunch(IReadOnlyList<string> arguments) =>
        arguments.Contains(BackgroundArgument, StringComparer.Ordinal);

    private static string CreateCommand()
    {
        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("Executable path is unavailable.");
        return CompactLaunchOptions.QuoteArgument(executable) + " " + BackgroundArgument;
    }
}
