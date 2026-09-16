using NektoMe.Application.Abstractions;

namespace NektoMe.Infrastructure.Media;

/// <summary>
/// Picks the live playback sink for the current platform. Returns null where
/// no backend exists — remote audio then flows nowhere, which the session
/// treats the same as "no speakers plugged in".
/// </summary>
public static class AudioSinkFactory
{
    public static IAudioSink? CreateDefaultPlaybackSink() =>
        OperatingSystem.IsMacOS() ? new MacAudioQueueSink() : null;

    /// <summary>
    /// Picks the live microphone source for the current platform. Never
    /// returns null — platforms without a capture backend get a silent no-op
    /// source so the session wiring stays uniform.
    /// </summary>
    public static IMicrophoneCapture CreateDefaultMicrophoneSource() =>
        OperatingSystem.IsMacOS() ? new MacAudioQueueMicSource() : NullMicrophoneCapture.Instance;
}
