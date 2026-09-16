namespace NektoMe.Application.Abstractions;

/// <summary>One measured round-trip result for a signaling-provided server.</summary>
public sealed record VoicePingSample(string Server, int RttMs, int Fails);

/// <summary>
/// Measures round-trip latency to servers the voice backend asks about
/// (<c>ping-server-request</c>). Mirrors the reference app: best (minimum) of
/// <paramref name="attempts"/> probes, <c>RttMs = -1</c> when a server never
/// answered.
/// </summary>
public interface IPingProber
{
    Task<IReadOnlyList<VoicePingSample>> ProbeAsync(
        IReadOnlyList<string> servers,
        int attempts,
        CancellationToken cancellationToken = default);
}
