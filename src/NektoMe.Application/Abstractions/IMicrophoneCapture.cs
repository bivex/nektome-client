namespace NektoMe.Application.Abstractions;

/// <summary>
/// Driven port for live microphone capture. Implementations start/stop an
/// OS capture pipeline and push captured PCM to subscribers from their own
/// threads. Sample rate is whatever the backend captures at; the audio
/// engine down-converts when needed.
/// </summary>
public interface IMicrophoneCapture : IDisposable
{
    /// <summary>True while the capture pipeline is running.</summary>
    bool IsCapturing { get; }

    /// <summary>Raised per captured buffer: (samples, sampleRate).</summary>
    event Action<short[], int>? PcmCaptured;

    /// <summary>Starts capturing; repeated calls are no-ops.</summary>
    void Start();

    /// <summary>Stops capturing; safe to call when not capturing.</summary>
    void Stop();
}
