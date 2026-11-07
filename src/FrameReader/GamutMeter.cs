using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FrameReader;

/// <summary>What <see cref="GamutMeter"/> counts as in gamut.</summary>
public sealed record GamutOptions
{
    /// <summary>The lowest R, G or B in gamut, 0 being black: -0.05 by default (EBU R 103's -5%).</summary>
    public double Low { get; init; } = -0.05;

    /// <summary>The highest R, G or B in gamut, 1 being white: 1.05 by default (EBU R 103's 105%).</summary>
    public double High { get; init; } = 1.05;

    /// <summary>
    /// The matrix's Kr and Kb (<see cref="GamutMeter.Coefficients"/> gives them for ffprobe's color_space); null: BT.709's,
    /// or BT.601's for a picture under 720 lines.
    /// </summary>
    public (double Kr, double Kb)? Matrix { get; init; }
}

/// <summary>
/// Pixels outside RGB gamut (EBU R 103, EBU QC 0051B) on frames of yours, with nothing of FFmpeg, which has no filter
/// that counts them (its RGB conversions clip): each pixel's R, G and B from its Y and the Cb and Cr samples it is sited
/// on (the one beside it in 4:2:2 and 4:2:0, not interpolated), by the matrix's Kr and Kb, each from 0 (black) to 1
/// (white); a pixel is out of gamut when any of them is below <see cref="GamutOptions.Low"/> or above
/// <see cref="GamutOptions.High"/>. Each frame's share of such pixels: the share an FFmpeg <c>geq</c> expression doing
/// this arithmetic per pixel (<see cref="InGamut"/>) gives as signalstats' YAVG of its 0-or-maximum mask. Planar YUV, 8
/// to 12 bits. Not thread-safe: one caller feeds it, in order.
/// </summary>
/// <remarks>
/// Each of R, G and B rises with Y by the same step for a given Cb and Cr, so the pixels in gamut are one run of Y codes
/// for each pair: a table of those runs (2^(2 x depth) entries), its edges found by <see cref="InGamut"/> itself, makes a
/// pixel one lookup. The table is built once for each depth, range, limits and matrix and shared by the meters made with
/// them (the last few kept: a 12-bit table is 64 MB).
/// </remarks>
public sealed class GamutMeter
{
    // The tables kept for meters made later, the latest used first, up to CacheBytes of them.
    const long CacheBytes = 128L << 20;
    static readonly Lock CacheLock = new();
    static readonly LinkedList<(Arithmetic Key, Lazy<uint[]> Runs)> Cache = [];

    readonly int _width, _height, _depth, _hShift, _vShift, _chromaWidth;
    readonly Arithmetic _arithmetic;
    readonly List<long> _pts = [];
    readonly List<double> _shares = [];
    readonly uint[] _runs;

    /// <summary>
    /// A meter of frames of <paramref name="width"/> by <paramref name="height"/>, <paramref name="chroma"/>,
    /// <paramref name="bitDepth"/> bits, full or limited range.
    /// </summary>
    /// <exception cref="ArgumentException">A size that isn't positive, a depth outside 8 to 12, or a low limit not below the high one.</exception>
    public GamutMeter(int width, int height, ChromaSubsampling chroma, int bitDepth, bool fullRange, GamutOptions? options = null)
    {
        var o = options ?? new GamutOptions();
        if (width <= 0 || height <= 0)
            throw new ArgumentException("The picture's size must be positive.", nameof(width));
        if (bitDepth is < 8 or > 12)
            throw new ArgumentException("The bit depth must be 8 to 12.", nameof(bitDepth));
        if (!(o.Low < o.High))
            throw new ArgumentException("The low limit must be below the high one.", nameof(options));
        (_width, _height, _depth) = (width, height, bitDepth);
        var (kr, kb) = o.Matrix ?? (height < 720 ? (0.299, 0.114) : (0.2126, 0.0722));
        _arithmetic = new Arithmetic(bitDepth, fullRange, o.Low, o.High, kr, kb);
        _hShift = chroma == ChromaSubsampling.Yuv444 ? 0 : 1;
        _vShift = chroma == ChromaSubsampling.Yuv420 ? 1 : 0;
        _chromaWidth = (width + _hShift) >> _hShift;
        _runs = Runs(_arithmetic);
    }

    /// <summary>A meter for frames of <paramref name="format"/> (its size, chroma, depth and range).</summary>
    public static GamutMeter For(PictureFormat format, GamutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        return new GamutMeter(format.Width, format.Height, format.Chroma, format.BitDepth, format.FullRange, options);
    }

    /// <summary>The Kr and Kb of ffprobe's color_space (bt709; bt470bg, smpte170m; bt2020nc, bt2020c), or null for another.</summary>
    public static (double Kr, double Kb)? Coefficients(string? colorSpace) => colorSpace switch
    {
        "bt709" => (0.2126, 0.0722),
        "bt470bg" or "smpte170m" => (0.299, 0.114),
        "bt2020nc" or "bt2020c" => (0.2627, 0.0593),
        _ => null,
    };

    /// <summary>The Kr and Kb the frames are converted with.</summary>
    public (double Kr, double Kb) Matrix => (_arithmetic.Kr, _arithmetic.Kb);

    /// <summary>Each frame's share of pixels out of gamut (0 to 1), in the order added.</summary>
    public IReadOnlyList<double> Shares => _shares;

    /// <summary>Each frame's pts, in the order added.</summary>
    public IReadOnlyList<long> Pts => _pts;

    /// <summary>Adds a decoded frame (its planes and pts).</summary>
    public void Add(DecodedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Add(frame.Pts, frame.GetPlane(0, out int ys), ys, frame.GetPlane(1, out int us), us, frame.GetPlane(2, out int vs), vs);
    }

    /// <summary>Adds the decoder's current frame (its planes and pts).</summary>
    public void Add(FrameDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        Add(decoder.Pts, decoder.GetPlane(0, out int ys), ys, decoder.GetPlane(1, out int us), us, decoder.GetPlane(2, out int vs), vs);
    }

    /// <summary>
    /// Adds the next frame: its pts and its Y, U and V planes, each with the bytes from row to row (8 bits a sample, else
    /// 16-bit little-endian).
    /// </summary>
    /// <exception cref="ArgumentException">A plane smaller than its size.</exception>
    public void Add(long pts, ReadOnlySpan<byte> y, int yStride, ReadOnlySpan<byte> u, int uStride, ReadOnlySpan<byte> v, int vStride)
    {
        int bytes = _depth > 8 ? 2 : 1;
        int chromaHeight = (_height + _vShift) >> _vShift;
        Check(y, yStride, _width * bytes, _height, 0);
        Check(u, uStride, _chromaWidth * bytes, chromaHeight, 1);
        Check(v, vStride, _chromaWidth * bytes, chromaHeight, 2);
        long outside = 0;
        for (int r = 0; r < _height; r++)
        {
            int c = r >> _vShift;
            if (_depth > 8)
            {
                outside += Row(
                    MemoryMarshal.Cast<byte, ushort>(y.Slice(r * yStride, _width * 2)),
                    MemoryMarshal.Cast<byte, ushort>(u.Slice(c * uStride, _chromaWidth * 2)),
                    MemoryMarshal.Cast<byte, ushort>(v.Slice(c * vStride, _chromaWidth * 2)),
                    _runs,
                    _hShift,
                    _depth);
            }
            else
            {
                outside += Row(y.Slice(r * yStride, _width), u.Slice(c * uStride, _chromaWidth), v.Slice(c * vStride, _chromaWidth), _runs, _hShift);
            }
        }

        _pts.Add(pts);
        _shares.Add((double)outside / ((long)_width * _height));
    }

    /// <summary>
    /// Whether the pixel of codes <paramref name="y"/>, <paramref name="cb"/>, <paramref name="cr"/> is in gamut, in the
    /// order a geq expression would do it: Y from 0 to 1 and Cb, Cr from -0.5 to 0.5 (limited range 16-235 and 16-240
    /// scaled to the depth; full range 0 to 2^depth − 1 about 2^(depth − 1)), R = Y + 2(1 − Kr)Cr, B = Y + 2(1 − Kb)Cb,
    /// G = (Y − Kr·R − Kb·B) / (1 − Kr − Kb).
    /// </summary>
    public bool InGamut(int y, int cb, int cr) => _arithmetic.InGamut(y, cb, cr);

    // The table of runs for the arithmetic: the one kept for it, or built (once, by whichever meter asks first).
    static uint[] Runs(Arithmetic arithmetic)
    {
        Lazy<uint[]> runs;
        lock (CacheLock)
        {
            var node = Cache.First;
            while (node is not null && !node.Value.Key.Equals(arithmetic))
                node = node.Next;
            if (node is null)
            {
                node = Cache.AddFirst((arithmetic, new Lazy<uint[]>(() => Build(arithmetic))));
                long bytes = 0;
                for (var n = Cache.First; n is not null; n = n.Next)
                    bytes += n.Value.Key.TableBytes;
                while (bytes > CacheBytes && Cache.Last != node)
                {
                    bytes -= Cache.Last!.Value.Key.TableBytes;
                    Cache.RemoveLast();
                }
            }
            else if (node != Cache.First)
            {
                Cache.Remove(node);
                Cache.AddFirst(node);
            }

            runs = node.Value.Runs;
        }

        return runs.Value;
    }

    static void Check(ReadOnlySpan<byte> plane, int stride, int rowBytes, int height, int index)
    {
        if (stride < rowBytes || plane.Length < ((long)stride * (height - 1)) + rowBytes)
            throw new ArgumentException($"Plane {index} is smaller than its size.", nameof(plane));
    }

    // The pixels of one row outside their chroma pair's run of Y codes (a run: its first code, and its last in the
    // high 16 bits), the pair looked up once for the luma samples sited on it.
    static long Row(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, uint[] runs, int hShift)
    {
        long outside = 0;
        if (hShift == 1)
        {
            // Two luma samples to each chroma pair: the pairs whose two samples both are there, then the last one alone.
            int pairs = luma.Length / 2;
            ref byte l = ref MemoryMarshal.GetReference(luma);
            ref byte b = ref MemoryMarshal.GetReference(cb);
            ref byte r = ref MemoryMarshal.GetReference(cr);
            ref uint table = ref MemoryMarshal.GetArrayDataReference(runs);
            for (int c = 0; c < pairs; c++)
            {
                uint run = Unsafe.Add(ref table, (Unsafe.Add(ref b, c) << 8) | Unsafe.Add(ref r, c));
                uint first = run & 0xFFFF, span = (run >> 16) - first;
                outside += ((uint)(Unsafe.Add(ref l, 2 * c) - first) > span ? 1 : 0) + ((uint)(Unsafe.Add(ref l, (2 * c) + 1) - first) > span ? 1 : 0);
            }

            if (luma.Length % 2 == 1)
            {
                uint run = runs[(cb[pairs] << 8) | cr[pairs]];
                uint first = run & 0xFFFF;
                outside += (uint)(luma[^1] - first) > (run >> 16) - first ? 1 : 0;
            }

            return outside;
        }

        for (int x = 0; x < luma.Length; x++)
        {
            uint run = runs[(cb[x] << 8) | cr[x]];
            uint first = run & 0xFFFF;

            // Unsigned: a code below the first wraps past the span.
            if ((uint)(luma[x] - first) > (run >> 16) - first)
                outside++;
        }

        return outside;
    }

    static long Row(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr, uint[] runs, int hShift, int depth)
    {
        long outside = 0;
        int mask = (1 << depth) - 1;
        if (hShift == 1)
        {
            int pairs = luma.Length / 2;
            ref ushort l = ref MemoryMarshal.GetReference(luma);
            ref ushort b = ref MemoryMarshal.GetReference(cb);
            ref ushort r = ref MemoryMarshal.GetReference(cr);
            ref uint table = ref MemoryMarshal.GetArrayDataReference(runs);
            for (int c = 0; c < pairs; c++)
            {
                uint run = Unsafe.Add(ref table, ((Unsafe.Add(ref b, c) & mask) << depth) | (Unsafe.Add(ref r, c) & mask));
                uint first = run & 0xFFFF, span = (run >> 16) - first;
                outside += ((uint)((Unsafe.Add(ref l, 2 * c) & mask) - first) > span ? 1 : 0) + ((uint)((Unsafe.Add(ref l, (2 * c) + 1) & mask) - first) > span ? 1 : 0);
            }

            if (luma.Length % 2 == 1)
            {
                uint run = runs[((cb[pairs] & mask) << depth) | (cr[pairs] & mask)];
                uint first = run & 0xFFFF;
                outside += (uint)((luma[^1] & mask) - first) > (run >> 16) - first ? 1 : 0;
            }

            return outside;
        }

        for (int x = 0; x < luma.Length; x++)
        {
            uint run = runs[((cb[x] & mask) << depth) | (cr[x] & mask)];
            uint first = run & 0xFFFF;
            if ((uint)((luma[x] & mask) - first) > (run >> 16) - first)
                outside++;
        }

        return outside;
    }

    /// <summary>
    /// What a table depends on, and the arithmetic itself: the depth, the range, the limits and the matrix (the limits
    /// and the matrix compared by their bits).
    /// </summary>
    readonly struct Arithmetic(int depth, bool fullRange, double low, double high, double kr, double kb) : IEquatable<Arithmetic>
    {
        public int Depth { get; } = depth;

        public bool FullRange { get; } = fullRange;

        public double Low { get; } = low;

        public double High { get; } = high;

        public double Kr { get; } = kr;

        public double Kb { get; } = kb;

        public long TableBytes => sizeof(uint) * (1L << (2 * Depth));

        public bool Equals(Arithmetic other) => Depth == other.Depth && FullRange == other.FullRange && Bits(Low) == Bits(other.Low)
            && Bits(High) == Bits(other.High) && Bits(Kr) == Bits(other.Kr) && Bits(Kb) == Bits(other.Kb);

        public override bool Equals(object? obj) => obj is Arithmetic other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Depth, FullRange, Bits(Low), Bits(High), Bits(Kr), Bits(Kb));

        public bool InGamut(int y, int cb, int cr)
        {
            var (yn, pb, pr) = Normalised(y, cb, cr);
            double r = yn + (2 * (1 - Kr) * pr);
            double b = yn + (2 * (1 - Kb) * pb);
            double g = (yn - (Kr * r) - (Kb * b)) / (1 - Kr - Kb);
            return r >= Low && r <= High && g >= Low && g <= High && b >= Low && b <= High;
        }

        public (double Y, double Cb, double Cr) Normalised(int y, int cb, int cr)
        {
            double s = 1 << (Depth - 8);
            if (FullRange)
            {
                double max = (1 << Depth) - 1, mid = 1 << (Depth - 1);
                return (y / max, (cb - mid) / max, (cr - mid) / max);
            }

            return ((y - (16 * s)) / (219 * s), (cb - (128 * s)) / (224 * s), (cr - (128 * s)) / (224 * s));
        }

        static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
    }

    // The run of Y codes in gamut for every Cb and Cr: estimated from the limits, then moved to where InGamut changes.
    // Each Cb's runs on a thread of their own: every entry depends on its codes alone.
    static uint[] Build(Arithmetic a)
    {
        int depth = a.Depth, size = 1 << depth, max = size - 1;
        var runs = new uint[size * size];
        double kg = 1 - a.Kr - a.Kb;
        double yScale = a.FullRange ? max : 219.0 * (1 << (depth - 8));
        double yOffset = a.FullRange ? 0 : 16.0 * (1 << (depth - 8));
        Parallel.For(0, size, cb =>
        {
            for (int cr = 0; cr < size; cr++)
            {
                var (_, pb, pr) = a.Normalised(0, cb, cr);
                double dr = 2 * (1 - a.Kr) * pr, db = 2 * (1 - a.Kb) * pb, dg = -((a.Kr * dr) + (a.Kb * db)) / kg;
                double low = a.Low - Math.Min(dr, Math.Min(dg, db)), high = a.High - Math.Max(dr, Math.Max(dg, db));
                int first = (int)Math.Clamp(Math.Ceiling((low * yScale) + yOffset), 0, max);
                int last = (int)Math.Clamp(Math.Floor((high * yScale) + yOffset), 0, max);

                // The estimate is off by a code at most: find one code in gamut near it (none when it is empty by more
                // than that), then the run's two ends.
                int? inside = null;
                for (int at = Math.Max(0, first - 1); at <= Math.Min(max, Math.Min(last, first + 1) + 1) && inside is null; at++)
                {
                    if (a.InGamut(at, cb, cr))
                        inside = at;
                }

                if (inside is { } found)
                {
                    (first, last) = (found, Math.Max(found, last));
                    while (first > 0 && a.InGamut(first - 1, cb, cr))
                        first--;
                    while (last > first && !a.InGamut(last, cb, cr))
                        last--;
                    while (last < max && a.InGamut(last + 1, cb, cr))
                        last++;
                }
                else
                {
                    // None in gamut: a run of a code no sample of 8 to 12 bits has (65535), which every code is outside.
                    (first, last) = (ushort.MaxValue, ushort.MaxValue);
                }

                runs[(cb << depth) | cr] = (uint)first | ((uint)last << 16);
            }
        });

        return runs;
    }
}
