using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FluentAssertions;
using timbre.Services;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class ElevenLabsRealtimeTranscriptionClientTests
{
    [Fact]
    public void BuildEndpoint_UsesConfiguredVadSilenceThresholdWithInvariantFormatting()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            var endpoint = ElevenLabsRealtimeTranscriptionClient.BuildEndpoint("en", 0.6, "single-use-token");

            endpoint.Query.Should().Contain("vad_silence_threshold_secs=0.6");
            endpoint.Query.Should().Contain("language_code=en");
            endpoint.Query.Should().Contain("token=single-use-token");
            endpoint.Query.Should().NotContain("vad_silence_threshold_secs=0%2C6");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
