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

            if (!CanSafelySetValue(focusedElement, valuePattern))
            {
                return new DirectTextInsertionAttemptResult(false, "UIA", $"{elementDescription} would require replacing existing text.");
            }

            valuePattern.SetValue(text);
            return new DirectTextInsertionAttemptResult(true, "UIAValuePattern", $"{elementDescription} accepted SetValue.");
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

    private static bool CanSafelySetValue(IUIAutomationElement element, IUIAutomationValuePattern valuePattern)
    {
        if (string.IsNullOrEmpty(valuePattern.CurrentValue))
        {
            return true;
        }

        var textPattern = TryGetTextPattern(element);
        if (textPattern is null)
        {
            return false;
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
