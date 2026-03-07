using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using whisper_windows.Interop;

namespace whisper_windows.Services;

public sealed class ClipboardPasteService
{
    private readonly DispatcherQueue _dispatcherQueue;

    public ClipboardPasteService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public Task PasteTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("The transcription was empty.");
        }

        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                DiagnosticsLogger.Info($"PasteTextAsync entered. TextLength={text.Length}.");
                SetClipboardText(text);
                DiagnosticsLogger.Info("Clipboard text set successfully.");

                await Task.Delay(100);
                SendPasteShortcut();
                DiagnosticsLogger.Info("Ctrl+V simulation completed.");

                completionSource.TrySetResult();
            }
            catch (Exception exception)
            {
                DiagnosticsLogger.Error("PasteTextAsync failed.", exception);
                completionSource.TrySetException(exception);
            }
        }))
        {
            completionSource.TrySetException(new InvalidOperationException("The UI thread was unavailable for clipboard paste."));
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

    private static void SendPasteShortcut()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        DiagnosticsLogger.Info($"Sending Ctrl+V to foreground window 0x{foregroundWindow.ToInt64():X}.");
        NativeMethods.keybd_event((byte)NativeMethods.VK_LCONTROL, 0, 0, UIntPtr.Zero);
        Thread.Sleep(15);
        NativeMethods.keybd_event((byte)NativeMethods.VK_V, 0, 0, UIntPtr.Zero);
        Thread.Sleep(15);
        NativeMethods.keybd_event((byte)NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(15);
        NativeMethods.keybd_event((byte)NativeMethods.VK_LCONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        DiagnosticsLogger.Info("keybd_event Ctrl+V sequence completed.");
    }
}
