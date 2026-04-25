using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace timbre.Services;

internal sealed class UiAutomationDirectTextInsertionService : IUiAutomationDirectTextInsertionService
{
    public UiAutomationDirectTextInsertionService()
    {
    }

    public DirectTextInsertionAttemptResult TryInsertText(string text)
    {
        try
        {
            IUIAutomation automation = new CUIAutomation8();
            var focusedElement = automation.GetFocusedElement();
            if (focusedElement is null)
            {
                return new DirectTextInsertionAttemptResult(false, "UIA", "No focused automation element was available.");
            }

            var elementDescription = DescribeElement(focusedElement);
            if (focusedElement.CurrentIsEnabled == 0)
            {
                return new DirectTextInsertionAttemptResult(false, "UIA", $"{elementDescription} is disabled.");
            }

            if (focusedElement.CurrentIsPassword != 0)
            {
                return new DirectTextInsertionAttemptResult(false, "UIA", $"{elementDescription} is a password field.");
            }

            if (TryGetValuePattern(focusedElement) is not { } valuePattern)
            {
                return new DirectTextInsertionAttemptResult(false, "UIA", $"{elementDescription} does not expose ValuePattern.");
            }

            if (valuePattern.CurrentIsReadOnly != 0)
            {
                return new DirectTextInsertionAttemptResult(false, "UIA", $"{elementDescription} is read-only.");
            }

            if (TryGetTextPattern(focusedElement) is not { } textPattern)
            {
                return new DirectTextInsertionAttemptResult(false, "UIA", $"{elementDescription} does not expose TextPattern for caret placement.");
            }

            if (!CanSafelySetValue(textPattern, valuePattern))
            {
                return new DirectTextInsertionAttemptResult(false, "UIA", $"{elementDescription} would require replacing existing text.");
            }

            valuePattern.SetValue(text);
            if (TryMoveCaretToEnd(focusedElement, out var caretPlacementDetail))
            {
                return new DirectTextInsertionAttemptResult(true, "UIAValuePattern", $"{elementDescription} accepted SetValue and moved caret to the end.");
            }

            DiagnosticsLogger.Info($"UI Automation direct insertion accepted SetValue but could not confirm caret placement. {caretPlacementDetail}");
            return new DirectTextInsertionAttemptResult(true, "UIAValuePattern", $"{elementDescription} accepted SetValue; caret placement was not confirmed.");
        }
        catch (COMException exception)
        {
            DiagnosticsLogger.Info($"UI Automation direct insertion failed with COM error 0x{exception.HResult:X8}: {exception.Message}");
            return new DirectTextInsertionAttemptResult(false, "UIA", $"COM failure 0x{exception.HResult:X8}.");
        }
        catch (Exception exception)
        {
            DiagnosticsLogger.Error("UI Automation direct insertion failed unexpectedly.", exception);
            return new DirectTextInsertionAttemptResult(false, "UIA", exception.Message);
        }
    }

    private static bool CanSafelySetValue(IUIAutomationTextPattern textPattern, IUIAutomationValuePattern valuePattern)
    {
        if (string.IsNullOrEmpty(valuePattern.CurrentValue))
        {
            return true;
        }

        var selection = textPattern.GetSelection();
        if (selection is null || selection.Length != 1)
        {
            return false;
        }

        var selectedRange = selection.GetElement(0);
        var documentRange = textPattern.DocumentRange;
        if (selectedRange is null || documentRange is null)
        {
            return false;
        }

        return documentRange.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, selectedRange, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start) == 0 &&
               documentRange.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, selectedRange, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) == 0;
    }

    private static bool TryMoveCaretToEnd(IUIAutomationElement element, out string detail)
    {
        detail = string.Empty;

        try
        {
            var textPattern = TryGetTextPattern(element);
            if (textPattern is null)
            {
                detail = $"{DescribeElement(element)} does not expose TextPattern after SetValue.";
                return false;
            }

            var documentRange = textPattern.DocumentRange;
            if (documentRange is null)
            {
                detail = $"{DescribeElement(element)} did not return a document range after SetValue.";
                return false;
            }

            documentRange.MoveEndpointByRange(
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                documentRange,
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
            documentRange.Select();
            return true;
        }
        catch (COMException exception)
        {
            detail = $"COM failure 0x{exception.HResult:X8} while moving caret to the end: {exception.Message}";
            return false;
        }
        catch (Exception exception)
        {
            detail = $"Unexpected failure while moving caret to the end: {exception.Message}";
            return false;
        }
    }

    private static IUIAutomationValuePattern? TryGetValuePattern(IUIAutomationElement element)
    {
        return element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) as IUIAutomationValuePattern;
    }

    private static IUIAutomationTextPattern? TryGetTextPattern(IUIAutomationElement element)
    {
        return element.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) as IUIAutomationTextPattern;
    }

    private static string DescribeElement(IUIAutomationElement element)
    {
        var name = string.IsNullOrWhiteSpace(element.CurrentName) ? "<unnamed>" : element.CurrentName;
        var className = string.IsNullOrWhiteSpace(element.CurrentClassName) ? "<no class>" : element.CurrentClassName;
        var frameworkId = string.IsNullOrWhiteSpace(element.CurrentFrameworkId) ? "<no framework>" : element.CurrentFrameworkId;
        return $"Element Name='{name}', Class='{className}', Framework='{frameworkId}', ControlType={element.CurrentControlType}, Handle=0x{element.CurrentNativeWindowHandle.ToInt64():X}";
    }
}
