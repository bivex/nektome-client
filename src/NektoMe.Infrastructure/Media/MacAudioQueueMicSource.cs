using System.Runtime.InteropServices;
using NektoMe.Application.Abstractions;

namespace NektoMe.Infrastructure.Media;

/// <summary>
/// Live microphone capture through CoreAudio's AudioQueue input queue
/// (macOS only; no native NuGet dependency — signatures match the SDK's
/// AudioToolbox headers). Captures 8 kHz mono 16-bit PCM — exactly what the
/// G.711 engine consumes — in ring-of-buffers style: the completion callback
/// drains a buffer, hands the samples to subscribers and re-arms the buffer.
/// All failures degrade to a no-op source — a missing or denied microphone
/// must not kill the call.
/// </summary>
public sealed class MacAudioQueueMicSource : IMicrophoneCapture, IDisposable
{
    private const int SampleRate = 48000;
    private const int BufferBytes = 1920;   // 20 ms of 48 kHz mono 16-bit
    private const int BufferCount = 4;      // ~80 ms of queued capture

    private const uint FormatLinearPcm = 0x6C70636D; // 'lpcm'
    private const uint FlagsSignedInteger = 1U << 2;
    private const uint FlagsPacked = 1U << 3;

    private readonly object _lifecycleGate = new();
    private readonly object _gate = new();
    private readonly AudioQueueInputCallback _callback;

    private IntPtr _queue;
    private IntPtr[] _buffers = [];
    private bool _capturing;
    private bool _broken;

    public MacAudioQueueMicSource()
    {
        _callback = OnInputBufferFilled;
    }

    /// <summary>True while a capture queue is running.</summary>
    public bool IsCapturing => Volatile.Read(ref _capturing);

    /// <summary>True once a CoreAudio call failed; the source then stays off.</summary>
    public bool IsBroken => Volatile.Read(ref _broken);

    public event Action<short[], int>? PcmCaptured;

    public void Start()
    {
        if (_broken || _capturing)
        {
            return;
        }

        IntPtr failedQueue = IntPtr.Zero;
        lock (_lifecycleGate)
        {
            lock (_gate)
            {
                if (_broken || _capturing || _queue != IntPtr.Zero)
                {
                    return;
                }

                if (StartLocked(out failedQueue))
                {
                    _capturing = true;
                }
            }

            if (failedQueue != IntPtr.Zero)
            {
                _ = AudioQueueStop(failedQueue, immediate: true);
                _ = AudioQueueDispose(failedQueue, immediate: true);
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycleGate)
        {
            IntPtr queueToDispose = IntPtr.Zero;
            lock (_gate)
            {
                if (!_capturing && _queue == IntPtr.Zero)
                {
                    return;
                }

                queueToDispose = _queue;
                _queue = IntPtr.Zero;
                _capturing = false;
                _buffers = [];
            }

            if (queueToDispose != IntPtr.Zero)
            {
                _ = AudioQueueStop(queueToDispose, immediate: true);
                _ = AudioQueueDispose(queueToDispose, immediate: true);
            }
        }
    }

    public void Dispose() => Stop();

    private bool StartLocked(out IntPtr failedQueue)
    {
        failedQueue = IntPtr.Zero;
        var format = new AudioStreamBasicDescription
        {
            SampleRate = SampleRate,
            FormatID = FormatLinearPcm,
            FormatFlags = FlagsSignedInteger | FlagsPacked,
            BytesPerPacket = 2,
            FramesPerPacket = 1,
            BytesPerFrame = 2,
            ChannelsPerFrame = 1,
            BitsPerChannel = 16,
        };

        if (AudioQueueNewInput(ref format, _callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out IntPtr queue) != 0)
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
                failedQueue = _queue;
                _queue = IntPtr.Zero;
                _buffers = [];
                return false;
            }

            _buffers[i] = buffer;
            if (AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero) != 0)
            {
                _broken = true;
                failedQueue = _queue;
                _queue = IntPtr.Zero;
                _buffers = [];
                return false;
            }
        }

        if (AudioQueueStart(_queue, IntPtr.Zero) != 0)
        {
            _broken = true;
            failedQueue = _queue;
            _queue = IntPtr.Zero;
            _buffers = [];
            return false;
        }

        return true;
    }

    private void OnInputBufferFilled(IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint packets, IntPtr descriptions)
    {
        // Runs on the audio queue's internal thread.
        short[]? samples = null;
        lock (_gate)
        {
            // A disposed queue can deliver one final flush callback with a
            // NULL queue reference; it must not pass the ownership check.
            if (_broken || _queue == IntPtr.Zero || queue != _queue)
            {
                return;
            }

            int size = Marshal.ReadInt32(buffer, 16);
            if (size > 1)
            {
                int count = size / 2;
                samples = new short[count];
                IntPtr audioData = Marshal.ReadIntPtr(buffer, 8);
                Marshal.Copy(audioData, samples, 0, count);
            }

            // Re-arm the buffer for the next fill.
            if (AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero) != 0)
            {
                _broken = true;
            }
        }

        // Raise outside the lock: subscribers push into the audio engine.
        if (samples is not null)
        {
            PcmCaptured?.Invoke(samples, SampleRate);
        }
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



    private delegate void AudioQueueInputCallback(
        IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint numberPacketDescriptions, IntPtr packetDescriptions);

    [DllImport("/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
    private static extern int AudioQueueNewInput(
        ref AudioStreamBasicDescription format, AudioQueueInputCallback callback, IntPtr userData,
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
