using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// A .NET Stream as FFmpeg's input (a custom AVIOContext). A stream that can seek (<see cref="Stream.CanSeek"/>: a
/// FileStream, a MemoryStream) is read as ffmpeg reads a file: seekable, its size known, from where it is when opened
/// (that position is offset 0), so an MP4 whose moov comes last opens and the demuxers behave as on the file's path.
/// Any other stream, or one after <c>prefix</c> (the bytes already read to probe it), is read once and not seekable, as
/// ffmpeg reads a pipe: like its <c>fd:</c> protocol (what <c>-i -</c> opens) on one, the size is 0 and seeking fails.
/// With no seek at all, a demuxer asking for the size got an error, which mp3dec takes for a huge file ("invalid
/// concatenated file") and then keeps the encoder's end padding.
/// </summary>
internal sealed unsafe class StreamInput : IDisposable
{
    const int BufferSize = 1 << 16;
    const int EIO = 5, ESPIPE = 29, EINVAL = 22, ENOSYS = 38;
    readonly byte[] _prefix;
    readonly Stream? _rest;
    readonly long _origin; // a seekable stream's position when opened: FFmpeg's offset 0
    readonly avio_alloc_context_read_packet _read; // kept alive while FFmpeg holds a pointer to it
    readonly avio_alloc_context_seek _seek;
    int _at;

    /// <summary>The context to set as AVFormatContext.pb (with AVFMT_FLAG_CUSTOM_IO).</summary>
    public AVIOContext* Context { get; private set; }

    /// <summary>What reading the stream threw, if it did (FFmpeg only sees an I/O error).</summary>
    public Exception? Error { get; private set; }

    /// <summary>Whether FFmpeg seeks the stream (it can, and there's no prefix): read as a file, not as a pipe.</summary>
    public bool Seekable { get; }

    public StreamInput(byte[] prefix, Stream? rest)
    {
        _prefix = prefix;
        _rest = rest;
        _read = Read;
        Seekable = prefix.Length == 0 && rest is { CanSeek: true };
        _origin = Seekable ? rest!.Position : 0;
        _seek = Seekable ? SeekStream : (_, _, whence) => whence == ffmpeg.AVSEEK_SIZE ? 0 : ffmpeg.AVERROR(ESPIPE); // fstat of a FIFO; lseek: ESPIPE
        var buffer = (byte*)ffmpeg.av_malloc(BufferSize);

        // With a seek function avio_alloc_context marks the context seekable (AVIO_SEEKABLE_NORMAL), as the file
        // protocol's is. For a pipe it marks it not seekable when given none, and the function (for the size alone) goes
        // in afterwards. Not through the seekable field: FFmpeg.AutoGen lays AVIOContext out for 8-byte longs, and on
        // Windows (4-byte `unsigned long checksum`) every field from checksum on sits 8 bytes earlier than it says.
        Context = ffmpeg.avio_alloc_context(buffer, BufferSize, 0, null, _read, null, Seekable ? _seek : null);
        if (!Seekable)
            Context->seek = new AVIOContext_seek_func { Pointer = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_seek) };
    }

    int Read(void* opaque, byte* buf, int size)
    {
        try
        {
            var span = new Span<byte>(buf, size);
            if (_at < _prefix.Length)
            {
                int n = Math.Min(size, _prefix.Length - _at);
                _prefix.AsSpan(_at, n).CopyTo(span);
                _at += n;
                return n;
            }

            int read = _rest?.Read(span) ?? 0;
            return read > 0 ? read : ffmpeg.AVERROR_EOF;
        }
        catch (Exception e)
        {
            Error = e;
            return ffmpeg.AVERROR(EIO);
        }
    }

    /// <summary>The file protocol's seek on the stream, offsets from <see cref="_origin"/>: SEEK_SET, SEEK_CUR, SEEK_END, AVSEEK_SIZE.</summary>
    long SeekStream(void* opaque, long offset, int whence)
    {
        var stream = _rest!;
        whence &= ~ffmpeg.AVSEEK_FORCE;
        if (whence == ffmpeg.AVSEEK_SIZE)
        {
            try
            {
                return stream.Length - _origin;
            }
            catch (NotSupportedException)
            {
                return ffmpeg.AVERROR(ENOSYS); // as the file protocol when fstat can't tell: FFmpeg goes on without
            }
        }

        try
        {
            long target = whence switch
            {
                0 => offset, // SEEK_SET
                1 => stream.Position - _origin + offset, // SEEK_CUR
                2 => stream.Length - _origin + offset, // SEEK_END
                _ => -1,
            };
            if (target < 0)
                return ffmpeg.AVERROR(EINVAL);
            stream.Position = _origin + target;
            return target;
        }
        catch (Exception e)
        {
            Error = e;
            return ffmpeg.AVERROR(EIO);
        }
    }

    /// <summary>Frees the context (after avformat_close_input, which leaves a custom one alone).</summary>
    public void Dispose()
    {
        if (Context == null)
            return;
        var c = Context;
        ffmpeg.av_freep(&c->buffer);
        ffmpeg.avio_context_free(&c);
        Context = null;
    }
}
