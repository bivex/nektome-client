namespace NektoMe.Domain;

public enum DialogStatus
{
    Open,
    Closed,
}

/// <summary>
/// Aggregate root for an anonymous dialog between two participants.
/// Raises in-process events so application services can react without polling.
/// </summary>
public sealed class Dialog
{
    private readonly List<Message> _messages = [];

    public Dialog(
        DialogId id,
        IReadOnlyList<UserId> interlocutors,
        bool supportsVoice = false,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        Id = id;
        Interlocutors = interlocutors;
        SupportsVoice = supportsVoice;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public DialogId Id { get; }

    public IReadOnlyList<UserId> Interlocutors { get; }

    public bool SupportsVoice { get; }

    public DateTimeOffset? CreatedAt { get; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public DialogStatus Status { get; private set; } = DialogStatus.Open;

    public IReadOnlyList<Message> Messages => _messages;

    /// <summary>Raised for every message accepted into this dialog.</summary>
    public event Action<Message>? MessageAdded;

    /// <summary>Raised with the ids of messages transitioned to the read state.</summary>
    public event Action<DialogId, IReadOnlyList<long>>? MessagesMarkedRead;

    /// <summary>
    /// Appends a message to the dialog. Returns false if the dialog is closed
    /// (a late server push after <see cref="Close"/>) — the caller decides
    /// whether that is worth surfacing.
    /// </summary>
    public bool TryAddMessage(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.DialogId != Id)
        {
            throw new ArgumentException(
                $"Message dialog {message.DialogId.Value} does not match dialog {Id.Value}.",
                nameof(message));
        }

        if (Status == DialogStatus.Closed)
        {
            return false;
        }

        _messages.Add(message);
        UpdatedAt = message.CreatedAt;
        MessageAdded?.Invoke(message);
        return true;
    }

    /// <summary>Marks every message with id ≤ <paramref name="upToMessageId"/> as read.</summary>
    public IReadOnlyList<long> MarkRead(MessageId upToMessageId)
    {
        List<long> newlyRead = [];
        foreach (var message in _messages)
        {
            if (!message.IsRead && message.Id.Value <= upToMessageId.Value)
            {
                message.MarkRead();
                newlyRead.Add(message.Id.Value);
            }
        }

        if (newlyRead.Count > 0)
        {
            MessagesMarkedRead?.Invoke(Id, newlyRead);
        }

        return newlyRead;
    }

    public void Close()
    {
        if (Status == DialogStatus.Open)
        {
            Status = DialogStatus.Closed;
        }
    }
}
