using System.Diagnostics;
using System.Globalization;
using FrameReader;

/// <summary>
/// FrameDecoder's rotation (ffmpeg's autorotate: transpose, hflip, vflip): its vector moves against a plain loop at
/// every small size, and its frames against ffmpeg's command line for every display rotation and flip, at odd sizes
/// and both sample sizes. The ffmpeg half needs FRAMEREADER_FFMPEG_LIBS.
/// </summary>
public class RotationTests
{
    static readonly string? Libs = TestLibraries.Load();

    // Every size up to 40 by 40 (vector blocks with every remainder, and none), both sample sizes, the four transposes
    // (signed strides, as FrameDecoder passes them) and the row flip.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public unsafe void MovesSamplesAsAPlainLoop(int bytes)
    {
        var random = new Random(bytes);
        foreach (bool vectors in new[] { true, false })
        {
            for (int w = 1; w <= 40; w++)
            {
                for (int h = 1; h <= 40; h++)
                {
                    int ss = (w * bytes) + 5, ds = (h * bytes) + 3;
                    var source = new byte[ss * h];
                    random.NextBytes(source);
                    var turned = new byte[ds * w];
                    var flipped = new byte[w * bytes];
                    fixed (byte* s = source, d = turned, f = flipped)
                    {
                        for (int dir = 0; dir < 4; dir++)
                        {
                            bool up = (dir & 1) != 0, right = (dir & 2) != 0;
                            PlaneOrientation.Transpose(up ? s + ((long)(h - 1) * ss) : s,
                                up ? -ss : ss,
                                right ? d + ((long)(w - 1) * ds) : d,
                                right ? -ds : ds,
                                w,
                                h,
                                bytes,
                                vectors);
                            for (int c = 0; c < w; c++)
                            {
                                for (int r = 0; r < h; r++)
                                {
                                    int from = ((up ? h - 1 - r : r) * ss) + (c * bytes), to = ((right ? w - 1 - c : c) * ds) + (r * bytes);
                                    Assert.True(source.AsSpan(from, bytes).SequenceEqual(turned.AsSpan(to, bytes)),
                                        $"{w}x{h}, {bytes} bytes, dir {dir}, vectors {vectors}: sample ({c}, {r})");
                                }
                            }
                        }

                        PlaneOrientation.FlipRow(s, f, w, bytes, vectors);
                        for (int x = 0; x < w; x++)
                            Assert.True(source.AsSpan((w - 1 - x) * bytes, bytes).SequenceEqual(flipped.AsSpan(x * bytes, bytes)), $"flip {w}, {bytes} bytes");
                    }
                }
            }
        }
    }

    // The display matrix's every turn and flip (transpose's four dirs, hflip, vflip, both), against ffmpeg's autorotate
    // byte for byte. Odd sizes give odd chroma planes and widths that aren't a multiple of a vector; every frame is held
    // (TakeFrame) until the end, so each one's buffer is a fresh one from the pool, not overwritten by the next.
    [Theory]
    [InlineData("yuv420p", 61, 37)]
    [InlineData("yuv420p", 35, 17)]
    [InlineData("yuv420p10le", 61, 37)]
    [InlineData("yuv444p", 47, 33)]
    [InlineData("gray16le", 29, 51)]
    public void TurnsAsFfmpeg(string format, int width, int height)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-rot-").FullName;
        try
        {
            string plain = Path.Combine(dir, "plain.mov");
            Run("-f", "lavfi", "-i", $"testsrc2=s={width}x{height}:r=25:d=0.4", "-pix_fmt", format, "-c:v", "ffv1", plain);
            foreach (int degrees in new[] { 0, 90, 180, 270 })
            {
                foreach (string flip in new[] { "", "-display_hflip", "-display_vflip" })
                {
                    if (degrees == 0 && flip == "")
                        continue;
                    string turned = Path.Combine(dir, "turned.mov"), expected = Path.Combine(dir, "expected.yuv");
                    Run([.. flip == "" ? [] : new[] { flip }, "-display_rotation", degrees.ToString(CultureInfo.InvariantCulture), "-i", plain, "-c", "copy", turned]);
                    Run("-i", turned, "-f", "rawvideo", "-pix_fmt", format, expected);
                    var frames = new List<DecodedFrame>();
                    try
                    {
                        using (var decoder = new FrameDecoder(turned, new FrameDecoderOptions()))
                        {
                            while (decoder.Next())
                                frames.Add(decoder.TakeFrame());
                        }

                        var decoded = new MemoryStream();
                        foreach (var frame in frames)
                            Write(decoded, frame, format);
                        Assert.True(File.ReadAllBytes(expected).AsSpan().SequenceEqual(decoded.ToArray()),
                            $"{format} {width}x{height} turned {degrees} {flip}: {frames.Count} frames differ from ffmpeg's");
                    }
                    finally
                    {
                        foreach (var frame in frames)
                            frame.Dispose();
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    static void Write(MemoryStream to, DecodedFrame frame, string format)
    {
        int bytes = format.EndsWith("le", StringComparison.Ordinal) ? 2 : 1, planes = format.StartsWith("gray", StringComparison.Ordinal) ? 1 : 3;
        int shift = format.StartsWith("yuv420", StringComparison.Ordinal) ? 1 : 0;
        for (int p = 0; p < planes; p++)
        {
            int w = p == 0 ? frame.Width : -((-frame.Width) >> shift), h = p == 0 ? frame.Height : -((-frame.Height) >> shift);
            var plane = frame.GetPlane(p, out int stride);
            for (int y = 0; y < h; y++)
                to.Write(plane.Slice(y * stride, w * bytes));
        }
    }

    static void Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg")) { RedirectStandardError = true };
        foreach (var a in (string[])["-v", "error", "-y", .. args])
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
    }
}
