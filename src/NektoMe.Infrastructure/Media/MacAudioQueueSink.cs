using System.Runtime.InteropServices;
using NektoMe.Application.Abstractions;

namespace NektoMe.Infrastructure.Media;

/// <summary>
/// Live playback of decoded remote audio through CoreAudio's AudioQueue
/// (macOS only; no native NuGet dependency — signatures match the SDK's
/// AudioToolbox headers). The queue runs continuously: the completion callback
/// re-enqueues each buffer, copying from the incoming ring buffer and
/// zero-filling whatever has not arrived yet, so silence plays during gaps.
/// All failures degrade to a no-op sink — a broken output device must not
/// kill the receive path.
/// </summary>
public sealed class MacAudioQueueSink : IAudioSink, IDisposable
{
    private const int BufferBytes = 1600;   // 100 ms of 8 kHz mono 16-bit
    private const int BufferCount = 5;      // ~500 ms of buffered playback

    private const uint FormatLinearPcm = 0x6C70636D; // 'lpcm'
    private const uint FlagsSignedInteger = 1U << 2;
    private const uint FlagsPacked = 1U << 3;

    private readonly object _gate = new();
    private readonly AudioQueueOutputCallback _callback;
    private readonly byte[] _ring = new byte[8000 * 2 * 2]; // 2 s ring
    private static readonly byte[] Silence = new byte[BufferBytes];

    private IntPtr _queue;
    private IntPtr[] _buffers = [];
    private int _ringStart;
    private int _ringLength;
    private int _sampleRate;
    private bool _broken;

    public MacAudioQueueSink()
    {
        _callback = OnBufferCompleted;
    }

    /// <summary>True once a CoreAudio call failed; the sink then ignores all input.</summary>
    public bool IsBroken => Volatile.Read(ref _broken);

    public void Write(short[] pcm, int sampleRate)
    {
        if (_broken || pcm.Length == 0)
        {
            return;
        }

        if (_queue == IntPtr.Zero)
        {
            lock (_gate)
            {
                if (_queue == IntPtr.Zero && !Start(sampleRate))
                {
                    return;
                }
            }
        }

        if (sampleRate != _sampleRate && sampleRate % _sampleRate == 0)
        {
            pcm = Decimate(pcm, sampleRate / _sampleRate);
        }

        // 16-bit signed little-endian bytes.
        var bytes = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++)
        {
            bytes[2 * i] = (byte)pcm[i];
            bytes[(2 * i) + 1] = (byte)(pcm[i] >> 8);
        }

        lock (_gate)
        {
            AppendLocked(bytes);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_queue != IntPtr.Zero)
            {
                _ = AudioQueueStop(_queue, immediate: true);
                _ = AudioQueueDispose(_queue, immediate: true);
                _queue = IntPtr.Zero;
            }

            _buffers = [];
            _ringLength = 0;
            _ringStart = 0;
        }
    }

    private bool Start(int sampleRate)
    {
        _sampleRate = sampleRate;
        var format = new AudioStreamBasicDescription
        {
            SampleRate = sampleRate,
            FormatID = FormatLinearPcm,
            FormatFlags = FlagsSignedInteger | FlagsPacked,
            BytesPerPacket = 2,
            FramesPerPacket = 1,
            BytesPerFrame = 2,
            ChannelsPerFrame = 1,
            BitsPerChannel = 16,
        };

        if (AudioQueueNewOutput(ref format, _callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out IntPtr queue) != 0)
        {
            _broken = true;
            return false;
        }

        _queue = queue;
        _buffers = new IntPtr[BufferCount];
        for (int i = 0; i < BufferCount; i++)
        {
            if (AudioQueueAllocateBuffer(_queue, BufferBytes, out IntPtr buffer) != 0)
            {
                _broken = true;
                return false;
            }

            _buffers[i] = buffer;
            FillAndEnqueue(buffer, silence: true);
        }

        if (AudioQueueStart(_queue, IntPtr.Zero) != 0)
        {
            _broken = true;
            return false;
        }

        return true;
    }

    private void OnBufferCompleted(IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint packets, IntPtr descriptions)
    {
        // Runs on the audio queue's internal thread.
        lock (_gate)
        {
            if (_broken || queue != _queue)
            {
                return;
            }

            FillAndEnqueue(buffer, silence: false);
        }
    }

    private void FillAndEnqueue(IntPtr buffer, bool silence)
    {
        var header = Marshal.PtrToStructure<AudioQueueBuffer>(buffer);
        int capacity = checked((int)header.AudioDataBytesCapacity);
        int size = Math.Min(capacity, BufferBytes);

        if (silence)
        {
            Marshal.Copy(Silence, 0, header.AudioData, size);
        }
        else
        {
            ReadLocked(header.AudioData, size);
        }

        header.AudioDataByteSize = (uint)size;
        Marshal.StructureToPtr(header, buffer, fDeleteOld: false);
        if (AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero) != 0)
        {
            _broken = true;
        }
    }

    private void AppendLocked(byte[] bytes)
    {
        int overflow = (_ringLength + bytes.Length) - _ring.Length;
        if (overflow > 0)
        {
            // Drop the oldest audio; latency must stay bounded.
            _ringStart = (_ringStart + overflow) % _ring.Length;
            _ringLength -= overflow;
        }

        int first = Math.Min(bytes.Length, _ring.Length - ((_ringStart + _ringLength) % _ring.Length));
        int offset = (_ringStart + _ringLength) % _ring.Length;
        Array.Copy(bytes, 0, _ring, offset, first);
        Array.Copy(bytes, first, _ring, 0, bytes.Length - first);
        _ringLength += bytes.Length;
    }

    private void ReadLocked(IntPtr destination, int size)
    {
        int take = Math.Min(size, _ringLength);
        int first = Math.Min(take, _ring.Length - _ringStart);
        Marshal.Copy(_ring, _ringStart, destination, first);
        if (take > first)
        {
            Marshal.Copy(_ring, 0, destination + first, take - first);
        }

        if (take < size)
        {
            Marshal.Copy(Silence, 0, destination + take, size - take);
        }

        _ringStart = (_ringStart + take) % _ring.Length;
        _ringLength -= take;
    }

    private static short[] Decimate(short[] samples, int factor)
    {
        var reduced = new short[samples.Length / factor];
        for (int i = 0; i < reduced.Length; i++)
        {
            reduced[i] = samples[i * factor];
        }

        return reduced;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatID;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    private delegate void AudioQueueOutputCallback(
        IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint numberPacketDescriptions, IntPtr packetDescriptions);

    [DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
    private static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription format, AudioQueueOutputCallback callback, IntPtr userData,
        IntPtr callbackRunLoop, IntPtr callbackRunLoopMode, uint flags, out IntPtr queue);

    [DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bufferByteSize, out IntPtr buffer);

    [DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
    private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint numPacketDescs, IntPtr packetDescs);

    [DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

    [DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
    private static extern int AudioQueueStop(IntPtr queue, bool immediate);

    [DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
    private static extern int AudioQueueDispose(IntPtr queue, bool immediate);
}
