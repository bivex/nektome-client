using NektoMe.Application.Services;

namespace NektoMe.Application.Tests;

public sealed class AudioDynamicsProcessorTests
{
    private static short[] Loud(int samples = 480) =>
        Enumerable.Repeat((short)20000, samples).ToArray();

    private static short[] Silence(int samples = 480) => new short[samples];

    private static float Peak(short[] pcm)
    {
        short peak = 0;
        foreach (short s in pcm)
        {
            if (Math.Abs(s) > peak)
            {
                peak = Math.Abs(s);
            }
        }
        return peak;
    }

    [Fact]
    public void Mic_is_ducked_while_speaker_hold_is_active()
    {
        var speaker = new AudioDynamicsProcessor { IsMicrophone = false };
        var mic = new AudioDynamicsProcessor { IsMicrophone = true };

        // A loud speaker burst arms the shared suppression hold.
        speaker.Process(Loud());

        short[] pcm = Loud();
        mic.Process(pcm);
        Assert.True(Peak(pcm) < 1000, $"expected ducked mic, got peak {Peak(pcm)}");
    }

    [Fact]
    public void Mic_recovers_after_speaker_hold_expires()
    {
        var speaker = new AudioDynamicsProcessor { IsMicrophone = false };
        var mic = new AudioDynamicsProcessor { IsMicrophone = true };

        speaker.Process(Loud());

        // Hold is 250ms; after it lapses the mic must pass through at full level.
        Thread.Sleep(300);

        short[] pcm = Loud();
        mic.Process(pcm);
        Assert.True(Peak(pcm) > 10000, $"expected recovered mic, got peak {Peak(pcm)}");
    }

    [Fact]
    public void Mic_recovers_after_speaker_speech_stops()
    {
        var speaker = new AudioDynamicsProcessor { IsMicrophone = false };
        var mic = new AudioDynamicsProcessor { IsMicrophone = true };

        // Continuous speech keeps refreshing the hold...
        speaker.Process(Loud());
        short[] ducked = Loud();
        mic.Process(ducked);
        Assert.True(Peak(ducked) < 1000);

        // ...then the speaker goes quiet: once the hold lapses the mic comes back
        // (regression: the old envelope latch kept the mic ducked forever).
        speaker.Process(Silence());
        Thread.Sleep(300);
        short[] recovered = Loud();
        mic.Process(recovered);
        Assert.True(Peak(recovered) > 10000, $"expected recovered mic, got peak {Peak(recovered)}");
    }

    [Fact]
    public void Disabling_echo_cancellation_leaves_mic_untouched()
    {
        var speaker = new AudioDynamicsProcessor { IsMicrophone = false };
        var mic = new AudioDynamicsProcessor { IsMicrophone = true, EchoCancellationEnabled = false };

        speaker.Process(Loud());

        short[] pcm = Loud();
        mic.Process(pcm);
        Assert.True(Peak(pcm) > 10000, $"expected full mic level, got peak {Peak(pcm)}");
    }
}
