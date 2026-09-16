using NektoMe.Application.Services;

namespace NektoMe.Application.Tests;

public sealed class SpeechAgcProcessorTests
{
    private const int SampleRate = 8000;
    private const int BlockSize = 160; // 20ms, the usual capture block

    private static short[] SineBlock(float amplitude, float frequency = 440f)
    {
        var block = new short[BlockSize];
        for (int i = 0; i < block.Length; i++)
        {
            block[i] = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * frequency * i / SampleRate));
        }
        return block;
    }

    private static double Rms(short[] pcm)
    {
        double sum = 0;
        for (int i = 0; i < pcm.Length; i++)
        {
            sum += (double)pcm[i] * pcm[i];
        }
        return Math.Sqrt(sum / pcm.Length);
    }

    private static float Peak(short[] pcm)
    {
        float peak = 0;
        for (int i = 0; i < pcm.Length; i++)
        {
            float abs = Math.Abs(pcm[i]);
            if (abs > peak) peak = abs;
        }
        return peak;
    }

    [Fact]
    public void Quiet_speech_is_lifted_toward_the_send_target()
    {
        // -38 dBFS sine: exactly the "they can't hear me" territory.
        // Fresh buffer per call, like real capture callbacks deliver.
        var agc = new SpeechAgcProcessor(SampleRate);
        for (int i = 0; i < 100; i++)
        {
            agc.Process(SineBlock(600f));
        }

        short[] block = SineBlock(600f);
        agc.Process(block);

        // Converged gain should bring ~0.013 RMS near the 0.08 target.
        double rms = Rms(block);
        Assert.InRange(rms, 0.04 * 32768, 0.16 * 32768);
        Assert.True(agc.CurrentGain > 2f);
    }

    [Fact]
    public void Noise_floor_stays_attenuated_during_pauses()
    {
        var agc = new SpeechAgcProcessor(SampleRate);
        short[] noise = SineBlock(15f, 3000f); // quiet ambient hiss
        for (int i = 0; i < 200; i++)
        {
            agc.Process(noise);
        }

        // The boosted hiss must not reach the peer: deep gate attenuation.
        Assert.True(Rms(noise) < 2.0);
        Assert.True(Peak(noise) < 10);
    }

    [Fact]
    public void Loud_speech_passes_at_unity_gain_without_clipping()
    {
        var agc = new SpeechAgcProcessor(SampleRate);
        short[] block = SineBlock(20000f);
        double inputRms = Rms(block);
        for (int i = 0; i < 60; i++)
        {
            agc.Process(block);
        }

        // Never attenuate: loud speech already exceeds the target.
        Assert.Equal(inputRms, Rms(block), 1);
        Assert.True(Peak(block) <= 0.98f * 32767);
        Assert.Equal(1f, agc.CurrentGain, 1);
    }

    [Fact]
    public void Disabled_processor_passes_audio_untouched()
    {
        var agc = new SpeechAgcProcessor(SampleRate) { IsEnabled = false };
        short[] block = SineBlock(600f);
        short[] expected = (short[])block.Clone();

        agc.Process(block);

        Assert.Equal(expected, block);
    }
}
