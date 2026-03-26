using System.Diagnostics;
using timbre.Interfaces;
using timbre.Models;

namespace timbre.Services;

internal readonly record struct DirectTextInsertionAttemptResult(bool Succeeded, string Method, string Detail);

internal interface IUiAutomationDirectTextInsertionService
{
    DirectTextInsertionAttemptResult TryInsertText(string text);
}

internal interface IUnicodeTextTypingService
{
    Task TypeTextAsync(string text, HotkeyBinding? triggeringHotkey = null, CancellationToken cancellationToken = default);
}

internal sealed class TextInsertionService : ITextInsertionService
{
    private readonly IUiDispatcherQueueAccessor _dispatcherQueueAccessor;
    private readonly IUiAutomationDirectTextInsertionService _uiAutomationDirectTextInsertionService;
    private readonly IUnicodeTextTypingService _unicodeTextTypingService;
    private readonly SemaphoreSlim _insertionLock = new(1, 1);

    public TextInsertionService(
        IUiDispatcherQueueAccessor dispatcherQueueAccessor,
        IUiAutomationDirectTextInsertionService uiAutomationDirectTextInsertionService,
        IUnicodeTextTypingService unicodeTextTypingService)
    {
        _dispatcherQueueAccessor = dispatcherQueueAccessor;
        _uiAutomationDirectTextInsertionService = uiAutomationDirectTextInsertionService;
        _unicodeTextTypingService = unicodeTextTypingService;
    }

    public async Task InsertTextAsync(string text, HotkeyBinding? triggeringHotkey = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("The transcription was empty.");
        }

        await _insertionLock.WaitAsync(cancellationToken);

        try
        {
            await RunOnUiThreadAsync(async () =>
            {
                var stopwatch = Stopwatch.StartNew();
                cancellationToken.ThrowIfCancellationRequested();
                DiagnosticsLogger.Info($"InsertTextAsync entered. TextLength={text.Length}, HasTriggeringHotkey={triggeringHotkey is not null}.");

                var directInsertionAttempt = _uiAutomationDirectTextInsertionService.TryInsertText(text);
                if (directInsertionAttempt.Succeeded)
                {
                    DiagnosticsLogger.Info(
                        $"InsertTextAsync completed with direct insertion. Method='{directInsertionAttempt.Method}', Detail='{directInsertionAttempt.Detail}', TotalElapsedMs={stopwatch.ElapsedMilliseconds}.");
                    return;
                }

                DiagnosticsLogger.Info(
                    $"Direct insertion unavailable. Method='{directInsertionAttempt.Method}', Detail='{directInsertionAttempt.Detail}'. Falling back to Unicode typing.");
                await _unicodeTextTypingService.TypeTextAsync(text, triggeringHotkey, cancellationToken);
                DiagnosticsLogger.Info($"InsertTextAsync completed with Unicode typing fallback. TotalElapsedMs={stopwatch.ElapsedMilliseconds}.");
            });
        }
        finally
        {
            _insertionLock.Release();
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
            completionSource.TrySetException(new InvalidOperationException("The UI thread was unavailable for text insertion."));
        }

        return completionSource.Task;
    }
}
