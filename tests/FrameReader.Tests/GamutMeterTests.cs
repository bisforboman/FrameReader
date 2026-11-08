using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FrameReader;

/// <summary>
/// GamutMeter's shares are an FFmpeg 8.1 geq expression's: the same arithmetic per pixel (Y and the chroma sample it is
/// sited on, the matrix's R, G and B, each against -5% and 105%), marking a pixel 0 or the depth's maximum, and
/// signalstats' YAVG of that mask, in 8 and 10 bits, 4:2:0, 4:2:2 and 4:4:4, limited and full range.
/// </summary>
public partial class GamutMeterTests
{
    static readonly string? Libs = TestLibraries.Load();

    // 1 s of grey, 2 s of a chroma ramp (its saturated corners out of gamut), 1 s of grey with a 0.5% patch out of gamut.
    const string Ramp = "between(T,0.999,2.999)";

    [Theory]
    [InlineData("yuv422p", "320x240", false)]
    [InlineData("yuv420p", "321x241", false)]
    [InlineData("yuv444p", "160x90", false)]
    [InlineData("yuv422p10le", "320x240", false)]
    [InlineData("yuv420p", "320x240", true)]
    public void IsGeqsArithmetic(string format, string size, bool full)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-gamut-").FullName;
        try
        {
            string path = Path.Combine(dir, "gamut.mkv");
            Run("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i",
                $"nullsrc=s={size}:r=25:d=4,format=yuv444p,geq=lum=128:cb='if({Ramp},16+X*224/W,128)':cr='if({Ramp},16+Y*224/H,if(gte(T,3)*lt(X,16)*lt(Y,24),240,128))',format={format}",
                "-c:v", "ffv1", "-color_range", full ? "pc" : "tv", path]);
            using var decoder = new FrameDecoder(path, new FrameDecoderOptions { Autorotate = false });
            GamutMeter? meter = null;
            while (decoder.Next())
            {
                var frame = decoder.TakeFrame();
                var picture = PictureFormat.Of(frame, decoder.TimeBase)!;
                Assert.Equal(full, picture.FullRange);
                meter ??= GamutMeter.For(picture);
                meter.Add(frame);
                frame.Dispose();
            }

            // Under 720 lines and untagged: BT.601's Kr and Kb.
            Assert.Equal((0.299, 0.114), meter!.Matrix);
            var expected = Geq(path, format, full, -0.05, 1.05, 0.299, 0.114);
            Assert.Equal(expected.Count, meter.Shares.Count);
            for (int i = 0; i < expected.Count; i++)
                Assert.Equal(expected[i], meter.Shares[i], 0.00001);
            Assert.Contains(meter.Shares, s => s > 0.01);
            Assert.Equal(0, meter.Shares[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The arithmetic at the limits: white and black in gamut, a code past 105% or below -5% not.
    [Fact]
    public void TheLimitsAreRgbs()
    {
        var meter = new GamutMeter(2, 2, ChromaSubsampling.Yuv444, 8, false, new GamutOptions { Matrix = GamutMeter.Coefficients("bt709") });

        Assert.True(meter.InGamut(235, 128, 128));
        Assert.True(meter.InGamut(16, 128, 128));
        Assert.True(meter.InGamut(16 + 229, 128, 128));
        Assert.False(meter.InGamut(16 + 230, 128, 128));
        Assert.True(meter.InGamut(16 - 10, 128, 128));
        Assert.False(meter.InGamut(16 - 11, 128, 128));

        // 100% red (BT.709: Y 63, Cb 102, Cr 240) is in gamut; Cr at its top with mid grey isn't.
        Assert.True(meter.InGamut(63, 102, 240));
        Assert.False(meter.InGamut(126, 128, 240));

        // No code is in gamut with both chroma samples at their extremes: every pixel there is outside.
        Assert.DoesNotContain(Enumerable.Range(0, 256), y => meter.InGamut(y, 16, 16));
        meter.Add(0, [128, 128, 128, 128], 2, [16, 16, 16, 16], 2, [16, 16, 16, 16], 2);
        meter.Add(1, [128, 128, 128, 128], 2, [128, 128, 128, 128], 2, [128, 128, 128, 128], 2);
        Assert.Equal([1.0, 0.0], meter.Shares);
        Assert.Equal([0L, 1L], meter.Pts);
    }

    // Every Y code against InGamut for every chroma pair at 8 bits: the table's runs are the arithmetic's, by matrix.
    [Theory]
    [InlineData("bt709", false)]
    [InlineData("bt2020nc", true)]
    public void TheTableIsTheArithmetic(string matrix, bool full)
    {
        var meter = new GamutMeter(256, 1, ChromaSubsampling.Yuv444, 8, full, new GamutOptions { Matrix = GamutMeter.Coefficients(matrix) });
        byte[] luma = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
        for (int cb = 0; cb < 256; cb += 15)
        {
            for (int cr = 0; cr < 256; cr += 15)
            {
                meter.Add(0, luma, 256, [.. Enumerable.Repeat((byte)cb, 256)], 256, [.. Enumerable.Repeat((byte)cr, 256)], 256);
                int outside = Enumerable.Range(0, 256).Count(y => !meter.InGamut(y, cb, cr));
                Assert.Equal(outside / 256.0, meter.Shares[^1]);
            }
        }
    }

    // At 10 and 12 bits (16-bit samples, the bits above the depth ignored), every Y code against InGamut for a grid of
    // chroma pairs, the table built on several threads.
    [Theory]
    [InlineData(10, false, "bt709")]
    [InlineData(12, false, "bt2020nc")]
    [InlineData(12, true, "bt709")]
    public void TheDeepTablesAreTheArithmetic(int depth, bool full, string matrix)
    {
        AssertTableIsArithmetic(new GamutMeter(1 << depth, 1, ChromaSubsampling.Yuv444, depth, full, new GamutOptions { Matrix = GamutMeter.Coefficients(matrix) }), depth, 255);
    }

    // The tables are shared by the meters made with the same depth, range, limits and matrix: a meter made after one
    // with other options gets its own, and one made again with the first's options the first's arithmetic.
    [Fact]
    public void SharedTablesFollowTheOptions()
    {
        GamutMeter Make(GamutOptions o, bool full = false) => new(256, 1, ChromaSubsampling.Yuv444, 8, full, o);
        var options = new GamutOptions[]
        {
            new(),
            new() { Low = 0, High = 1 },
            new() { Low = -0.05, High = 1.0 },
            new() { Matrix = GamutMeter.Coefficients("bt2020nc") },
            new() { Low = -0.0, High = 1 },
        };
        foreach (var o in options)
        {
            AssertTableIsArithmetic(Make(o), 8, 15);
            AssertTableIsArithmetic(Make(o, full: true), 8, 15);
        }

        foreach (var o in options)
            AssertTableIsArithmetic(Make(o), 8, 15);

        // The limits narrowed: some pixel the default counts in is out.
        var wide = Make(new GamutOptions());
        var narrow = Make(new GamutOptions { Low = 0, High = 1 });
        Assert.Contains(Enumerable.Range(0, 256), y => wide.InGamut(y, 128, 200) != narrow.InGamut(y, 128, 200));
    }

    static void AssertTableIsArithmetic(GamutMeter meter, int depth, int step)
    {
        int size = 1 << depth;
        int bytes = depth > 8 ? 2 : 1;

        // Over 8 bits, the codes with bits above the depth set: the meter masks them off.
        byte[] Samples(Func<int, int> code)
        {
            var b = new byte[size * bytes];
            for (int i = 0; i < size; i++)
            {
                if (bytes == 1)
                    b[i] = (byte)code(i);
                else
                    BitConverter.TryWriteBytes(b.AsSpan(2 * i), (ushort)(code(i) | (i % 3 == 0 ? 0xF000 & ~(size - 1) : 0)));
            }

            return b;
        }

        byte[] luma = Samples(i => i);
        for (int cb = 0; cb < size; cb += step)
        {
            for (int cr = 0; cr < size; cr += step)
            {
                meter.Add(0, luma, size * bytes, Samples(_ => cb), size * bytes, Samples(_ => cr), size * bytes);
                int outside = Enumerable.Range(0, size).Count(y => !meter.InGamut(y, cb, cr));
                Assert.Equal((double)outside / size, meter.Shares[^1]);
            }
        }
    }

    [Fact]
    public void RefusesWhatItCantMeter()
    {
        Assert.Null(GamutMeter.Coefficients("unknown"));
        Assert.Throws<ArgumentException>(() => new GamutMeter(4, 4, ChromaSubsampling.Yuv420, 16, false));
        Assert.Throws<ArgumentException>(() => new GamutMeter(4, 4, ChromaSubsampling.Yuv420, 8, false, new GamutOptions { Low = 1, High = 0 }));
        var meter = new GamutMeter(4, 4, ChromaSubsampling.Yuv420, 8, false);
        Assert.Throws<ArgumentException>(() => meter.Add(0, new byte[15], 4, new byte[4], 2, new byte[4], 2));
    }

    // geq doing GamutMeter's arithmetic per pixel, a pixel 0 or the depth's maximum, and signalstats' YAVG of it.
    static List<double> Geq(string path, string format, bool full, double low, double high, double kr, double kb)
    {
        int depth = format.Contains("10", StringComparison.Ordinal) ? 10 : 8;
        string cx = format.Contains("444", StringComparison.Ordinal) ? "X" : "floor(X/2)";
        string cy = format.Contains("420", StringComparison.Ordinal) ? "floor(Y/2)" : "Y";
        int max = (1 << depth) - 1, mid = 1 << (depth - 1), s = 1 << (depth - 8);
        string yn = full ? Invariant($"lum(X,Y)/{max}") : Invariant($"(lum(X,Y)-16*{s})/(219*{s})");
        string pb = full ? Invariant($"(cb({cx},{cy})-{mid})/{max}") : Invariant($"(cb({cx},{cy})-128*{s})/(224*{s})");
        string pr = full ? Invariant($"(cr({cx},{cy})-{mid})/{max}") : Invariant($"(cr({cx},{cy})-128*{s})/(224*{s})");
        string Outside(int i) => Invariant($"lt(ld({i}),{low})+gt(ld({i}),{high})");
        string expression = Invariant($"st(0,{yn});st(1,{pb});st(2,{pr});st(3,ld(0)+2*(1-{kr})*ld(2));st(4,ld(0)+2*(1-{kb})*ld(1));st(5,(ld(0)-{kr}*ld(3)-{kb}*ld(4))/(1-{kr}-{kb}));if({Outside(3)}+{Outside(5)}+{Outside(4)},{max},0)");
        string log = Run("ffmpeg", ["-hide_banner", "-nostats", "-nostdin", "-i", path, "-an", "-vf", $"geq=lum='{expression}':cb={mid}:cr={mid},signalstats,metadata=print", "-f", "null", "-"]);
        return [.. Average().Matches(log).Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / max)];
    }

    static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"lavfi\.signalstats\.YAVG=(-?[\d.]+)")]
    private static partial Regex Average();

    static string Run(string program, string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? program + ".exe" : program)) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, stderr);
        return stderr;
    }
}
