using NektoMe.Domain;

namespace NektoMe.Application.Events;

/// <summary>Base for everything the session publishes to consumers (UI, loggers, …).</summary>
public abstract record ChatEvent(DateTimeOffset At);

public sealed record ConnectionChanged(bool Connected, string? Reason) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record ReconnectingStarted() : ChatEvent(DateTimeOffset.UtcNow);

public sealed record TransportFailed(string Message) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record Authenticated(long? UserId, BanStatus Ban, AppConfig? Config) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record ErrorReceived(ChatError Error) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record DialogOpened(Dialog Dialog) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record DialogClosed(DialogId DialogId) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record MessageReceived(Message Message, bool OwnMessage) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record MessagesRead(DialogId DialogId, IReadOnlyList<long> MessageIds) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record TypingChanged(DialogId DialogId, bool Typing, bool Voice) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record SearchStateChanged(bool Searching) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record OnlineCountReceived(int InChats, int InSearch) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record CaptchaRequired(string? Solution) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record DialogPaid(DialogId DialogId, bool Paid) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record PurchaseChanged(int? PaidType) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record ServerRequestedClose(string? Reason) : ChatEvent(DateTimeOffset.UtcNow);

public sealed record RawNotice(string Name, int? Badge, int? Page) : ChatEvent(DateTimeOffset.UtcNow);
