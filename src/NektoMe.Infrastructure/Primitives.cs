using NektoMe.Application.Abstractions;

namespace NektoMe.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Positive random ids, mirroring the Java client's UUID-based correlation values.</summary>
public sealed class RandomIdGenerator : IRandomIdGenerator
{
    public long Next() => Math.Abs(Random.Shared.NextInt64());
}
