using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using timbre.Services;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class DeepgramStreamingTranscriptionClientTests
{
    [Fact]
    public void BuildEndpoint_WhenUsingFlux_AddsEotTimeoutInMilliseconds()
    {
        // Act
        var endpoint = DeepgramStreamingTranscriptionClient.BuildEndpoint("flux-general-en", "en", 0.6);

        // Assert
        endpoint.AbsoluteUri.Should().StartWith("wss://api.deepgram.com/v2/listen");
        endpoint.Query.Should().Contain("eot_timeout_ms=600");
        endpoint.Query.Should().Contain("eot_threshold=0.7");
        endpoint.Query.Should().NotContain("endpointing=");
    }

    [Fact]
    public void BuildEndpoint_WhenUsingNova_AddsEndpointingInMilliseconds()
    {
        // Act
        var endpoint = DeepgramStreamingTranscriptionClient.BuildEndpoint("nova-3", "en", 0.6);

        // Assert
        endpoint.AbsoluteUri.Should().StartWith("wss://api.deepgram.com/v1/listen");
        endpoint.Query.Should().Contain("endpointing=600");
        endpoint.Query.Should().Contain("interim_results=true");
        endpoint.Query.Should().NotContain("eot_timeout_ms=");
    }
}
