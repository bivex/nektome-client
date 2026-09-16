using NektoMe.Application.Abstractions;

namespace NektoMe.Infrastructure.Media;

/// <summary>Microphone source for platforms without a capture backend: never captures, never fails.</summary>
public sealed class NullMicrophoneCapture : IMicrophoneCapture
{
    public static NullMicrophoneCapture Instance { get; } = new();

    public bool IsCapturing => false;

    public event Action<short[], int>? PcmCaptured { add { } remove { } }

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}
