namespace NektoMe.Infrastructure.Media;

/// <summary>
/// Managed G.711 codec (μ-law encode/decode plus A-law decode) so the engine
/// needs no native media backend. Sample domain is 16-bit signed PCM at 8 kHz.
/// </summary>
internal static class G711
{
    public const int SampleRate = 8000;
    public const int PcmuPayloadType = 0;
    public const int PcmaPayloadType = 8;

    public static byte[] EncodePcmu(short[] pcm)
    {
        var encoded = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            encoded[i] = LinearToPcmu(pcm[i]);
        }

        return encoded;
    }

    public static short[] Decode(int payloadType, byte[] payload)
    {
        bool isPcma = payloadType == PcmaPayloadType;
        var decoded = new short[payload.Length];
        for (int i = 0; i < payload.Length; i++)
        {
            decoded[i] = isPcma ? AlawToLinear(payload[i]) : PcmuToLinear(payload[i]);
        }

        return decoded;
    }

    private static byte LinearToPcmu(short sample)
    {
        const int bias = 0x84;
        const int clip = 32635;
        int value = sample;
        bool sign = value < 0;
        if (sign)
        {
            value = -value;
        }

        if (value > clip)
        {
            value = clip;
        }

        value += bias;
        int exponent = 7;
        for (int mask = 0x4000; (value & mask) == 0 && exponent > 0; mask >>= 1)
        {
            exponent--;
        }

        int mantissa = (value >> (exponent + 3)) & 0x0F;
        int signBit = sign ? 0x80 : 0;
        byte ulaw = (byte)~(signBit | exponent << 4 | mantissa);
        return ulaw;
    }

    private static short PcmuToLinear(byte ulaw)
    {
        const int bias = 0x84;
        int value = ~ulaw;
        bool sign = (value & 0x80) != 0;
        int exponent = (value >> 4) & 0x07;
        int mantissa = value & 0x0F;
        int linear = ((mantissa << 3) + bias) << exponent;
        short sample = (short)(linear - bias);
        return sign ? (short)-sample : sample;
    }

    private static short AlawToLinear(byte alaw)
    {
        int value = alaw ^ 0x55;
        bool sign = (value & 0x80) != 0;
        int exponent = (value >> 4) & 0x07;
        int mantissa = value & 0x0F;
        int linear;
        if (exponent == 0)
        {
            linear = (mantissa << 4) + 8;
        }
        else
        {
            linear = ((mantissa << 4) + 0x108) << (exponent - 1);
        }

        short sample = (short)linear;
        return sign ? (short)-sample : sample;
    }
}
