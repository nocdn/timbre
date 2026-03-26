using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using timbre.Models;
using timbre.Services;
using Xunit;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class HotkeyValidationServiceTests
{
    [Fact]
    public void Validate_RequiresRealModifier()
    {
        var binding = new HotkeyBinding
        {
            Control = false,
            Shift = true,
            KeyCode = 0x41,
        };

        var result = HotkeyValidationService.Validate(binding, HotkeyBinding.PasteLastTranscriptDefault, HotkeyBinding.OpenHistoryDefault);

        result.Errors.Should().ContainSingle(error => error.Contains("must include Ctrl, Alt, or Win", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsTabMainKey()
    {
        var binding = new HotkeyBinding
        {
            Control = true,
            KeyCode = 0x09,
        };

        var result = HotkeyValidationService.Validate(binding, HotkeyBinding.PasteLastTranscriptDefault, HotkeyBinding.OpenHistoryDefault);

        result.Errors.Should().ContainSingle(error => error.Contains("cannot use Tab as the main key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x0D)]
    [InlineData(0x1B)]
    public void Validate_RejectsEnterAndEscMainKeys(uint keyCode)
    {
        var binding = new HotkeyBinding
        {
            Control = true,
            KeyCode = keyCode,
        };

        var result = HotkeyValidationService.Validate(binding, HotkeyBinding.PasteLastTranscriptDefault, HotkeyBinding.OpenHistoryDefault);

        result.Errors.Should().ContainSingle(error => error.Contains("cannot use Enter or Esc as the main key", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsModifierAsMainKey()
    {
        var binding = new HotkeyBinding
        {
            Control = true,
            KeyCode = 0xA2,
        };

        var result = HotkeyValidationService.Validate(binding, HotkeyBinding.PasteLastTranscriptDefault, HotkeyBinding.OpenHistoryDefault);

        result.Errors.Should().ContainSingle(error => error.Contains("cannot use a modifier key as the main key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x2C)]
    [InlineData(0xAF)]
    [InlineData(0x5F)]
    public void Validate_WarnsForProblematicSystemKeys(uint keyCode)
    {
        var binding = new HotkeyBinding
        {
            Control = true,
            KeyCode = keyCode,
        };

        var result = HotkeyValidationService.Validate(binding, HotkeyBinding.PasteLastTranscriptDefault, HotkeyBinding.OpenHistoryDefault);

        result.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_DefaultHotkeysRemainValid()
    {
        var result = HotkeyValidationService.Validate(
            HotkeyBinding.Default,
            HotkeyBinding.PasteLastTranscriptDefault,
            HotkeyBinding.OpenHistoryDefault);

        result.HasErrors.Should().BeFalse();
    }
}
