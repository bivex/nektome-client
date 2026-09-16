namespace NektoMe.Domain;

/// <summary>A single message inside an anonymous dialog.</summary>
public sealed class Message
{
    public Message(
        MessageId id,
        DialogId dialogId,
        UserId senderId,
        string text,
        DateTimeOffset createdAt,
        long randomId,
        bool isRead = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Message text must not be empty.", nameof(text));
        }

        Id = id;
        DialogId = dialogId;
        SenderId = senderId;
        Text = text;
        CreatedAt = createdAt;
        RandomId = randomId;
        IsRead = isRead;
    }

    public MessageId Id { get; }

    public DialogId DialogId { get; }

    public UserId SenderId { get; }

    public string Text { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>Client-generated correlation id; the server echoes it back on delivery.</summary>
    public long RandomId { get; }

    public bool IsRead { get; private set; }

    public void MarkRead() => IsRead = true;
}
