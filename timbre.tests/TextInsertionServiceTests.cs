using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.UI.Dispatching;
using timbre.Interfaces;
using timbre.Models;
using timbre.Services;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class TextInsertionServiceTests
{
    [Fact]
    public async Task InsertTextAsync_UsesDirectInsertionWhenAvailable()
    {
        var directInserter = new FakeUiAutomationDirectTextInsertionService(
            new DirectTextInsertionAttemptResult(true, "UIAValuePattern", "Inserted directly."));
        var unicodeTyper = new FakeUnicodeTextTypingService();
        var service = CreateService(directInserter, unicodeTyper);

        await service.InsertTextAsync("hello");

        directInserter.AttemptCount.Should().Be(1);
        unicodeTyper.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task InsertTextAsync_FallsBackToUnicodeTypingWhenDirectInsertionFails()
    {
        var directInserter = new FakeUiAutomationDirectTextInsertionService(
            new DirectTextInsertionAttemptResult(false, "UIA", "ValuePattern unavailable."));
        var unicodeTyper = new FakeUnicodeTextTypingService();
        var service = CreateService(directInserter, unicodeTyper);
        var hotkey = HotkeyBinding.PasteLastTranscriptDefault;

        await service.InsertTextAsync("hello", hotkey);

        directInserter.AttemptCount.Should().Be(1);
        unicodeTyper.CallCount.Should().Be(1);
        unicodeTyper.LastTypedText.Should().Be("hello");
        unicodeTyper.LastHotkey.Should().BeSameAs(hotkey);
    }

    [Fact]
    public async Task InsertTextAsync_WhenUnicodeTypingIsPreferred_SkipsDirectInsertion()
    {
        var directInserter = new FakeUiAutomationDirectTextInsertionService(
            new DirectTextInsertionAttemptResult(true, "UIAValuePattern", "Inserted directly."));
        var unicodeTyper = new FakeUnicodeTextTypingService();
        var service = CreateService(directInserter, unicodeTyper);

        await service.InsertTextAsync("streaming chunk", insertionMode: TextInsertionMode.PreferUnicodeTyping);

        directInserter.AttemptCount.Should().Be(0);
        unicodeTyper.CallCount.Should().Be(1);
        unicodeTyper.LastTypedText.Should().Be("streaming chunk");
    }

    [Fact]
    public async Task InsertTextAsync_RejectsEmptyText()
    {
        var service = CreateService(
            new FakeUiAutomationDirectTextInsertionService(new DirectTextInsertionAttemptResult(true, "UIAValuePattern", "Inserted directly.")),
            new FakeUnicodeTextTypingService());

        var act = () => service.InsertTextAsync(" ");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The transcription was empty.");
    }

    private static TextInsertionService CreateService(
        IUiAutomationDirectTextInsertionService directInserter,
        IUnicodeTextTypingService unicodeTyper)
    {
        return new TextInsertionService(new TestDispatcherQueueAccessor(), directInserter, unicodeTyper);
    }

    private sealed class TestDispatcherQueueAccessor : IUiDispatcherQueueAccessor
    {
        public DispatcherQueue? DispatcherQueue { get; set; }
    }

    private sealed class FakeUiAutomationDirectTextInsertionService : IUiAutomationDirectTextInsertionService
    {
        private readonly DirectTextInsertionAttemptResult _result;

        public FakeUiAutomationDirectTextInsertionService(DirectTextInsertionAttemptResult result)
        {
            _result = result;
        }

        public int AttemptCount { get; private set; }

        public DirectTextInsertionAttemptResult TryInsertText(string text)
        {
            AttemptCount++;
            return _result;
        }
    }

    private sealed class FakeUnicodeTextTypingService : IUnicodeTextTypingService
    {
        public int CallCount { get; private set; }

        public string? LastTypedText { get; private set; }

        public HotkeyBinding? LastHotkey { get; private set; }

        public Task TypeTextAsync(string text, HotkeyBinding? triggeringHotkey = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastTypedText = text;
            LastHotkey = triggeringHotkey;
            return Task.CompletedTask;
        }
    }
}
