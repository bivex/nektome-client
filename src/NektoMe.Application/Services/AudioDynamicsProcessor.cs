using System;

namespace NektoMe.Application.Services;

/// <summary>
/// A real-time DSP processor providing a Noise Gate, Compressor, and Make-up Gain.
/// </summary>
public sealed class AudioDynamicsProcessor
{
    // Thresholds
    public float NoiseGateThreshold { get; set; } = SettingsService.Load().NoiseGateThreshold; // ~ -46 dBFS
    public float CompressorThreshold { get; set; } = SettingsService.Load().CompressorThreshold;  // ~ -10 dBFS
    public float CompressorRatio { get; set; } = SettingsService.Load().CompressorRatio;      // 4:1 compression
    public float MakeupGain { get; set; } = SettingsService.Load().MakeupGain;           // +6 dB boost

    public bool IsEnabled { get; set; } = true;

    private float _envelope = 0f;

    // Time constants for 48kHz by default, but we'll calculate per-sample.
    // We want fast attack (e.g., 2ms) and slow release (e.g., 100ms)
    private float _attackCoef;
    private float _releaseCoef;

    public AudioDynamicsProcessor(int sampleRate = 48000)
    {
        SetSampleRate(sampleRate);
    }

    public void SetSampleRate(int sampleRate)
    {
        // alpha = exp(-1.0 / (time_sec * sample_rate))
        _attackCoef = (float)Math.Exp(-1.0 / (0.002 * sampleRate)); // 2ms attack
        _releaseCoef = (float)Math.Exp(-1.0 / (0.100 * sampleRate)); // 100ms release
    }

    public void Process(short[] pcm)
    {
        if (!IsEnabled)
            return;

        const float maxSample = 32768f;
        float attack = _attackCoef;
        float release = _releaseCoef;
        float noiseGateThreshold = NoiseGateThreshold;
        float compThreshold = CompressorThreshold;
        float compRatio = CompressorRatio;
        float makeupGain = MakeupGain;

        for (int i = 0; i < pcm.Length; i++)
        {
            float sample = pcm[i] / maxSample;
            float absSample = Math.Abs(sample);

            // Envelope detection (peak)
            if (absSample > _envelope)
                _envelope = attack * _envelope + (1 - attack) * absSample;
            else
                _envelope = release * _envelope + (1 - release) * absSample;

            float gain = 1.0f;

            // 1. Noise Gate (soft knee downward expander)
            if (_envelope < noiseGateThreshold)
            {
                // Smooth fade out below threshold (e.g. from noiseGateThreshold down to 0)
                float gateGain = _envelope / noiseGateThreshold;
                gain *= gateGain * gateGain; // squaring makes it a smoother curve
            }

            // 2. Compressor (downward compression)
            if (_envelope > compThreshold)
            {
                float overshoot = _envelope - compThreshold;
                float compressedOvershoot = overshoot / compRatio;
                float targetGain = (compThreshold + compressedOvershoot) / _envelope;
                if (targetGain < gain)
                {
                    gain = targetGain;
                }
            }

            // Apply gain and makeup gain
            sample *= gain * makeupGain;

            // Hard clip to avoid integer overflow clicking
            if (sample > 0.999f) sample = 0.999f;
            else if (sample < -0.999f) sample = -0.999f;

            pcm[i] = (short)(sample * maxSample);
        }
    }
}
