using System.Text.Json;
using System.Text.Json.Serialization;
using NektoMe.Application;
using NektoMe.Application.Abstractions;

namespace NektoMe.Infrastructure.Transports;

/// <summary>
/// Resolves the signaling-server address. A static endpoint from options wins
/// (the current reference setup); otherwise it falls back to the legacy
/// bootstrap document <c>{ "socket": bool, "url": …, "path": … }</c>.
/// </summary>
public sealed class HttpAudioServerEndpointResolver : IAudioServerEndpointResolver, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly VoiceOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public HttpAudioServerEndpointResolver(VoiceOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
    }

    public async Task<AudioServerEndpoint> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (_options.ProtocolMode == VoiceProtocolMode.Web)
        {
            return _options.WebEndpoint;
        }

        if (_options.StaticEndpoint is { } staticEndpoint)
        {
            return staticEndpoint;
        }

        string bootstrapUrl = _options.BootstrapUrl
            ?? throw new InvalidOperationException(
                "No static audio-server endpoint is configured and the legacy bootstrap URL is unset.");

        using HttpResponseMessage response = await _httpClient
            .GetAsync(bootstrapUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream body = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        BootstrapDocument? document = await JsonSerializer
            .DeserializeAsync<BootstrapDocument>(body, Json, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The audio-server bootstrap document was empty.");

        if (!document.Socket || string.IsNullOrWhiteSpace(document.Url))
        {
            throw new InvalidOperationException("The audio-server bootstrap marks the voice socket as disabled.");
        }

        return new AudioServerEndpoint(new Uri(document.Url), document.Path ?? "/audiochat");
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed class BootstrapDocument
    {
        [JsonPropertyName("socket")]
        public bool Socket { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }
    }
}
