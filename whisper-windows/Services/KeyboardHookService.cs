using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using whisper_windows.Interop;
using whisper_windows.Models;

namespace whisper_windows.Services;

public sealed class KeyboardHookService : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly NativeMethods.LowLevelKeyboardProc _hookCallback;
    private readonly HashSet<uint> _pressedKeys = [];
    private IntPtr _hookHandle;

    private bool _leftCtrlDown;
    private bool _rightCtrlDown;
    private bool _leftShiftDown;
    private bool _rightShiftDown;
    private bool _leftAltDown;
    private bool _rightAltDown;
    private bool _leftWinDown;
    private bool _rightWinDown;
    private bool _recordingComboActive;
    private bool _pasteLastTranscriptComboActive;
    private bool _isCapturingHotkey;
    private Action<HotkeyBinding>? _hotkeyCapturedCallback;
    private HotkeyBinding _recordingHotkey = HotkeyBinding.Default;
    private HotkeyBinding _pasteLastTranscriptHotkey = HotkeyBinding.PasteLastTranscriptDefault;

    public KeyboardHookService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _hookCallback = HandleHookCallback;
    }

    public event EventHandler? RecordingHotkeyStarted;

    public event EventHandler? RecordingHotkeyEnded;

    public event EventHandler? PasteLastTranscriptHotkeyPressed;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        IntPtr moduleHandle = IntPtr.Zero;

        try
        {
            var moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
            moduleHandle = NativeMethods.GetModuleHandle(moduleName);
        }
        catch
        {
        }

        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookCallback, moduleHandle, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to register the global keyboard hook.");
        }
    }

    public void UpdateHotkeys(HotkeyBinding recordingHotkey, HotkeyBinding pasteLastTranscriptHotkey)
    {
        _recordingHotkey = recordingHotkey ?? HotkeyBinding.Default;
        _pasteLastTranscriptHotkey = pasteLastTranscriptHotkey ?? HotkeyBinding.PasteLastTranscriptDefault;
        DiagnosticsLogger.Info(
            $"Keyboard hotkeys updated. Recording='{_recordingHotkey.ToDisplayString()}', PasteLastTranscript='{_pasteLastTranscriptHotkey.ToDisplayString()}'.");
    }

    public void BeginHotkeyCapture(Action<HotkeyBinding> hotkeyCapturedCallback)
    {
        _hotkeyCapturedCallback = hotkeyCapturedCallback;
        _isCapturingHotkey = true;
        DiagnosticsLogger.Info("Hotkey capture started.");
    }

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private IntPtr HandleHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var keyboardData = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        var message = unchecked((uint)wParam.ToInt64());

        var handled = message switch
        {
            NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN => HandleKeyDown(keyboardData.vkCode),
            NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP => HandleKeyUp(keyboardData.vkCode),
            _ => false,
        };

        return handled ? new IntPtr(1) : NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool HandleKeyDown(uint virtualKeyCode)
    {
        SetModifierState(virtualKeyCode, true);
        _pressedKeys.Add(virtualKeyCode);

        if (_isCapturingHotkey)
        {
            return HandleHotkeyCaptureKeyDown(virtualKeyCode);
        }

        var isModifierKey = IsModifierKey(virtualKeyCode);
        var matchesRecordingMainKey = virtualKeyCode == _recordingHotkey.KeyCode;
        var matchesPasteLastTranscriptMainKey = virtualKeyCode == _pasteLastTranscriptHotkey.KeyCode;

        if (IsHotkeyPressed(_recordingHotkey) && !_recordingComboActive)
        {
            _recordingComboActive = true;
            _dispatcherQueue.TryEnqueue(() => RecordingHotkeyStarted?.Invoke(this, EventArgs.Empty));
        }

        if (IsHotkeyPressed(_pasteLastTranscriptHotkey) && !_pasteLastTranscriptComboActive)
        {
            _pasteLastTranscriptComboActive = true;
            _dispatcherQueue.TryEnqueue(() => PasteLastTranscriptHotkeyPressed?.Invoke(this, EventArgs.Empty));
        }

        return (matchesRecordingMainKey && ModifiersExactlyMatch(_recordingHotkey)) ||
               (matchesPasteLastTranscriptMainKey && ModifiersExactlyMatch(_pasteLastTranscriptHotkey)) ||
               ((_recordingComboActive || _pasteLastTranscriptComboActive) && isModifierKey);
    }

    private bool HandleKeyUp(uint virtualKeyCode)
    {
        var recordingComboWasActive = _recordingComboActive;
        var pasteLastTranscriptComboWasActive = _pasteLastTranscriptComboActive;
        var isModifierKey = IsModifierKey(virtualKeyCode);
        var matchesRecordingMainKey = virtualKeyCode == _recordingHotkey.KeyCode;
        var matchesPasteLastTranscriptMainKey = virtualKeyCode == _pasteLastTranscriptHotkey.KeyCode;

        SetModifierState(virtualKeyCode, false);
        _pressedKeys.Remove(virtualKeyCode);

        if (recordingComboWasActive && !IsHotkeyPressed(_recordingHotkey))
        {
            _recordingComboActive = false;
            _dispatcherQueue.TryEnqueue(() => RecordingHotkeyEnded?.Invoke(this, EventArgs.Empty));
        }

        if (pasteLastTranscriptComboWasActive && !IsAnyHotkeyKeyStillPressed(_pasteLastTranscriptHotkey))
        {
            _pasteLastTranscriptComboActive = false;
        }

        if (_isCapturingHotkey)
        {
            return isModifierKey;
        }

        return matchesRecordingMainKey ||
               matchesPasteLastTranscriptMainKey ||
               ((recordingComboWasActive || pasteLastTranscriptComboWasActive) && isModifierKey);
    }

    private bool HandleHotkeyCaptureKeyDown(uint virtualKeyCode)
    {
        if (IsModifierKey(virtualKeyCode))
        {
            return true;
        }

        var capturedHotkey = new HotkeyBinding
        {
            Control = IsCtrlDown,
            Shift = IsShiftDown,
            Alt = IsAltDown,
            Windows = IsWindowsDown,
            KeyCode = virtualKeyCode,
        };

        _isCapturingHotkey = false;
        var callback = _hotkeyCapturedCallback;
        _hotkeyCapturedCallback = null;

        _dispatcherQueue.TryEnqueue(() => callback?.Invoke(capturedHotkey));
        DiagnosticsLogger.Info($"Hotkey capture completed. Captured='{capturedHotkey.ToDisplayString()}'.");

        return true;
    }

    private void SetModifierState(uint virtualKeyCode, bool isDown)
    {
        switch (virtualKeyCode)
        {
            case NativeMethods.VK_CONTROL:
            case NativeMethods.VK_LCONTROL:
                _leftCtrlDown = isDown;
                break;
            case NativeMethods.VK_RCONTROL:
                _rightCtrlDown = isDown;
                break;
            case NativeMethods.VK_SHIFT:
            case NativeMethods.VK_LSHIFT:
                _leftShiftDown = isDown;
                break;
            case NativeMethods.VK_RSHIFT:
                _rightShiftDown = isDown;
                break;
            case NativeMethods.VK_MENU:
            case NativeMethods.VK_LMENU:
                _leftAltDown = isDown;
                break;
            case NativeMethods.VK_RMENU:
                _rightAltDown = isDown;
                break;
            case NativeMethods.VK_LWIN:
                _leftWinDown = isDown;
                break;
            case NativeMethods.VK_RWIN:
                _rightWinDown = isDown;
                break;
        }
    }

    private bool IsModifierKey(uint virtualKeyCode)
    {
        return virtualKeyCode is NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
            or NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT
            or NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU
            or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;
    }

    private bool ModifiersExactlyMatch(HotkeyBinding hotkey)
    {
        return hotkey.Control == IsCtrlDown &&
               hotkey.Shift == IsShiftDown &&
               hotkey.Alt == IsAltDown &&
               hotkey.Windows == IsWindowsDown;
    }

    private bool IsHotkeyPressed(HotkeyBinding hotkey)
    {
        return _pressedKeys.Contains(hotkey.KeyCode) && ModifiersExactlyMatch(hotkey);
    }

    private bool IsAnyHotkeyKeyStillPressed(HotkeyBinding hotkey)
    {
        return _pressedKeys.Contains(hotkey.KeyCode) ||
               (hotkey.Control && IsCtrlDown) ||
               (hotkey.Shift && IsShiftDown) ||
               (hotkey.Alt && IsAltDown) ||
               (hotkey.Windows && IsWindowsDown);
    }

    private bool IsCtrlDown => _leftCtrlDown || _rightCtrlDown;

    private bool IsShiftDown => _leftShiftDown || _rightShiftDown;

    private bool IsAltDown => _leftAltDown || _rightAltDown;

    private bool IsWindowsDown => _leftWinDown || _rightWinDown;
}
