using System;

namespace NektoMe.Application.Services;

/// <summary>
/// Speech-adaptive automatic gain control for the microphone send path.
///
/// Quiet laptop mics deliver speech at -35..-45 dBFS, which is near-silent for
/// the peer. This stage tracks the noise floor, detects speech by SNR, and
/// adapts a gain that lifts speech toward a healthy send level while keeping
/// the boosted noise floor heavily attenuated during pauses.
/// </summary>
public sealed class SpeechAgcProcessor
{
    public bool IsEnabled { get; set; } = true;

    /// <summary>Send target for speech, linear amplitude (~ -22 dBFS).</summary>
    public float TargetRms { get; set; } = 0.08f;

    /// <summary>Boost ceiling (+21 dB): quiet speech lifts, noise never explodes.</summary>
    public float MaxGain { get; set; } = 12f;

    /// <summary>Gain currently adapted for speech (1 = no boost), for diagnostics.</summary>
    public float CurrentGain => _gain;

    private float _gain = 1f;       // speech gain adapted toward the target
    private float _applied = 1f;    // smoothed per-sample gain actually applied
    private float _floor = 0.0005f; // noise-floor estimate, linear RMS

    private double _gateOpenCoef;   // per-sample 3ms attack on the first syllable
    private double _gateCloseCoef;  // per-sample 250ms release into a pause
    private int _sampleRate;

    public SpeechAgcProcessor(int sampleRate = 48000) => SetSampleRate(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = sampleRate;
        // Per-sample step factors for y += (target - y) * alpha:
        // alpha = 1 - exp(-1 / (time_sec * sample_rate)).
        _gateOpenCoef = 1.0 - Math.Exp(-1.0 / (0.003 * sampleRate));
        _gateCloseCoef = 1.0 - Math.Exp(-1.0 / (0.250 * sampleRate));
    }

    // Block-level step factor: alpha = 1 - exp(-samples / (time_sec * sample_rate)).
    private double Alpha(double timeSeconds, int samples) =>
        1.0 - Math.Exp(-samples / (timeSeconds * _sampleRate));

    public void Process(short[] pcm)
    {
        if (!IsEnabled || pcm.Length == 0)
        {
            return;
        }

        // Block RMS and peak (linear 0..1).
        double sumSquares = 0;
        float peak = 0;
        for (int i = 0; i < pcm.Length; i++)
        {
            float v = Math.Abs(pcm[i] / 32768f);
            sumSquares += (double)v * v;
            if (v > peak) peak = v;
        }
        float blockRms = (float)Math.Sqrt(sumSquares / pcm.Length);

        // Minimum-statistics noise floor: instant down, very slow up.
        if (blockRms < _floor)
        {
            _floor = blockRms;
        }
        else
        {
            _floor += (blockRms - _floor) * (float)Alpha(10.0, pcm.Length);
        }

        // Speech decision on the instantaneous block RMS: the level beats the
        // floor by ~9 dB SNR (plus an absolute floor so digital silence never
        // counts as speech). A lagging envelope here would misclassify the
        // first syllable after every pause.
        bool speech = blockRms > _floor * 2.8f && blockRms > 0.0008f;

        float target;
        if (speech)
        {
            // Lift quiet speech toward the send target; never attenuate (the
            // compressor downstream tames loud peaks) and never exceed MaxGain.
            float desired = Math.Clamp(TargetRms / Math.Max(blockRms, 1e-4f), 1f, MaxGain);
            _gain += (desired - _gain) * (float)Alpha(0.300, pcm.Length);
            // Peak safety: never let the boosted signal clip.
            float safeGain = 0.97f / Math.Max(peak, 0.001f);
            target = Math.Min(_gain, safeGain);
        }
        else
        {
            // Not speech: keep the adapted gain but heavily attenuate the
            // boosted noise floor instead of sending amplified hiss to the peer.
            target = 0.02f;
        }

        for (int i = 0; i < pcm.Length; i++)
        {
            _applied += (target - _applied) * (float)(target > _applied ? _gateOpenCoef : _gateCloseCoef);
            float sample = pcm[i] * _applied;
            pcm[i] = (short)Math.Clamp((int)Math.Round(sample), short.MinValue, short.MaxValue);
        }
    }
}
