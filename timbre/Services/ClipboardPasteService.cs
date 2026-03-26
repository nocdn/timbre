using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using timbre.Interfaces;
using timbre.Interop;

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

        return ExecuteClipboardOperationAsync(() =>
        {
            return CopyTextOnUiThreadAsync(text, cancellationToken);
        }, cancellationToken);
    }

    private async Task ExecuteClipboardOperationAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await RunOnUiThreadAsync(action);
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

    private static async Task CopyTextOnUiThreadAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SetClipboardTextAsync(text, cancellationToken);
        DiagnosticsLogger.Info($"Clipboard text copied successfully. TextLength={text.Length}.");
    }

    private static async Task SetClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        var lastError = 0;
        var bytes = Encoding.Unicode.GetBytes(text + '\0');

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IntPtr clipboardMemory = IntPtr.Zero;
            var clipboardOpened = false;

            try
            {
                clipboardOpened = NativeMethods.OpenClipboard(IntPtr.Zero);

                if (!clipboardOpened)
                {
                    lastError = Marshal.GetLastWin32Error();
                    DiagnosticsLogger.Info($"OpenClipboard attempt {attempt} failed with Win32 error {lastError}.");
                    await Task.Delay(40, cancellationToken);
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
}
