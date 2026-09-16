namespace NektoMe.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Generates client-side correlation ids for outgoing messages.</summary>
public interface IRandomIdGenerator
{
    long Next();
}
