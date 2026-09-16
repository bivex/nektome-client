namespace NektoMe.Domain;

/// <summary>Identifier of an anonymous chat dialog (server-assigned long).</summary>
public readonly record struct DialogId(long Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a chat participant (server-assigned long).</summary>
public readonly record struct UserId(long Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier of a message (server-assigned long).</summary>
public readonly record struct MessageId(long Value)
{
    public override string ToString() => Value.ToString();
}
