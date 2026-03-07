using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using timbre.Interfaces;
using timbre.Interop;
using timbre.Models;

namespace timbre.Services;

public sealed class ClipboardPasteService : IClipboardPasteService
{
    private readonly IUiDispatcherQueueAccessor _dispatcherQueueAccessor;

    public ClipboardPasteService(IUiDispatcherQueueAccessor dispatcherQueueAccessor)
    {
        _dispatcherQueueAccessor = dispatcherQueueAccessor;
    }

    public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("The transcription was empty.");
        }

        return RunOnUiThreadAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetClipboardText(text);
            DiagnosticsLogger.Info($"Clipboard text copied successfully. TextLength={text.Length}.");
            return Task.CompletedTask;
        });
    }

    public Task PasteTextAsync(string text, HotkeyBinding? triggeringHotkey = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("The transcription was empty.");
        }

        return RunOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiagnosticsLogger.Info($"PasteTextAsync entered. TextLength={text.Length}.");
            SetClipboardText(text);
            DiagnosticsLogger.Info("Clipboard text set successfully.");

            if (triggeringHotkey is not null)
            {
                SendInputEvents(CreateReleaseHotkeyInputs(triggeringHotkey));
                await Task.Delay(30, cancellationToken);
            }

            await Task.Delay(75, cancellationToken);
            try
            {
                SendInputEvents(CreatePasteShortcutInputs());
                DiagnosticsLogger.Info("SendInput Ctrl+V sequence completed.");
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 87)
            {
                DiagnosticsLogger.Info("SendInput returned Win32 error 87. Falling back to WM_PASTE.");
                SendPasteMessage();
            }
        });
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        var dispatcherQueue = _dispatcherQueueAccessor.DispatcherQueue;
        if (dispatcherQueue is null)
        {
            return action();
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    completionSource.TrySetResult();
                }
                catch (Exception exception)
                {
                    completionSource.TrySetException(exception);
                }
            }))
        {
            completionSource.TrySetException(new InvalidOperationException("The UI thread was unavailable for clipboard operations."));
        }

        return completionSource.Task;
    }

    private static void SetClipboardText(string text)
    {
        var lastError = 0;
        var bytes = Encoding.Unicode.GetBytes(text + '\0');

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            IntPtr clipboardMemory = IntPtr.Zero;
            var clipboardOpened = false;

            try
            {
                clipboardOpened = NativeMethods.OpenClipboard(IntPtr.Zero);

                if (!clipboardOpened)
                {
                    lastError = Marshal.GetLastWin32Error();
                    DiagnosticsLogger.Info($"OpenClipboard attempt {attempt} failed with Win32 error {lastError}.");
                    Thread.Sleep(40);
                    continue;
                }

                if (!NativeMethods.EmptyClipboard())
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to clear the clipboard.");
                }

                clipboardMemory = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes.Length);

                if (clipboardMemory == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to allocate clipboard memory.");
                }

                var target = NativeMethods.GlobalLock(clipboardMemory);

                if (target == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to lock clipboard memory.");
                }

                try
                {
                    Marshal.Copy(bytes, 0, target, bytes.Length);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(clipboardMemory);
                }

                if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, clipboardMemory) == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to place text on the clipboard.");
                }

                clipboardMemory = IntPtr.Zero;
                return;
            }
            finally
            {
                if (clipboardOpened)
                {
                    NativeMethods.CloseClipboard();
                }

                if (clipboardMemory != IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(clipboardMemory);
                }
            }
        }

        throw new Win32Exception(lastError, "Failed to open the clipboard after multiple attempts.");
    }

    private static void SendInputEvents(NativeMethods.INPUT[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            var lastError = Marshal.GetLastWin32Error();
            var exception = new Win32Exception(lastError, "Failed to send keyboard input.");
            DiagnosticsLogger.Error($"SendInput failed. Requested={inputs.Length}, Sent={sent}, LastError={lastError}.", exception);
            throw exception;
        }
    }

    private static NativeMethods.INPUT[] CreatePasteShortcutInputs()
    {
        return
        [
            CreateKeyboardInput((ushort)NativeMethods.VK_CONTROL, false),
            CreateKeyboardInput((ushort)'V', false),
            CreateKeyboardInput((ushort)'V', true),
            CreateKeyboardInput((ushort)NativeMethods.VK_CONTROL, true),
        ];
    }

    private static NativeMethods.INPUT[] CreateReleaseHotkeyInputs(HotkeyBinding hotkey)
    {
        var inputs = new List<NativeMethods.INPUT>
        {
            CreateKeyboardInput((ushort)hotkey.KeyCode, true),
        };

        if (hotkey.Shift)
        {
            inputs.Add(CreateKeyboardInput((ushort)NativeMethods.VK_LSHIFT, true));
            inputs.Add(CreateKeyboardInput((ushort)NativeMethods.VK_RSHIFT, true));
        }

        if (hotkey.Alt)
        {
            inputs.Add(CreateKeyboardInput((ushort)NativeMethods.VK_LMENU, true));
            inputs.Add(CreateKeyboardInput((ushort)NativeMethods.VK_RMENU, true));
        }

        if (hotkey.Control)
        {
            inputs.Add(CreateKeyboardInput((ushort)NativeMethods.VK_LCONTROL, true));
            inputs.Add(CreateKeyboardInput((ushort)NativeMethods.VK_RCONTROL, true));
        }

        if (hotkey.Windows)
        {
            inputs.Add(CreateKeyboardInput((ushort)NativeMethods.VK_LWIN, true));
            inputs.Add(CreateKeyboardInput((ushort)NativeMethods.VK_RWIN, true));
        }

        return inputs.ToArray();
    }

    private static NativeMethods.INPUT CreateKeyboardInput(ushort virtualKey, bool keyUp)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                },
            },
        };
    }

    private static void SendPasteMessage()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("There is no active window available for paste.");
        }

        if (!NativeMethods.PostMessage(foregroundWindow, NativeMethods.WM_PASTE, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to post a paste message to the active window.");
        }

        DiagnosticsLogger.Info($"WM_PASTE posted to foreground window 0x{foregroundWindow.ToInt64():X}.");
    }
}
