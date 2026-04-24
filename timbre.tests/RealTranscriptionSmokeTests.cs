using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using timbre.Interfaces;
using timbre.Models;
using timbre.Services;
using Xunit.Abstractions;

namespace timbre.tests.Services;

[ExcludeFromCodeCoverage]
public sealed class RealTranscriptionSmokeTests
{
    private readonly ITestOutputHelper _output;

    public RealTranscriptionSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConfiguredProviders_TranscribeFixtureAudio()
    {
        var configuration = SmokeTestConfiguration.Load();
        if (!File.Exists(configuration.AudioPath))
        {
            _output.WriteLine($"Smoke test audio file was not found at '{configuration.AudioPath}'.");
            return;
        }

        var configuredProviders = GetConfiguredProviders(configuration).ToList();
        if (configuredProviders.Count == 0)
        {
            _output.WriteLine("No provider API keys were found in .env or the current process environment.");
            return;
        }

        var audioBytes = await File.ReadAllBytesAsync(configuration.AudioPath);
        audioBytes.Should().NotBeEmpty();

        var failures = new List<string>();

        foreach (var configuredProvider in configuredProviders)
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(3),
            };
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            try
            {
                var transcript = await configuredProvider.CreateClient(httpClient).TranscribeAsync(
                    audioBytes,
                    configuredProvider.ApiKey!,
                    configuredProvider.Model,
                    configuredProvider.Language,
                    cancellationTokenSource.Token);

                transcript.Should().NotBeNullOrWhiteSpace();
                _output.WriteLine($"{configuredProvider.Provider}: {Shorten(transcript)}");
            }
            catch (Exception exception)
            {
                failures.Add($"{configuredProvider.Provider}: {exception.GetType().Name}: {exception.Message}");
                _output.WriteLine($"{configuredProvider.Provider} failed: {exception}");
            }
        }

        failures.Should().BeEmpty("configured providers should accept the fixture audio and return a transcript");
    }

    private static IEnumerable<ConfiguredProvider> GetConfiguredProviders(SmokeTestConfiguration configuration)
    {
        foreach (var configuredProvider in GetProviderCandidates(configuration))
        {
            if (!string.IsNullOrWhiteSpace(configuredProvider.ApiKey))
            {
                yield return configuredProvider;
            }
        }
    }

    private static IEnumerable<ConfiguredProvider> GetProviderCandidates(SmokeTestConfiguration configuration)
    {
        yield return new ConfiguredProvider(
            TranscriptionProvider.Groq,
            configuration.GetSecret("GROQ_API_KEY"),
            TranscriptionModelCatalog.DefaultGroqModel,
            null,
            httpClient => new GroqTranscriptionClient(httpClient));

        yield return new ConfiguredProvider(
            TranscriptionProvider.Fireworks,
            configuration.GetSecret("FIREWORKS_API_KEY"),
            TranscriptionModelCatalog.DefaultFireworksModel,
            null,
            httpClient => new FireworksTranscriptionClient(httpClient));

        yield return new ConfiguredProvider(
            TranscriptionProvider.Deepgram,
            configuration.GetSecret("DEEPGRAM_API_KEY"),
            TranscriptionModelCatalog.DefaultDeepgramNonStreamingModel,
            "en",
            httpClient => new DeepgramTranscriptionClient(httpClient));

        yield return new ConfiguredProvider(
            TranscriptionProvider.Mistral,
            configuration.GetSecret("MISTRAL_API_KEY"),
            TranscriptionModelCatalog.DefaultMistralNonStreamingModel,
            null,
            httpClient => new MistralTranscriptionClient(httpClient));

        yield return new ConfiguredProvider(
            TranscriptionProvider.Cohere,
            configuration.GetSecret("COHERE_API_KEY"),
            TranscriptionModelCatalog.DefaultCohereModel,
            "en",
            httpClient => new CohereTranscriptionClient(httpClient));

        yield return new ConfiguredProvider(
            TranscriptionProvider.ElevenLabs,
            configuration.GetSecret("ELEVENLABS_API_KEY"),
            TranscriptionModelCatalog.DefaultElevenLabsNonStreamingModel,
            null,
            httpClient => new ElevenLabsTranscriptionClient(httpClient));
    }

    private static string Shorten(string value)
    {
        var singleLine = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 160
            ? singleLine
            : $"{singleLine[..157]}...";
    }

    private sealed record ConfiguredProvider(
        TranscriptionProvider Provider,
        string? ApiKey,
        string Model,
        string? Language,
        Func<HttpClient, ITranscriptionClient> CreateClient);

    private sealed class SmokeTestConfiguration
    {
        private readonly IReadOnlyDictionary<string, string> _dotenvValues;

        private SmokeTestConfiguration(string repoRoot, IReadOnlyDictionary<string, string> dotenvValues)
        {
            RepoRoot = repoRoot;
            _dotenvValues = dotenvValues;
            AudioPath = Path.Combine(repoRoot, "TEST-AUDIO.wav");
        }

        public string RepoRoot { get; }

        public string AudioPath { get; }

        public static SmokeTestConfiguration Load()
        {
            var repoRoot = FindRepoRoot();
            var dotenvPath = Path.Combine(repoRoot, ".env");
            var dotenvValues = File.Exists(dotenvPath)
                ? ParseDotEnvFile(dotenvPath)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            return new SmokeTestConfiguration(repoRoot, dotenvValues);
        }

        public string? GetSecret(string key)
        {
            var environmentValue = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                return environmentValue.Trim();
            }

            return _dotenvValues.TryGetValue(key, out var dotenvValue) && !string.IsNullOrWhiteSpace(dotenvValue)
                ? dotenvValue.Trim()
                : null;
        }

        private static string FindRepoRoot()
        {
            var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

            while (currentDirectory is not null)
            {
                if (File.Exists(Path.Combine(currentDirectory.FullName, "timbre.sln")))
                {
                    return currentDirectory.FullName;
                }

                currentDirectory = currentDirectory.Parent;
            }

            throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
        }

        private static Dictionary<string, string> ParseDotEnvFile(string dotenvPath)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in File.ReadLines(dotenvPath))
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.Length == 0 || trimmedLine.StartsWith('#'))
                {
                    continue;
                }

                var separatorIndex = trimmedLine.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = trimmedLine[..separatorIndex].Trim();
                var value = trimmedLine[(separatorIndex + 1)..].Trim();
                values[key] = TrimMatchingQuotes(value);
            }

            return values;
        }

        private static string TrimMatchingQuotes(string value)
        {
            if (value.Length >= 2)
            {
                var firstCharacter = value[0];
                var lastCharacter = value[^1];

                if ((firstCharacter == '"' && lastCharacter == '"') ||
                    (firstCharacter == '\'' && lastCharacter == '\''))
                {
                    return value[1..^1];
                }
            }

            return value;
        }
    }
}
