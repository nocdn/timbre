using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using timbre.Interfaces;
using timbre.Interop;
using timbre.Models;

namespace timbre.Services;

public sealed class ClipboardPasteService : IClipboardPasteService
{
    private const int HotkeyReleaseDelayMilliseconds = 30;
    private const int PasteDispatchDelayMilliseconds = 75;
    private const int PasteObservationPollMilliseconds = 10;
    private const int PasteObservationTimeoutMilliseconds = 600;

    private readonly IUiDispatcherQueueAccessor _dispatcherQueueAccessor;
    private readonly SemaphoreSlim _clipboardOperationLock = new(1, 1);
    private readonly Stack<IDataObject?> _clipboardBackups = [];

    private readonly record struct PasteCompletionObservation(bool Observed, uint ClipboardOwnerProcessId, long ElapsedMilliseconds);

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

    public Task PasteTextAsync(string text, HotkeyBinding? triggeringHotkey = null, bool waitForPasteCompletion = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("The transcription was empty.");
        }

        return ExecuteClipboardOperationAsync(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();
            DiagnosticsLogger.Info($"PasteTextAsync entered. TextLength={text.Length}, WaitForPasteCompletion={waitForPasteCompletion}, HasTriggeringHotkey={triggeringHotkey is not null}.");
            await SetClipboardTextAsync(text, cancellationToken);
            DiagnosticsLogger.Info($"Clipboard text set successfully. ElapsedMs={stopwatch.ElapsedMilliseconds}.");

            if (triggeringHotkey is not null)
            {
                SendInputEvents(CreateReleaseHotkeyInputs(triggeringHotkey));
                await Task.Delay(HotkeyReleaseDelayMilliseconds, cancellationToken);
            }

            await Task.Delay(PasteDispatchDelayMilliseconds, cancellationToken);
            var pasteTargetProcessId = GetForegroundProcessId();
            var pasteDispatchMethod = "SendInput";

            try
            {
                SendInputEvents(CreatePasteShortcutInputs());
                DiagnosticsLogger.Info("SendInput Ctrl+V sequence completed.");
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 87)
            {
                pasteDispatchMethod = "WM_PASTE";
                DiagnosticsLogger.Info("SendInput returned Win32 error 87. Falling back to WM_PASTE.");
                SendPasteMessage();
            }

            var pasteObservation = new PasteCompletionObservation(false, 0, 0);
            if (waitForPasteCompletion)
            {
                pasteObservation = await WaitForPasteCompletionAsync(pasteTargetProcessId, cancellationToken);
            }

            DiagnosticsLogger.Info(
                $"PasteTextAsync completed. TotalElapsedMs={stopwatch.ElapsedMilliseconds}, PasteDispatchMethod='{pasteDispatchMethod}', TargetProcessId={pasteTargetProcessId}, WaitedForPasteCompletion={waitForPasteCompletion}, PasteCompletionObserved={pasteObservation.Observed}, ClipboardOwnerProcessId={pasteObservation.ClipboardOwnerProcessId}, PasteObservationElapsedMs={pasteObservation.ElapsedMilliseconds}.");
        }, cancellationToken);
    }

    public Task BackupClipboardAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteClipboardOperationAsync(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            DiagnosticsLogger.Info($"BackupClipboardAsync: Attempting to backup clipboard. ExistingBackupCount={_clipboardBackups.Count}.");
            for (var attempt = 1; attempt <= 10; attempt++)
            {
                try
                {
                    NativeMethods.OleGetClipboard(out var backup);
                    if (backup != null)
                    {
                        _clipboardBackups.Push(backup);
                        DiagnosticsLogger.Info($"BackupClipboardAsync: Clipboard successfully backed up. Attempt={attempt}, BackupCount={_clipboardBackups.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
                        return;
                    }
                    else
                    {
                        DiagnosticsLogger.Info($"BackupClipboardAsync: OleGetClipboard returned null on attempt {attempt}.");
                    }
                }
                catch (COMException exception)
                {
                    DiagnosticsLogger.Info($"BackupClipboardAsync: Attempt {attempt} failed ({exception.Message}).");
                }
                catch (Exception exception)
                {
                    DiagnosticsLogger.Error("BackupClipboardAsync: Unexpected exception occurred.", exception);
                    break;
                }

                if (attempt < 10)
                {
                    await Task.Delay(40, cancellationToken);
                }
            }

            DiagnosticsLogger.Error("BackupClipboardAsync: Failed to backup clipboard after 10 attempts.", new InvalidOperationException("Clipboard backup failed."));
            _clipboardBackups.Push(null);
            DiagnosticsLogger.Info($"BackupClipboardAsync: Stored null backup sentinel. BackupCount={_clipboardBackups.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
        }, cancellationToken);
    }

    public Task RestoreClipboardAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteClipboardOperationAsync(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            DiagnosticsLogger.Info($"RestoreClipboardAsync: Attempting to restore clipboard. AvailableBackupCount={_clipboardBackups.Count}.");
            if (_clipboardBackups.Count == 0)
            {
                DiagnosticsLogger.Info($"RestoreClipboardAsync: No clipboard backup exists. Skipping restore. ElapsedMs={stopwatch.ElapsedMilliseconds}.");
                return;
            }

            var clipboardBackup = _clipboardBackups.Pop();
            if (clipboardBackup is null)
            {
                DiagnosticsLogger.Info($"RestoreClipboardAsync: Latest clipboard backup was unavailable. Skipping restore. RemainingBackupCount={_clipboardBackups.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
                return;
            }

            for (var attempt = 1; attempt <= 10; attempt++)
            {
                try
                {
                    NativeMethods.OleSetClipboard(clipboardBackup);
                    // We purposely DO NOT call OleFlushClipboard here. 
                    // Flushing forces the OS to render all formats to HGLOBALs, which then get destroyed 
                    // during the next dictation's EmptyClipboard(), breaking subsequent backups.
                    DiagnosticsLogger.Info($"RestoreClipboardAsync: Clipboard successfully restored from backup on attempt {attempt}. RemainingBackupCount={_clipboardBackups.Count}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
                    return;
                }
                catch (COMException exception)
                {
                    DiagnosticsLogger.Info($"RestoreClipboardAsync: Attempt {attempt} failed ({exception.Message}).");
                    if (attempt == 10)
                    {
                        DiagnosticsLogger.Error("RestoreClipboardAsync: Failed to restore clipboard after 10 attempts.", exception);
                    }
                }
                catch (Exception exception)
                {
                    DiagnosticsLogger.Error("RestoreClipboardAsync: Unexpected exception occurred.", exception);
                    break;
                }

                if (attempt < 10)
                {
                    await Task.Delay(40, cancellationToken);
                }
            }
        }, cancellationToken);
    }

    private async Task ExecuteClipboardOperationAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _clipboardOperationLock.WaitAsync(cancellationToken);

        try
        {
            await RunOnUiThreadAsync(action);
        }
        finally
        {
            _clipboardOperationLock.Release();
        }
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

    private static async Task<PasteCompletionObservation> WaitForPasteCompletionAsync(uint pasteTargetProcessId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var observedClipboardAccess = false;
        uint clipboardOwnerProcessId = 0;
        var attemptCount = PasteObservationTimeoutMilliseconds / PasteObservationPollMilliseconds;

        for (var attempt = 0; attempt < attemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var clipboardWindow = NativeMethods.GetOpenClipboardWindow();
            if (clipboardWindow != IntPtr.Zero)
            {
                var clipboardWindowProcessId = GetProcessIdForWindow(clipboardWindow);
                if (pasteTargetProcessId == 0 || clipboardWindowProcessId == pasteTargetProcessId)
                {
                    if (!observedClipboardAccess)
                    {
                        DiagnosticsLogger.Info($"Paste completion wait observed clipboard acquisition. TargetProcessId={pasteTargetProcessId}, ClipboardOwnerProcessId={clipboardWindowProcessId}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
                    }

                    observedClipboardAccess = true;
                    clipboardOwnerProcessId = clipboardWindowProcessId;
                }
            }
            else if (observedClipboardAccess)
            {
                DiagnosticsLogger.Info($"Paste completion observed. TargetProcessId={pasteTargetProcessId}, ClipboardOwnerProcessId={clipboardOwnerProcessId}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
                return new PasteCompletionObservation(true, clipboardOwnerProcessId, stopwatch.ElapsedMilliseconds);
            }

            await Task.Delay(PasteObservationPollMilliseconds, cancellationToken);
        }

        DiagnosticsLogger.Info($"Paste completion was not observed before timeout. TargetProcessId={pasteTargetProcessId}, ClipboardOwnerProcessId={clipboardOwnerProcessId}, TimeoutMs={PasteObservationTimeoutMilliseconds}.");
        return new PasteCompletionObservation(false, clipboardOwnerProcessId, stopwatch.ElapsedMilliseconds);
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

    private static uint GetForegroundProcessId()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        return GetProcessIdForWindow(foregroundWindow);
    }

    private static uint GetProcessIdForWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return 0;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        return processId;
    }
}
