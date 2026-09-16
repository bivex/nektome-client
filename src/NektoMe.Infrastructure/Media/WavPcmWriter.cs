using NektoMe.Application.Abstractions;

namespace NektoMe.Infrastructure.Media;

/// <summary>
/// Diagnostic <see cref="IAudioSink"/> that records decoded remote audio as a
/// mono 16-bit WAV file. The RIFF header is stamped on the first write and
/// patched with the final length on dispose.
/// </summary>
public sealed class WavPcmWriter : IAudioSink, IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private int _sampleRate;
    private long _dataBytes;
    private bool _disposed;

    public WavPcmWriter(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_stream);
        FilePath = Path.GetFullPath(path);
        WriteHeaderPlaceholder();
    }

    public string FilePath { get; }

    public void Write(short[] pcm, int sampleRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sampleRate == 0)
        {
            _sampleRate = sampleRate;
            WriteHeader();
        }

        if (sampleRate != _sampleRate)
        {
            // A single WAV cannot mix rates; keep recording, the header stays at the first rate.
            return;
        }

        byte[] bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        _writer.Write(bytes);
        _dataBytes += bytes.Length;
        _writer.Flush();
    }

    private void WriteHeaderPlaceholder()
    {
        _writer.Seek(0, SeekOrigin.Begin);
        _writer.Write(new byte[44]);
        _writer.Flush();
    }

    private void WriteHeader()
    {
        const short bitsPerSample = 16;
        const short channels = 1;
        int byteRate = _sampleRate * channels * bitsPerSample / 8;

        _writer.Seek(0, SeekOrigin.Begin);
        _writer.Write("RIFF"u8);
        _writer.Write((int)(36 + _dataBytes));
        _writer.Write("WAVE"u8);
        _writer.Write("fmt "u8);
        _writer.Write(16);
        _writer.Write((short)1); // PCM
        _writer.Write(channels);
        _writer.Write(_sampleRate);
        _writer.Write(byteRate);
        _writer.Write((short)(channels * bitsPerSample / 8));
        _writer.Write(bitsPerSample);
        _writer.Write("data"u8);
        _writer.Write((int)_dataBytes);
        _writer.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_sampleRate != 0)
        {
            WriteHeader();
        }

        _writer.Dispose();
        _stream.Dispose();
    }
}
