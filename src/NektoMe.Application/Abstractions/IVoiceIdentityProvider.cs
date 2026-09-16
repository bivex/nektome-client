namespace NektoMe.Application.Abstractions;

/// <summary>
/// Stable user id for the voice service ("&lt;store&gt;-&lt;id&gt;" in the reference
/// client). Persisted across runs so the server recognizes the client.
/// </summary>
public interface IVoiceIdentityProvider
{
    string GetUserId();

    /// <summary>
    /// Last connection id the server handed out, kept across runs like the
    /// reference client's <c>connectionIdLast</c> (re-sent with every register).
    /// </summary>
    string? GetLastConnectionId();

    /// <summary>Stores the connection id returned by <c>registered</c> (best effort).</summary>
    void SaveLastConnectionId(string connectionId);

    /// <summary>Mints a fresh identity and discards the stored connection id.</summary>
    string ResetIdentity(string? prefix = null);

    /// <summary>Explicitly sets the user id and discards the stored connection id.</summary>
    void SetUserId(string userId);

    /// <summary>Explicitly sets the user id and optional connection id for session continuity.</summary>
    void SetIdentity(string userId, string? connectionId = null);

    /// <summary>Gets the persistent Web audiochat token (32 hex characters).</summary>
    string GetWebToken();

    /// <summary>Mints a new random Web audiochat token.</summary>
    string ResetWebToken();

    /// <summary>Explicitly sets the Web audiochat token.</summary>
    void SetWebToken(string token);
}

/// <summary>Resolved signaling-server address (from <c>nekto.me/audioserver.json</c>).</summary>
public sealed record AudioServerEndpoint(Uri Url, string Path);

/// <summary>Driven port resolving where the voice signaling socket lives.</summary>
public interface IAudioServerEndpointResolver
{
    Task<AudioServerEndpoint> ResolveAsync(CancellationToken cancellationToken = default);
}
