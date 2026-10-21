using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App;

public partial class MainWindow
{
    private FullScreenExitOverlayWindow? _fullScreenExitOverlay;

    // Esc and F11 only reach the router while this window owns the
    // foreground, and a click on the preview is device input. Offer a
    // pointer path out of main-window full screen as well.
    private void UpdateFullScreenExitOverlay()
    {
        if (_isFullScreen && _windowSource?.Handle is { } handle && handle != 0)
        {
            _fullScreenExitOverlay ??= FullScreenExitOverlayWindow.ShowFor(handle,
                OnFullScreenExitOverlayClick);
            return;
        }
        CloseFullScreenExitOverlay();
    }

    private void CloseFullScreenExitOverlay()
    {
        _fullScreenExitOverlay?.Close();
        _fullScreenExitOverlay = null;
    }

    private void OnFullScreenExitOverlayClick()
    {
        _viewModel.AddDiagnosticLog(AppLog.Event("fullscreen_exit_overlay_click"));
        if (_isFullScreen) ToggleFullScreen();
    }
}
