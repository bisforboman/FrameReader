using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// <see cref="VideoFrameReader"/>'s conversion: a decoded frame to <see cref="VideoFrameReaderOptions.FrameFormat"/> at
/// the options' size, as ffmpeg's <c>-vf scale=W:H:flags=bicubic,format=F -f rawvideo</c> gives it, into a
/// <see cref="FrameOutput"/>. One swscale context, on one thread at a time: the reader's caller has one, the read-ahead
/// thread another (<see cref="ReadAhead"/>), with the same settings, so the same bytes.
/// </summary>
internal sealed unsafe class FrameConverter : IDisposable
{
    readonly VideoFrameReaderOptions _options;
    readonly AVPixelFormat _format;
    SwsContext* _sws;
    AVFrame* _in;

    internal FrameConverter(VideoFrameReaderOptions options, AVPixelFormat format) => (_options, _format) = (options, format);

    /// <summary>Converts <paramref name="src"/> into <paramref name="output"/> (its bytes, size and tags).</summary>
    /// <exception cref="FrameReaderException">Converting failed.</exception>
    internal void Convert(AVFrame* src, FrameOutput output)
    {
        var (width, height) = OutputSize(src->width, src->height);
        if (_sws == null)
        {
            _in = ffmpeg.av_frame_alloc();
            _sws = ffmpeg.sws_alloc_context();
            VideoFrameReader.Check(ffmpeg.av_opt_set(_sws, "sws_flags", "bicubic", 0), "scaler");
        }

        var o = output.Frame;
        if (o->width != width || o->height != height)
        {
            ffmpeg.av_frame_unref(o);
            (o->format, o->width, o->height) = ((int)_format, width, height);

            // As the scale filter's output buffers are (issue #656): aligned rows, where swscale takes other code paths into
            // an unaligned buffer (packed rows) that left an odd width's last RGB column unwritten on x64; and zeroed, as
            // libavfilter's frame pool allocates them (av_buffer_allocz), since on ARM swscale never writes that column, so
            // ffmpeg's bytes there are 0 (ours were the buffer's old contents). Packed after converting, as rawvideo writes them.
            VideoFrameReader.Check(ffmpeg.av_frame_get_buffer(o, 0), "frame buffer");
            for (uint i = 0; i < 8 && o->buf[i] != null; i++)
                NativeMemory.Clear(o->buf[i]->data, (nuint)o->buf[i]->size);
        }

        // As the scale filter does: the input read as progressive (its interl=0), and the output's tags those the link
        // negotiates (YUV and gray keep the input's matrix and range; RGB is RGB).
        // Its in_chroma_loc (default unspecified) replaces the frame's chroma location, so chroma isn't re-sited.
        VideoFrameReader.Check(ffmpeg.av_frame_ref(_in, src), "frame");
        _in->flags &= ~ffmpeg.AV_FRAME_FLAG_INTERLACED;
        _in->chroma_location = AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED;
        (o->chroma_location, o->color_primaries, o->color_trc) = (_in->chroma_location, _in->color_primaries, _in->color_trc);
        bool rgb = _options.FrameFormat is not (FrameFormat.Gray8 or FrameFormat.Yuv420p);
        o->colorspace = rgb ? AVColorSpace.AVCOL_SPC_RGB : src->colorspace;
        o->color_range = rgb ? AVColorRange.AVCOL_RANGE_JPEG : src->color_range;

        // The decoded frame already in the output's format and size: the scale filter passes it through untouched, where
        // swscale's unscaled copy leaves the last chroma row of an odd height unwritten (issue #656).
        bool passThrough = src->format == (int)_format && (src->width, src->height) == (width, height);
        if (!passThrough)
        {
            int ret = ffmpeg.sws_scale_frame(_sws, o, _in);
            ffmpeg.av_frame_unref(_in);
            VideoFrameReader.Check(ret, "convert");
        }

        var converted = passThrough ? _in : o;
        int size = ffmpeg.av_image_get_buffer_size(_format, width, height, 1);
        bool packed = !(!passThrough && o->linesize[0] == size / height && _options.FrameFormat != FrameFormat.Yuv420p);
        if (packed)
        {
            // The rows and planes packed one after another, as rawvideo writes them (else one plane, its rows already packed).
            if (output.Packed.Length != size)
                output.Packed = new byte[size];
            var planes = default(byte_ptrArray4);
            var strides = default(int_array4);
            for (uint i = 0; i < 4; i++)
            {
                planes[i] = converted->data[i];
                strides[i] = converted->linesize[i];
            }

            fixed (byte* p = output.Packed)
            {
                int ret = ffmpeg.av_image_copy_to_buffer(p, size, in planes, in strides, _format, width, height, 1);
                if (passThrough)
                    ffmpeg.av_frame_unref(_in);
                VideoFrameReader.Check(ret, "pack");
            }
        }

        // The aspect ratio as the scale filter sets its output's: the input's times the change in shape (0/1 stays).
        var sar = src->sample_aspect_ratio;
        sar = sar.num != 0
            ? ffmpeg.av_mul_q(new AVRational { num = height * src->width, den = width * src->height }, sar)
            : new AVRational { num = 0, den = 1 };

        // Gray as ffmpeg's link negotiates it: full range, no matrix, although swscale copies the luma unchanged.
        output.Tags = _options.FrameFormat == FrameFormat.Gray8
            ? new FrameTags(sar, AVColorRange.AVCOL_RANGE_JPEG, AVColorSpace.AVCOL_SPC_UNSPECIFIED, o->color_primaries, o->color_trc)
            : new FrameTags(sar, o->color_range, o->colorspace, o->color_primaries, o->color_trc);
        (output.Width, output.Height, output.Size, output.InPacked) = (width, height, size, packed);
    }

    /// <summary>The size frames come out at: Width and Height, or MaxWidth's cap, or the frame's own.</summary>
    (int Width, int Height) OutputSize(int width, int height)
    {
        if (_options.Width is { } w && _options.Height is { } h)
            return (w, h);
        if (_options.MaxWidth is not { } max)
            return (width, height);

        // scale's -2: av_rescale(w, ih, iw * 2) * 2, the division rounded to nearest (halves away from zero), so an
        // odd height becomes even even when the width stays. A height that comes out 0 (a frame over twice as wide as
        // tall per pixel of the cap) is the input's, as libavfilter fills in a link's missing size.
        int outWidth = Math.Min(max, width);
        long twice = 2L * width;
        int outHeight = (int)((outWidth * (long)height + twice / 2) / twice * 2);
        return (outWidth, outHeight == 0 ? height : outHeight);
    }

    /// <summary>Frees the scaler.</summary>
    public void Dispose()
    {
        if (_sws != null)
        {
            var s = _sws;
            ffmpeg.sws_free_context(&s);
            _sws = null;
        }

        if (_in != null)
        {
            var f = _in;
            ffmpeg.av_frame_free(&f);
            _in = null;
        }
    }
}

/// <summary>
/// A converted frame: <see cref="FrameConverter"/>'s output buffer, and the packed copy where rawvideo's layout differs
/// from it. Reused frame after frame; its <see cref="Data"/> valid until the next conversion into it.
/// </summary>
internal sealed unsafe class FrameOutput : IDisposable
{
    static int _alive;

    AVFrame* _frame = ffmpeg.av_frame_alloc();

    internal FrameOutput() => Interlocked.Increment(ref _alive);

    /// <summary>Outputs made and not disposed yet, in all readers (tests: none after Dispose).</summary>
    internal static int Alive => Volatile.Read(ref _alive);

    internal AVFrame* Frame => _frame;

    internal byte[] Packed { get; set; } = [];

    internal int Width { get; set; }

    internal int Height { get; set; }

    internal int Size { get; set; }

    internal bool InPacked { get; set; }

    internal FrameTags Tags { get; set; }

    /// <summary>The bytes, packed as rawvideo writes them.</summary>
    internal ReadOnlySpan<byte> Data => InPacked ? Packed : new ReadOnlySpan<byte>(_frame->data[0], Size);

    /// <summary>Frees the buffer.</summary>
    public void Dispose()
    {
        if (_frame == null)
            return;
        var f = _frame;
        ffmpeg.av_frame_free(&f);
        _frame = null;
        Interlocked.Decrement(ref _alive);
    }
}
