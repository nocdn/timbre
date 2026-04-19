using System.ComponentModel;
using System.Runtime.InteropServices;
using timbre.Interop;

namespace timbre.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint TrayCallbackMessage = NativeMethods.WM_APP + 1;
    private const uint TrayIconId = 1;
    private const uint SettingsCommandId = 1001;
    private const uint QuitCommandId = 1002;
    private const uint ContextMenuFallbackDelayMilliseconds = 75;
    private const string AppName = "Timbre";
    private const int TrayWindowCoordinate = -32000;
    private const int TrayWindowSize = 1;
    private static readonly UIntPtr ContextMenuTimerId = new(1);
    private static readonly IntPtr ContextMenuTimerMessageId = new(1);

    private readonly Func<Task> _openSettingsAsync;
    private readonly Action _quit;
    private readonly NativeMethods.WndProc _trayWindowProc;
    private IntPtr _trayWindowHandle;
    private IntPtr _previousTrayWindowProc;
    private IntPtr _iconHandle;
    private bool _ownsIconHandle;
    private bool _initialized;
    private bool _contextMenuPending;
    private string _pendingContextMenuSource = string.Empty;

    public TrayIconService(Func<Task> openSettingsAsync, Action quit)
    {
        _openSettingsAsync = openSettingsAsync;
        _quit = quit;
        _trayWindowProc = TrayWindowProcedure;
    }

    public void Initialize()
    {
        DiagnosticsLogger.Info("TrayIconService.Initialize entered.");
        if (_initialized)
        {
            DiagnosticsLogger.Info("TrayIconService.Initialize skipped because it is already initialized.");
            return;
        }

        _trayWindowHandle = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOOLWINDOW,
            "STATIC",
            AppName,
            NativeMethods.WS_POPUP,
            TrayWindowCoordinate,
            TrayWindowCoordinate,
            TrayWindowSize,
            TrayWindowSize,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_trayWindowHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the tray owner window.");
        }

        _previousTrayWindowProc = NativeMethods.SetWindowLongPtr(_trayWindowHandle, NativeMethods.GWL_WNDPROC, _trayWindowProc);
        NativeMethods.ShowWindow(_trayWindowHandle, NativeMethods.SW_SHOWNOACTIVATE);

        _iconHandle = LoadTrayIcon(out _ownsIconHandle);

        var trayData = CreateNotifyIconData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
        trayData.szTip = AppName;

        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref trayData))
        {
            ReleaseIcon();
            DestroyTrayWindow();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the tray icon.");
        }

        trayData.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref trayData);

        _initialized = true;
        DiagnosticsLogger.Info("TrayIconService.Initialize completed.");
    }

    public void ShowNotification(string title, string message, bool isError)
    {
        DiagnosticsLogger.Info($"TrayIconService.ShowNotification title='{title}' error={isError}.");
        if (!_initialized)
        {
            DiagnosticsLogger.Info("TrayIconService.ShowNotification ignored because tray is not initialized.");
            return;
        }

        var trayData = CreateNotifyIconData(NativeMethods.NIF_INFO);
        trayData.szInfoTitle = Truncate(title, 63);
        trayData.szInfo = Truncate(message.Replace(Environment.NewLine, " "), 255);
        trayData.dwInfoFlags = isError ? NativeMethods.NIIF_ERROR : NativeMethods.NIIF_INFO;
        trayData.uTimeoutOrVersion = 5000;

        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref trayData);
    }

    public void Dispose()
    {
        DiagnosticsLogger.Info("TrayIconService.Dispose entered.");
        if (!_initialized)
        {
            DiagnosticsLogger.Info("TrayIconService.Dispose skipped because tray is not initialized.");
            return;
        }

        CancelPendingContextMenu();

        var trayData = CreateNotifyIconData(0);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref trayData);

        ReleaseIcon();
        DestroyTrayWindow();
        _initialized = false;
        DiagnosticsLogger.Info("TrayIconService.Dispose completed.");
    }

    private static IntPtr LoadTrayIcon(out bool ownsIconHandle)
    {
        ownsIconHandle = false;

        if (AppIcon.TryGetPath(out var iconPath))
        {
            var iconWidth = Math.Max(16, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON));
            var iconHeight = Math.Max(16, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSMICON));
            var iconHandle = NativeMethods.LoadImage(
                IntPtr.Zero,
                iconPath,
                NativeMethods.IMAGE_ICON,
                iconWidth,
                iconHeight,
                NativeMethods.LR_LOADFROMFILE);

            if (iconHandle != IntPtr.Zero)
            {
                ownsIconHandle = true;
                return iconHandle;
            }

            DiagnosticsLogger.Info($"TrayIconService could not load custom icon from '{iconPath}'. Win32Error={Marshal.GetLastWin32Error()}.");
        }

        return NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(NativeMethods.IDI_APPLICATION));
    }

    private void ReleaseIcon()
    {
        if (_ownsIconHandle && _iconHandle != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyIcon(_iconHandle);
        }

        _ownsIconHandle = false;
        _iconHandle = IntPtr.Zero;
    }

    private NativeMethods.NOTIFYICONDATA CreateNotifyIconData(uint flags)
    {
        return new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _trayWindowHandle,
            uID = TrayIconId,
            uFlags = flags,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _iconHandle,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    private IntPtr TrayWindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_initialized && message == TrayCallbackMessage && HandleTrayCallbackMessage(wParam, lParam))
        {
            return IntPtr.Zero;
        }

        if (_initialized && message == NativeMethods.WM_TIMER && wParam == ContextMenuTimerMessageId)
        {
            var source = _pendingContextMenuSource;
            CancelPendingContextMenu();
            ShowContextMenu(source);
            return IntPtr.Zero;
        }

        return CallDefaultTrayWindowProc(hWnd, message, wParam, lParam);
    }

    private bool HandleTrayCallbackMessage(IntPtr wParam, IntPtr lParam)
    {
        var trayMessage = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
        DiagnosticsLogger.Info(
            $"TrayIconService received tray callback. Message=0x{trayMessage:X} wParam=0x{wParam.ToInt64():X} lParam=0x{lParam.ToInt64():X}.");

        if (trayMessage == NativeMethods.WM_CONTEXTMENU)
        {
            QueueContextMenu("WM_CONTEXTMENU", 1);
            return true;
        }

        if (trayMessage == NativeMethods.WM_RBUTTONUP)
        {
            QueueContextMenu("WM_RBUTTONUP", ContextMenuFallbackDelayMilliseconds);
            return true;
        }

        if (trayMessage == NativeMethods.NIN_SELECT || trayMessage == NativeMethods.NIN_KEYSELECT ||
            trayMessage == NativeMethods.WM_LBUTTONUP || trayMessage == NativeMethods.WM_LBUTTONDBLCLK)
        {
            CancelPendingContextMenu();
            DiagnosticsLogger.Info($"TrayIconService opening settings from tray callback 0x{trayMessage:X}.");
            _ = _openSettingsAsync();
            return true;
        }

        return false;
    }

    private void QueueContextMenu(string source, uint delayMilliseconds)
    {
        if (_trayWindowHandle == IntPtr.Zero)
        {
            return;
        }

        _pendingContextMenuSource = source;
        _contextMenuPending = true;
        NativeMethods.KillTimer(_trayWindowHandle, ContextMenuTimerId);

        var timerHandle = NativeMethods.SetTimer(_trayWindowHandle, ContextMenuTimerId, delayMilliseconds, IntPtr.Zero);
        DiagnosticsLogger.Info(
            $"TrayIconService queued context menu. Source={source}, DelayMs={delayMilliseconds}, TimerCreated={timerHandle != IntPtr.Zero}.");

        if (timerHandle == IntPtr.Zero)
        {
            var fallbackSource = _pendingContextMenuSource + "-ImmediateFallback";
            CancelPendingContextMenu();
            ShowContextMenu(fallbackSource);
        }
    }

    private void CancelPendingContextMenu()
    {
        if (_trayWindowHandle != IntPtr.Zero && _contextMenuPending)
        {
            NativeMethods.KillTimer(_trayWindowHandle, ContextMenuTimerId);
        }

        _contextMenuPending = false;
        _pendingContextMenuSource = string.Empty;
    }

    private IntPtr CallDefaultTrayWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        return _previousTrayWindowProc != IntPtr.Zero
            ? NativeMethods.CallWindowProc(_previousTrayWindowProc, hWnd, message, wParam, lParam)
            : NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void DestroyTrayWindow()
    {
        if (_trayWindowHandle == IntPtr.Zero)
        {
            return;
        }

        if (_previousTrayWindowProc != IntPtr.Zero)
        {
            NativeMethods.SetWindowLongPtr(_trayWindowHandle, NativeMethods.GWL_WNDPROC, _previousTrayWindowProc);
            _previousTrayWindowProc = IntPtr.Zero;
        }

        NativeMethods.DestroyWindow(_trayWindowHandle);
        _trayWindowHandle = IntPtr.Zero;
    }

    private void ShowContextMenu(string source)
    {
        var menuHandle = NativeMethods.CreatePopupMenu();

        if (menuHandle == IntPtr.Zero)
        {
            DiagnosticsLogger.Info("TrayIconService.ShowContextMenu could not create the popup menu.");
            return;
        }

        try
        {
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, SettingsCommandId, "Settings");
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, QuitCommandId, "Quit");

            NativeMethods.GetCursorPos(out var cursorPosition);
            var setForegroundSucceeded = NativeMethods.SetForegroundWindow(_trayWindowHandle);
            var foregroundWindow = NativeMethods.GetForegroundWindow();
            DiagnosticsLogger.Info(
                $"TrayIconService.ShowContextMenu source={source}, cursor=({cursorPosition.X},{cursorPosition.Y}), " +
                $"SetForegroundWindowSucceeded={setForegroundSucceeded}, ForegroundHwnd=0x{foregroundWindow.ToInt64():X}, " +
                $"OwnerHwnd=0x{_trayWindowHandle.ToInt64():X}.");

            var selectedCommand = NativeMethods.TrackPopupMenuEx(
                menuHandle,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                cursorPosition.X,
                cursorPosition.Y,
                _trayWindowHandle,
                IntPtr.Zero);
            DiagnosticsLogger.Info($"TrayIconService.ShowContextMenu TrackPopupMenuEx returned {selectedCommand}.");

            NativeMethods.PostMessage(_trayWindowHandle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            var trayData = CreateNotifyIconData(0);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETFOCUS, ref trayData);

            if (selectedCommand == SettingsCommandId)
            {
                DiagnosticsLogger.Info("TrayIconService.ShowContextMenu opening settings from popup menu.");
                _ = _openSettingsAsync();
            }
            else if (selectedCommand == QuitCommandId)
            {
                DiagnosticsLogger.Info("TrayIconService.ShowContextMenu quitting from popup menu.");
                _quit();
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menuHandle);
        }
    }

    private static string Truncate(string value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
        {
            return value;
        }

        return value[..maximumLength];
    }
}
