using NektoMe.Application.Services;

namespace NektoMe.Application.Tests;

public sealed class AudioDynamicsProcessorTests
{
    private const int Block = 480; // 10ms @ 48kHz

    private static short[] Loud(int samples = Block) =>
        Enumerable.Repeat((short)20000, samples).ToArray();

    private static short[] Silence(int samples = Block) => new short[samples];

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

    /// <summary>Feeds N blocks of loud audio through the mic processor.</summary>
    private static float Pump(AudioDynamicsProcessor mic, int blocks)
    {
        float peak = 0;
        for (int i = 0; i < blocks; i++)
        {
            short[] pcm = Loud();
            mic.Process(pcm);
            float blockPeak = Peak(pcm);
            if (blockPeak > peak)
            {
                peak = blockPeak;
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

        // The first block contains the ~2ms fade-in of the ramp; by the second
        // block the gain is fully ducked.
        mic.Process(Loud());
        short[] pcm = Loud();
        mic.Process(pcm);
        float peak = Peak(pcm);
        Assert.True(peak < 1500, $"expected ducked mic, got peak {peak}");
    }

    [Fact]
    public void Mic_recovers_after_speaker_hold_expires()
    {
        var speaker = new AudioDynamicsProcessor { IsMicrophone = false };
        var mic = new AudioDynamicsProcessor { IsMicrophone = true };

        speaker.Process(Loud());

        // Hold is 250ms; after it lapses the release ramp (80ms) must bring the
        // mic back to full level within a few blocks.
        Thread.Sleep(300);
        float peak = Pump(mic, 30);
        Assert.True(peak > 10000, $"expected recovered mic, got peak {peak}");
    }

    [Fact]
    public void Mic_recovers_after_speaker_speech_stops()
    {
        var speaker = new AudioDynamicsProcessor { IsMicrophone = false };
        var mic = new AudioDynamicsProcessor { IsMicrophone = true };

        // Continuous speech keeps refreshing the hold...
        speaker.Process(Loud());
        mic.Process(Loud()); // ramp fade-in block
        short[] ducked = Loud();
        mic.Process(ducked);
        Assert.True(Peak(ducked) < 1500);

        // ...then the speaker goes quiet: once the hold lapses the mic comes back
        // (regression: the old envelope latch kept the mic ducked forever).
        speaker.Process(Silence());
        Thread.Sleep(300);
        float peak = Pump(mic, 30);
        Assert.True(peak > 10000, $"expected recovered mic, got peak {peak}");
    }

    [Fact]
    public void Ducking_ramp_does_not_click_between_blocks()
    {
        var speaker = new AudioDynamicsProcessor { IsMicrophone = false };
        var mic = new AudioDynamicsProcessor { IsMicrophone = true };

        // Duck the mic first so the release ramp actually starts from ~0.01.
        speaker.Process(Loud());
        mic.Process(Loud());
        short[] ducked = Loud();
        mic.Process(ducked);
        float previousLast = ducked[^1];
        Thread.Sleep(300);

        // Feeding constant full-scale input through the release ramp, consecutive
        // blocks may differ but each block's internal spread must stay small
        // relative to full scale — a hard on/off switch would produce ~20000 steps.
        for (int i = 0; i < 10; i++)
        {
            short[] pcm = Loud();
            mic.Process(pcm);
            float spread = Math.Abs(pcm[^1] - pcm[0]);
            Assert.True(spread < 8000, $"block {i} shows a hard step: {spread}");
            Assert.True(Math.Abs(pcm[0] - previousLast) < 8000, $"click at block {i} boundary");
            previousLast = pcm[^1];
        }
    }

    [Fact]
    public void Disabling_echo_cancellation_leaves_mic_untouched()
    {
        var speaker = new AudioDynamicsProcessor { IsMicrophone = false };
        var mic = new AudioDynamicsProcessor { IsMicrophone = true, EchoCancellationEnabled = false };

        speaker.Process(Loud());

        short[] pcm = Loud();
        mic.Process(pcm);
        float peak = Peak(pcm);
        Assert.True(peak > 10000, $"expected full mic level, got peak {peak}");
    }
}
