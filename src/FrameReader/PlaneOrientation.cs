using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace FrameReader;

/// <summary>
/// The sample moves of <see cref="FrameDecoder"/>'s rotation (ffmpeg's transpose, hflip and vflip filters) on one plane
/// of 8- or 16-bit samples: rows reversed, and planes transposed in 16x16 (8x8) blocks of vector unpacks, with scalar
/// edges. Pure moves, so the bytes are the scalar loops' whatever the vector width.
/// </summary>
static unsafe class PlaneOrientation
{
    /// <summary>Whether <see cref="Transpose"/> has its vector blocks here (SSE2 or ARM64's AdvSimd).</summary>
    internal static bool VectorTranspose => Sse2.IsSupported || AdvSimd.Arm64.IsSupported;

    /// <summary>d[x] = s[n - 1 - x] for n samples of <paramref name="bytes"/> (1 or 2) bytes.</summary>
    internal static void FlipRow(byte* s, byte* d, int n, int bytes, bool vectors = true)
    {
        int x = 0;
        if (bytes == 1)
        {
            if (vectors && Vector128.IsHardwareAccelerated)
            {
                var reverse = Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0);
                for (; x <= n - 16; x += 16)
                    Vector128.Shuffle(Vector128.Load(s + n - 16 - x), reverse).Store(d + x);
            }

            for (; x < n; x++)
                d[x] = s[n - 1 - x];
        }
        else
        {
            ushort* s16 = (ushort*)s, d16 = (ushort*)d;
            if (vectors && Vector128.IsHardwareAccelerated)
            {
                var reverse = Vector128.Create((ushort)7, 6, 5, 4, 3, 2, 1, 0);
                for (; x <= n - 8; x += 8)
                    Vector128.Shuffle(Vector128.Load(s16 + n - 8 - x), reverse).Store(d16 + x);
            }

            for (; x < n; x++)
                d16[x] = s16[n - 1 - x];
        }
    }

    /// <summary>
    /// dst[c][r] = src[r][c] for a source of <paramref name="w"/> by <paramref name="h"/> samples of
    /// <paramref name="bytes"/> (1 or 2) bytes; the strides are in bytes and may be negative (the other turns).
    /// </summary>
    internal static void Transpose(byte* src, long ss, byte* dst, long ds, int w, int h, int bytes, bool vectors = true)
    {
        int n = !vectors || !VectorTranspose ? 0 : bytes == 1 ? 16 : 8; // the block, in samples
        int bw = n == 0 ? 0 : w / n * n, bh = n == 0 ? 0 : h / n * n;
        for (int c = 0; c < bw; c += n)
        {
            for (int r = 0; r < bh; r += n)
            {
                byte* s = src + r * ss + (long)c * bytes, d = dst + c * ds + (long)r * bytes;
                if (bytes == 1)
                    Block8(s, ss, d, ds);
                else
                    Block16(s, ss, d, ds);
            }
        }

        // The edges: the columns right of the blocks (all rows), the rows below them.
        Scalar(src, ss, dst, ds, bw, w, 0, h, bytes);
        Scalar(src, ss, dst, ds, 0, bw, bh, h, bytes);
    }

    static void Scalar(byte* src, long ss, byte* dst, long ds, int c0, int c1, int r0, int r1, int bytes)
    {
        for (int c = c0; c < c1; c++)
        {
            byte* d = dst + c * ds, s = src + (long)c * bytes;
            if (bytes == 1)
            {
                for (int r = r0; r < r1; r++)
                    d[r] = s[r * ss];
            }
            else
            {
                for (int r = r0; r < r1; r++)
                    ((ushort*)d)[r] = *(ushort*)(s + r * ss);
            }
        }
    }

    // A 16x16 byte transpose: four rounds of out[2i] = lo(in[i], in[i + 8]), out[2i + 1] = hi(in[i], in[i + 8]). Each
    // round rotates the 8 bits of (row, column) right by one, so four swap row and column.
    static void Block8(byte* s, long ss, byte* d, long ds)
    {
        Vector128<byte> a0 = Vector128.Load(s), a1 = Vector128.Load(s + ss), a2 = Vector128.Load(s + 2 * ss), a3 = Vector128.Load(s + 3 * ss),
            a4 = Vector128.Load(s + 4 * ss), a5 = Vector128.Load(s + 5 * ss), a6 = Vector128.Load(s + 6 * ss), a7 = Vector128.Load(s + 7 * ss),
            a8 = Vector128.Load(s + 8 * ss), a9 = Vector128.Load(s + 9 * ss), a10 = Vector128.Load(s + 10 * ss), a11 = Vector128.Load(s + 11 * ss),
            a12 = Vector128.Load(s + 12 * ss), a13 = Vector128.Load(s + 13 * ss), a14 = Vector128.Load(s + 14 * ss), a15 = Vector128.Load(s + 15 * ss);
        for (int round = 0; round < 4; round++)
        {
            var (b0, b1, b2, b3, b4, b5, b6, b7) = (Lo(a0, a8), Hi(a0, a8), Lo(a1, a9), Hi(a1, a9), Lo(a2, a10), Hi(a2, a10), Lo(a3, a11), Hi(a3, a11));
            (a8, a9, a10, a11, a12, a13, a14, a15) = (Lo(a4, a12), Hi(a4, a12), Lo(a5, a13), Hi(a5, a13), Lo(a6, a14), Hi(a6, a14), Lo(a7, a15), Hi(a7, a15));
            (a0, a1, a2, a3, a4, a5, a6, a7) = (b0, b1, b2, b3, b4, b5, b6, b7);
        }

        a0.Store(d);
        a1.Store(d + ds);
        a2.Store(d + 2 * ds);
        a3.Store(d + 3 * ds);
        a4.Store(d + 4 * ds);
        a5.Store(d + 5 * ds);
        a6.Store(d + 6 * ds);
        a7.Store(d + 7 * ds);
        a8.Store(d + 8 * ds);
        a9.Store(d + 9 * ds);
        a10.Store(d + 10 * ds);
        a11.Store(d + 11 * ds);
        a12.Store(d + 12 * ds);
        a13.Store(d + 13 * ds);
        a14.Store(d + 14 * ds);
        a15.Store(d + 15 * ds);
    }

    // An 8x8 transpose of 16-bit samples: three rounds of out[2i] = lo(in[i], in[i + 4]), out[2i + 1] = hi(in[i], in[i + 4]).
    static void Block16(byte* s, long ss, byte* d, long ds)
    {
        Vector128<ushort> a0 = Vector128.Load((ushort*)s), a1 = Vector128.Load((ushort*)(s + ss)), a2 = Vector128.Load((ushort*)(s + 2 * ss)),
            a3 = Vector128.Load((ushort*)(s + 3 * ss)), a4 = Vector128.Load((ushort*)(s + 4 * ss)), a5 = Vector128.Load((ushort*)(s + 5 * ss)),
            a6 = Vector128.Load((ushort*)(s + 6 * ss)), a7 = Vector128.Load((ushort*)(s + 7 * ss));
        for (int round = 0; round < 3; round++)
        {
            var (b0, b1, b2, b3) = (Lo(a0, a4), Hi(a0, a4), Lo(a1, a5), Hi(a1, a5));
            (a4, a5, a6, a7) = (Lo(a2, a6), Hi(a2, a6), Lo(a3, a7), Hi(a3, a7));
            (a0, a1, a2, a3) = (b0, b1, b2, b3);
        }

        a0.Store((ushort*)d);
        a1.Store((ushort*)(d + ds));
        a2.Store((ushort*)(d + 2 * ds));
        a3.Store((ushort*)(d + 3 * ds));
        a4.Store((ushort*)(d + 4 * ds));
        a5.Store((ushort*)(d + 5 * ds));
        a6.Store((ushort*)(d + 6 * ds));
        a7.Store((ushort*)(d + 7 * ds));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> Lo(Vector128<byte> a, Vector128<byte> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<byte> Hi(Vector128<byte> a, Vector128<byte> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<ushort> Lo(Vector128<ushort> a, Vector128<ushort> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<ushort> Hi(Vector128<ushort> a, Vector128<ushort> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);
}
