using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using FluentAssertions;
using NAudio.CoreAudioApi;
using timbre.Interfaces;
using timbre.Models;
using timbre.Services;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class DictationControllerPostProcessingTests
{
    [Fact]
    public async Task ApplyLlmPostProcessingWithFallbackAsync_WhenPostProcessingSucceeds_ReturnsCleanedTranscript()
    {
        // Arrange
        var handler = new StaticResponseHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "{\"cleaned_text\":\"Cleaned transcript\"}"
                          }
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json"),
            });
        var notifications = new FakeNotificationService();
        var controller = CreateController(handler, notifications);
        var settings = new AppSettings
        {
            LlmPostProcessingEnabled = true,
            LlmPostProcessingProvider = LlmPostProcessingProvider.Cerebras,
            CerebrasApiKey = "cerebras-key",
        };

        // Act
        var result = await controller.ApplyLlmPostProcessingWithFallbackAsync("um cleaned transcript", settings, CancellationToken.None);

        // Assert
        result.Should().Be("Cleaned transcript");
        notifications.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyLlmPostProcessingWithFallbackAsync_WhenPostProcessingFails_ReturnsRawTranscriptAndShowsProviderNotification()
    {
        // Arrange
        var handler = new StaticResponseHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"message":"Slow down"}}""", Encoding.UTF8, "application/json"),
            });
        var notifications = new FakeNotificationService();
        var controller = CreateController(handler, notifications);
        var statuses = new List<DictationStatusChangedEventArgs>();
        controller.StatusChanged += (_, args) => statuses.Add(args);
        var settings = new AppSettings
        {
            LlmPostProcessingEnabled = true,
            LlmPostProcessingProvider = LlmPostProcessingProvider.Groq,
            LlmGroqApiKey = "groq-key",
        };
        const string rawTranscript = "raw transcript";

        // Act
        var result = await controller.ApplyLlmPostProcessingWithFallbackAsync(rawTranscript, settings, CancellationToken.None);

        // Assert
        result.Should().Be(rawTranscript);
        notifications.Notifications.Should().ContainSingle().Which.Should().Be(
            new Notification("Groq post-processing failed", "Error: Slow down. Inserting the raw transcript instead.", true));
        statuses.Should().Contain(status =>
            status.State == DictationState.Transcribing &&
            status.Message == "Groq post-processing failed. Inserting the raw transcript instead." &&
            status.CanCancel);
    }

    [Fact]
    public async Task ApplyLlmPostProcessingWithFallbackAsync_WhenCancellationIsRequested_ThrowsWithoutFallbackNotification()
    {
        // Arrange
        var notifications = new FakeNotificationService();
        var controller = CreateController(new CancellationAwareHttpMessageHandler(), notifications);
        var settings = new AppSettings
        {
            LlmPostProcessingEnabled = true,
            LlmPostProcessingProvider = LlmPostProcessingProvider.Cerebras,
            CerebrasApiKey = "cerebras-key",
        };
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Act
        var act = () => controller.ApplyLlmPostProcessingWithFallbackAsync("raw transcript", settings, cancellationTokenSource.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        notifications.Notifications.Should().BeEmpty();
    }

    private static DictationController CreateController(HttpMessageHandler handler, FakeNotificationService notifications)
    {
        return new DictationController(
            new FakeAppSettingsStore(),
            new FakeAudioDeviceService(),
            new FakeTranscriptionClientFactory(),
            new FakeTextInsertionService(),
            new FakeTranscriptHistoryStore(),
            notifications,
            new LlmTranscriptPostProcessor(new HttpClient(handler)),
            new DeepgramStreamingTranscriptionClient(),
            new MistralRealtimeTranscriptionClient());
    }

    private sealed class StaticResponseHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StaticResponseHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }

    private sealed class CancellationAwareHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FakeAppSettingsStore : IAppSettingsStore
    {
        public AppSettings CurrentSettings { get; private set; } = new();

        public Task<AppSettings> LoadAsync(bool forceReload = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CurrentSettings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            CurrentSettings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAudioDeviceService : IAudioDeviceService
    {
        public IReadOnlyList<AudioInputDevice> GetInputDevices()
        {
            return [];
        }

        public MMDevice OpenPreferredCaptureDevice(string? selectedDeviceId)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class FakeTranscriptionClientFactory : ITranscriptionClientFactory
    {
        public ITranscriptionClient GetClient(TranscriptionProvider provider)
        {
            return new FakeTranscriptionClient();
        }
    }

    private sealed class FakeTranscriptionClient : ITranscriptionClient
    {
        public Task<string> TranscribeAsync(
            byte[] audioBytes,
            string apiKey,
            string model,
            string? language,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult("raw transcript");
        }
    }

    private sealed class FakeTextInsertionService : ITextInsertionService
    {
        public Task InsertTextAsync(
            string text,
            HotkeyBinding? triggeringHotkey = null,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTranscriptHistoryStore : ITranscriptHistoryStore
    {
        public event EventHandler? HistoryChanged;

        public Task AppendAsync(string transcript, int maxEntries, CancellationToken cancellationToken = default)
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranscriptHistoryEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TranscriptHistoryEntry>>([]);
        }

        public Task<string?> GetLatestTranscriptAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task DeleteAsync(string entryId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task EnforceRetentionAsync(int maxEntries, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public List<Notification> Notifications { get; } = [];

        public void AttachTrayIconService(TrayIconService trayIconService)
        {
        }

        public void ShowNotification(string title, string message, bool isError)
        {
            Notifications.Add(new Notification(title, message, isError));
        }
    }

    private sealed record Notification(string Title, string Message, bool IsError);
}
