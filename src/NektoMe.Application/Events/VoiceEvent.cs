namespace NektoMe.Application.Events;

/// <summary>Base for everything the voice session publishes to consumers.</summary>
public abstract record VoiceChatEvent(DateTimeOffset At);

public sealed record VoiceConnectionChanged(bool Connected, string? Reason) : VoiceChatEvent(DateTimeOffset.UtcNow);

public sealed record VoiceReconnectingStarted() : VoiceChatEvent(DateTimeOffset.UtcNow);

public sealed record VoiceTransportFailed(string Message) : VoiceChatEvent(DateTimeOffset.UtcNow);

/// <summary>Server accepted (or refused) our registration.</summary>
public sealed record VoiceRegistered(
    bool Success,
    string? ConnectionId,
    int? ErrorCode,
    string? RecaptchaSiteKey) : VoiceChatEvent(DateTimeOffset.UtcNow);

public sealed record VoiceSearchStateChanged(bool Searching) : VoiceChatEvent(DateTimeOffset.UtcNow);

/// <summary>Server matched us with a peer and handed over WebRTC parameters.</summary>
public sealed record VoicePeerFound(string ConnectionId, bool Initiator, bool Relay) : VoiceChatEvent(DateTimeOffset.UtcNow);

/// <summary>The peer finished building its side of the connection.</summary>
public sealed record VoicePeerConnected(string ConnectionId) : VoiceChatEvent(DateTimeOffset.UtcNow);

/// <summary>Remote audio is flowing; <see cref="Talking"/> becomes true for the session lifetime.</summary>
public sealed record VoiceMediaEstablished(string ConnectionId) : VoiceChatEvent(DateTimeOffset.UtcNow);

/// <summary>Peer left. <paramref name="Soft"/> marks the resume-window variant.</summary>
public sealed record VoicePeerGone(string ConnectionId, bool Soft) : VoiceChatEvent(DateTimeOffset.UtcNow);

public sealed record VoicePeerMuteChanged(bool Muted) : VoiceChatEvent(DateTimeOffset.UtcNow);

public sealed record VoiceUsersCount(int Users, int Waiting, int Talking) : VoiceChatEvent(DateTimeOffset.UtcNow);

/// <summary>Server demands a captcha solution before further searching.</summary>
public sealed record VoiceCaptchaRequested(
    string? CaptchaType,
    string? Url,
    int LeftChats,
    int NeedChats) : VoiceChatEvent(DateTimeOffset.UtcNow);

public sealed record VoiceBanned(
    string? BanEnum,
    bool Permanently,
    string? Text,
    string? Detail) : VoiceChatEvent(DateTimeOffset.UtcNow);

public sealed record VoiceErrorReceived(string? Code, string? Description) : VoiceChatEvent(DateTimeOffset.UtcNow);

public sealed record VoiceMediaStateChanged(string State) : VoiceChatEvent(DateTimeOffset.UtcNow);

public sealed record VoicePurchaseChanged(int? PaidType) : VoiceChatEvent(DateTimeOffset.UtcNow);

/// <summary>Detailed signaling/media diagnostic message for logging.</summary>
public sealed record VoiceDiagnosticMessage(string Message) : VoiceChatEvent(DateTimeOffset.UtcNow);

/// <summary>Unrecognized or raw server event; surfaced for diagnostics.</summary>
public sealed record VoiceRawEvent(string Type, string? Payload = null) : VoiceChatEvent(DateTimeOffset.UtcNow);
