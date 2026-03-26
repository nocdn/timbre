using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using timbre.Models;
using timbre.ViewModels;
using Xunit;

namespace timbre.tests.ViewModels;

[ExcludeFromCodeCoverage]
public sealed class TranscriptHistoryItemViewModelTests
{
    [Fact]
    public void Constructor_SetsPropertiesCorrectly()
    {
        // Arrange
        var entry = new TranscriptHistoryEntry
        {
            Id = "test-id-123",
            Text = "Hello world",
            CreatedAtUtc = new DateTimeOffset(2023, 1, 1, 12, 0, 0, TimeSpan.Zero)
        };

        // Act
        var viewModel = new TranscriptHistoryItemViewModel(entry);

        // Assert
        viewModel.EntryId.Should().Be("test-id-123");
        viewModel.Text.Should().Be("Hello world");
        viewModel.CreatedAtDisplay.Should().Be(entry.CreatedAtUtc.ToLocalTime().ToString("g"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Hello", true)]
    [InlineData("hello", true)]
    [InlineData("WORLD", true)]
    [InlineData("test", false)]
    [InlineData("123", false)]
    public void MatchesSearch_ReturnsExpectedResult(string? searchText, bool expected)
    {
        // Arrange
        var entry = new TranscriptHistoryEntry
        {
            Text = "Hello world from the transcriber"
        };
        var viewModel = new TranscriptHistoryItemViewModel(entry);

        // Act
        var result = viewModel.MatchesSearch(searchText);

        // Assert
        result.Should().Be(expected);
    }
}
