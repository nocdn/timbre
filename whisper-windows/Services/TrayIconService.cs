using System.ComponentModel;
using System.Runtime.InteropServices;
using whisper_windows.Interop;

namespace whisper_windows.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint TrayCallbackMessage = NativeMethods.WM_APP + 1;
    private const uint TrayIconId = 1;
    private const uint SettingsCommandId = 1001;
    private const uint QuitCommandId = 1002;
    private const string AppName = "Timbre";

    private readonly Func<Task> _openSettingsAsync;
    private readonly Action _quit;
    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private bool _initialized;

    public TrayIconService(Func<Task> openSettingsAsync, Action quit)
    {
        _openSettingsAsync = openSettingsAsync;
        _quit = quit;
    }

    public void Initialize(IntPtr windowHandle)
    {
        DiagnosticsLogger.Info($"TrayIconService.Initialize entered with hwnd=0x{windowHandle.ToInt64():X}.");
        if (_initialized)
        {
            DiagnosticsLogger.Info("TrayIconService.Initialize skipped because it is already initialized.");
            return;
        }

        _windowHandle = windowHandle;
        _iconHandle = NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(NativeMethods.IDI_APPLICATION));

        var trayData = CreateNotifyIconData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
        trayData.szTip = AppName;

        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref trayData))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the tray icon.");
        }

        trayData.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref trayData);

        _initialized = true;
        DiagnosticsLogger.Info("TrayIconService.Initialize completed.");
    }

    public bool HandleWindowMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!_initialized || message != TrayCallbackMessage)
        {
            return false;
        }

        var trayMessage = unchecked((uint)(lParam.ToInt64() & 0xFFFF));

        if (trayMessage == NativeMethods.WM_CONTEXTMENU || trayMessage == NativeMethods.WM_RBUTTONUP)
        {
            ShowContextMenu();
            return true;
        }

        if (trayMessage == NativeMethods.NIN_SELECT || trayMessage == NativeMethods.NIN_KEYSELECT ||
            trayMessage == NativeMethods.WM_LBUTTONUP || trayMessage == NativeMethods.WM_LBUTTONDBLCLK)
        {
            _ = _openSettingsAsync();
            return true;
        }

        return false;
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

        var trayData = CreateNotifyIconData(0);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref trayData);

        _initialized = false;
        _windowHandle = IntPtr.Zero;
        _iconHandle = IntPtr.Zero;
        DiagnosticsLogger.Info("TrayIconService.Dispose completed.");
    }

    private NativeMethods.NOTIFYICONDATA CreateNotifyIconData(uint flags)
    {
        return new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _windowHandle,
            uID = TrayIconId,
            uFlags = flags,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _iconHandle,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    private void ShowContextMenu()
    {
        var menuHandle = NativeMethods.CreatePopupMenu();

        if (menuHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, SettingsCommandId, "Settings");
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, QuitCommandId, "Quit");

            NativeMethods.GetCursorPos(out var cursorPosition);
            NativeMethods.SetForegroundWindow(_windowHandle);

            var selectedCommand = NativeMethods.TrackPopupMenuEx(
                menuHandle,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                cursorPosition.X,
                cursorPosition.Y,
                _windowHandle,
                IntPtr.Zero);

            NativeMethods.PostMessage(_windowHandle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            var trayData = CreateNotifyIconData(0);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETFOCUS, ref trayData);

            if (selectedCommand == SettingsCommandId)
            {
                _ = _openSettingsAsync();
            }
            else if (selectedCommand == QuitCommandId)
            {
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
