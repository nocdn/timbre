using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using timbre.Models;
using Xunit;

namespace timbre.tests.Models;

[ExcludeFromCodeCoverage]
public sealed class HotkeyBindingTests
{
    [Fact]
    public void Default_ReturnsExpectedControlCloseBracket()
    {
        // Act
        var result = HotkeyBinding.Default;

        // Assert
        result.Control.Should().BeTrue();
        result.Shift.Should().BeFalse();
        result.Alt.Should().BeFalse();
        result.Windows.Should().BeFalse();
        result.KeyCode.Should().Be(0xDD); // ] (VK_OEM_6)
    }

    [Fact]
    public void ToDisplayString_Default_ReturnsCtrlCloseBracket()
    {
        // Arrange
        var binding = HotkeyBinding.Default;

        // Act
        var displayString = binding.ToDisplayString();

        // Assert
        displayString.Should().Be("Ctrl+]");
    }

    [Fact]
    public void PasteLastTranscriptDefault_IsControlOpenBracket()
    {
        var result = HotkeyBinding.PasteLastTranscriptDefault;
        result.Control.Should().BeTrue();
        result.Shift.Should().BeFalse();
        result.KeyCode.Should().Be(0xDB);
        result.ToDisplayString().Should().Be("Ctrl+[");
    }

    [Fact]
    public void OpenHistoryDefault_IsControlApostrophe()
    {
        var result = HotkeyBinding.OpenHistoryDefault;
        result.Control.Should().BeTrue();
        result.Shift.Should().BeFalse();
        result.KeyCode.Should().Be(0xDE);
        result.ToDisplayString().Should().Be("Ctrl+'");
    }

    [Fact]
    public void ToDisplayString_AllModifiers_ReturnsCorrectOrder()
    {
        // Arrange
        var binding = new HotkeyBinding
        {
            Control = true,
            Shift = true,
            Alt = true,
            Windows = true,
            KeyCode = 0x41 // A
        };

        // Act
        var displayString = binding.ToDisplayString();

        // Assert
        displayString.Should().Be("Ctrl+Shift+Alt+Win+A");
    }

    [Fact]
    public void Equals_SameValues_ReturnsTrue()
    {
        // Arrange
        var binding1 = new HotkeyBinding { Control = true, KeyCode = 0x41 };
        var binding2 = new HotkeyBinding { Control = true, KeyCode = 0x41 };

        // Act
        var result = binding1.Equals(binding2);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentValues_ReturnsFalse()
    {
        // Arrange
        var binding1 = new HotkeyBinding { Control = true, KeyCode = 0x41 };
        var binding2 = new HotkeyBinding { Control = true, KeyCode = 0x42 }; // B

        // Act
        var result = binding1.Equals(binding2);

        // Assert
        result.Should().BeFalse();
    }
}
