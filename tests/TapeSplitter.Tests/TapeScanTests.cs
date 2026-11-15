using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FrameReader;
using TapeSplitter;

/// <summary>
/// The tape splitter's report against FFmpeg 8.1's own filters on a tape made with lavfi: black leader, a recording, a
/// blue "no signal" screen, a recording that freezes on its last frame, a recording, black to the end, the sound silent
/// wherever the picture is blank. Its black, frozen and silent spans print as blackdetect's, freezedetect's and
/// silencedetect's (on the samples as floats, <c>aformat=flt</c>, as <see cref="SilenceFinder"/> takes them) with
/// <c>-copyts</c>; the flat colours (no FFmpeg filter) and the gaps, cut points and recordings are what the tape was
/// made of. As a lossless capture (FFV1, PCM), as DV (the codec of DV tapes and their captures) and at 10 bits 4:2:2
/// without sound. The rules from spans to gaps (how long, the sound agreeing, glitches, which detectors, their
/// thresholds) on a second tape, each set of options' spans against the filters with the same options. Needs FRAMEREADER_FFMPEG_LIBS (an FFmpeg 8.1 shared build's folder with ffmpeg); without it these do
/// nothing.
/// </summary>
public partial class TapeScanTests
{
    static readonly string? Libs = Load();

    // Inputs 0 to 5: black 2 s, a recording 4 s, blue 3 s, a recording 4 s (frozen on its last frame for 3 s), a
    // recording 4 s (testsrc2 from 3 s, so it differs from the frozen frame), black 3 s; 6: the sound.
    const string Tape = "-f lavfi -i color=black:{0}:d=2 -f lavfi -i testsrc2={0}:d=4 -f lavfi -i color=0x1010d0:{0}:d=3 "
        + "-f lavfi -i testsrc2={0}:d=4 -f lavfi -i testsrc2={0}:d=4 -f lavfi -i color=black:{0}:d=3 "
        + "-f lavfi -i aevalsrc=0.3*sin(2*PI*440*t)*(between(t\\,2\\,6)+between(t\\,9\\,13)+between(t\\,16\\,20)):s=48000:c=stereo:d=23 "
        + "-filter_complex [3]tpad=stop_mode=clone:stop_duration=3[b];[4]setpts=PTS+3/TB,trim=start=3,setpts=PTS-STARTPTS[c];"
        + "[0][1][2][b][c][5]concat=n=6:v=1:a=0,format={1}[v] -map [v]";

    [Theory]
    [InlineData("tape.mkv", "s=320x240:r=25", "yuv420p", "-map 6:a -c:v ffv1 -c:a pcm_s16le")]
    [InlineData("tape.dv", "s=720x576:r=25", "yuv420p", "-map 6:a -c:v dvvideo -c:a pcm_s16le")]
    [InlineData("silent.mkv", "s=320x240:r=25", "yuv422p10le", "-c:v ffv1")]
    public void IsFfmpegsFilters(string name, string size, string format, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-tape-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", .. string.Format(CultureInfo.InvariantCulture, Tape, size, format).Split(' '), .. encode.Split(' '), path]);
            bool sound = encode.Contains("-c:a", StringComparison.Ordinal);
            var report = TapeScan.Run(path);

            SameAsFilters(path, report, new TapeScanOptions(), sound);
            Assert.Equal(sound, report.Spans.Any(s => s.Kind == "silence"));

            // The flat colours: black (16, 128, 128) at either end, the blue screen between the first two recordings.
            var flat = report.Spans.Where(s => s.Kind == "flat").ToList();
            Assert.Equal([(0.0, 2.0), (6.0, 9.0), (20.0, 23.0)], flat.Select(s => (s.Start, s.End!.Value)));
            Assert.All(new[] { flat[0], flat[2] }, s => Assert.Equal([16, 128, 128], s.Colour!.Select(c => Math.Round(c))));
            Assert.True(flat[1].Colour![1] > 200, $"the blue screen's Cb {flat[1].Colour![1]}");

            // The gaps, cut points and recordings: what the tape was made of. The frozen picture's gap starts at its
            // first frame, the recording's last (freezedetect's start).
            Assert.Equal(23, report.Duration);
            Assert.Equal([(0.0, 2.0), (6.0, 9.0), (12.96, 16.0), (20.0, 23.0)], report.Gaps.Select(g => (g.Start, g.End)));
            Assert.Equal(["black", "flat", "frozen"], report.Gaps[0].Kinds);
            Assert.Equal(["flat", "frozen"], report.Gaps[1].Kinds);
            Assert.Equal(["frozen"], report.Gaps[2].Kinds);
            Assert.Equal(sound ? [2, 3, 3, 3] : new double[] { 0, 0, 0, 0 }, report.Gaps.Select(g => Math.Round(g.Silence)));
            Assert.Equal([2, 6, 9, 12.96, 16, 20], report.Cuts);
            Assert.Equal([new Recording(2, 6), new Recording(9, 12.96), new Recording(16, 20)], report.Recordings);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Inputs 0 to 12, the sound 13: black 2 s; a recording 3 s; black 1 s then blue 1.6 s (neither a gap alone); a
    // recording 3 s, its last frame held 3 s with the sound going on (a still with music); a recording 3 s; black 2 s,
    // a glitch of 0.4 s, blue 3 s; a recording 3 s; black 3 s, the sound going on for its first second; a recording
    // 3 s, its last frame held 3 s under noise (frozen only to a looser freezedetect); dark grey 3 s to the end (black
    // only to a looser blackdetect). 37 s.
    const string Rules = "-f lavfi -i color=black:{0}:d=2 -f lavfi -i testsrc2={0}:d=3 -f lavfi -i color=black:{0}:d=1 -f lavfi -i color=0x1010d0:{0}:d=1.6 "
        + "-f lavfi -i testsrc2={0}:d=3 -f lavfi -i testsrc2={0}:d=3 -f lavfi -i color=black:{0}:d=2 -f lavfi -i testsrc2={0}:d=0.4 "
        + "-f lavfi -i color=0x1010d0:{0}:d=3 -f lavfi -i testsrc2={0}:d=3 -f lavfi -i color=black:{0}:d=3 -f lavfi -i testsrc2={0}:d=3 "
        + "-f lavfi -i color=0x313131:{0}:d=3 "
        + "-f lavfi -i aevalsrc=0.3*sin(2*PI*440*t)*(between(t\\,2\\,5)+between(t\\,7.6\\,16.6)+between(t\\,22\\,26.02)+between(t\\,28\\,31)):s=48000:c=stereo:d=37 "
        + "-filter_complex [4]tpad=stop_mode=clone:stop_duration=3[b];[11]tpad=stop_mode=clone:stop_duration=3,noise=alls=6:allf=t:enable='gte(t,3)'[e];"
        + "[0][1][2][3][b][5][6][7][8][9][10][e][12]concat=n=13:v=1:a=0,format=yuv420p[v] -map [v] -map 13:a -c:v ffv1 -c:a pcm_s16le";

    // The rules from spans to gaps. Every set of options' spans are the filters' with the same options; the gaps are
    // what the tape was made of (a held frame's gap starts at the recording's last frame, 0.04 s before the hold).
    [Fact]
    public void RulesFromSpansToGaps()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-tape-").FullName;
        try
        {
            string path = Path.Combine(dir, "rules.mkv");
            Ffmpeg(["-v", "error", "-y", .. string.Format(CultureInfo.InvariantCulture, Rules, "s=320x240:r=25").Split(' '), path]);

            // The defaults: the second of black and the blue after it make one gap (spans from 0.5 s, gaps from 2 s),
            // the still is one though its sound goes on, the glitch between black and blue is part of their gap, and
            // the black with sound in its first second is a gap from its first frame. The noisy still and the dark
            // grey: only the grey, as a flat colour.
            var defaults = Gaps(path, new TapeScanOptions());
            Assert.Equal([(0, 2), (5, 7.6), (10.56, 13.6), (16.6, 22), (25, 28), (34, 37)], defaults.Gaps.Select(g => (g.Start, g.End)));
            Assert.Equal(["black", "flat", "frozen"], defaults.Gaps[1].Kinds);
            Assert.Equal(["frozen"], defaults.Gaps[2].Kinds);
            Assert.Equal(["flat", "frozen"], defaults.Gaps[5].Kinds);
            Assert.Equal([2, 2.6, 0, 5.4, 1.98, 3], defaults.Gaps.Select(g => Math.Round(g.Silence, 3)));
            Assert.Equal([new Recording(2, 5), new Recording(7.6, 10.56), new Recording(13.6, 16.6), new Recording(22, 25), new Recording(28, 34)], defaults.Recordings);
            Assert.Equal([2, 5, 7.6, 10.56, 13.6, 16.6, 22, 25, 28, 34], defaults.Cuts);

            // Spans from 2 s, as the filters' own default: the black and the blue are no gap.
            var whole = Gaps(path, new TapeScanOptions { MinSpan = TimeSpan.FromSeconds(2) });
            Assert.Equal([(0, 2), (10.56, 13.6), (16.6, 22), (25, 28), (34, 37)], whole.Gaps.Select(g => (g.Start, g.End)));

            // Every stretch a recording: the glitch is one.
            var glitch = Gaps(path, new TapeScanOptions { MinRecording = TimeSpan.Zero });
            Assert.Equal([(0, 2), (5, 7.6), (10.56, 13.6), (16.6, 18.6), (19, 22), (25, 28), (34, 37)], glitch.Gaps.Select(g => (g.Start, g.End)));
            Assert.Contains(new Recording(18.6, 19), glitch.Recordings);

            // The sound must agree: the still with music is no gap, and the black's gap starts where its sound stops
            // (silencedetect's start, half a frame into the black's second second: 1.98 s, a gap within a frame of 2 s).
            var agreed = Gaps(path, new TapeScanOptions { Sound = SoundRule.Agree });
            Assert.Equal([(0, 2), (5, 7.6), (16.6, 22), (26.02, 28), (34, 37)], agreed.Gaps.Select(g => (Math.Round(g.Start, 3), Math.Round(g.End, 3))));
            Assert.All(agreed.Gaps, g => Assert.Equal(Math.Round(g.End - g.Start, 6), g.Silence));
            Assert.Contains(agreed.Spans, s => s.Kind == "silence" && s.Start == agreed.Gaps[3].Start);

            // blackdetect alone, then with a pixel threshold that takes the dark grey (and the blue screen's luma).
            var black = Gaps(path, new TapeScanOptions { Kinds = ["black"] });
            Assert.Equal([(0, 2), (16.6, 18.6), (25, 28)], black.Gaps.Select(g => (g.Start, g.End)));
            Assert.All(black.Spans, s => Assert.Contains(s.Kind, new[] { "black", "silence" }));
            var darker = Gaps(path, new TapeScanOptions { Kinds = ["black"], BlackPixel = 0.2 });
            Assert.Equal([(0, 2), (5, 7.6), (16.6, 22), (25, 28), (34, 37)], darker.Gaps.Select(g => (g.Start, g.End)));

            // freezedetect with more noise let through: the noisy still is frozen, and one gap with the grey after it.
            var noisy = Gaps(path, new TapeScanOptions { FreezeNoise = 0.02 });
            Assert.Equal(6, noisy.Gaps.Count);
            Assert.InRange(noisy.Gaps[5].Start, 30.8, 31);
            Assert.Equal(37, noisy.Gaps[5].End);

            // A louder silence level and a pic_th: the spans are the filters' still.
            Gaps(path, new TapeScanOptions { SilenceLevel = -20, BlackPicture = 0.5, MinGap = TimeSpan.FromSeconds(1), MinSpan = TimeSpan.FromSeconds(1) });

            Assert.Throws<ArgumentException>(() => TapeScan.Run(path, new TapeScanOptions { MinSpan = TimeSpan.FromSeconds(3) }));
            Assert.Throws<ArgumentException>(() => TapeScan.Run(path, new TapeScanOptions { Kinds = ["snow"] }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        static TapeReport Gaps(string path, TapeScanOptions options)
        {
            var report = TapeScan.Run(path, options);
            SameAsFilters(path, report, options, sound: true);
            return report;
        }
    }

    // Each recording written by stream copy is ffmpeg's -ss START -to END -c copy, byte for byte, and as these captures
    // are intra-only (every frame a keyframe: FFV1 with -g 1, as archives encode it; by default a keyframe every 12
    // frames), exactly the recording's frames: 4 s, 3.96 s (up to the frozen frame), 4 s.
    [Theory]
    // The DV tape's recordings go into AVI: FrameReader.Native's FFmpeg has no raw dv muxer.
    [InlineData("tape.mkv", "s=320x240:r=25", "-map 6:a -c:v ffv1 -g 1 -c:a pcm_s16le", ".mkv")]
    [InlineData("tape.dv", "s=720x576:r=25", "-map 6:a -c:v dvvideo -c:a pcm_s16le", ".avi")]
    public void WritesEachRecordingAsFfmpeg(string name, string size, string encode, string extension)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-tape-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", .. string.Format(CultureInfo.InvariantCulture, Tape, size, "yuv420p").Split(' '), .. encode.Split(' '), path]);
            var report = TapeScan.Run(path);
            var files = TapeScan.Write(path, report, Path.Combine(dir, "recordings"), extension, bitexact: true);
            Assert.Equal(report.Recordings.Count, files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                var r = report.Recordings[i];
                string expected = Path.Combine(dir, $"expected-{i}{extension}");
                Ffmpeg("-v", "error", "-y", "-ss", Text(r.Start), "-to", Text(r.End), "-i", path, "-c", "copy", "-fflags", "+bitexact", expected);
                Assert.True(File.ReadAllBytes(expected).AsSpan().SequenceEqual(File.ReadAllBytes(files[i])), $"recording {i + 1} ({r}) isn't ffmpeg's");
                string frames = Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", files[i]).Trim();
                Assert.Equal(((int)Math.Round((r.End - r.Start) * 25)).ToString(CultureInfo.InvariantCulture), frames);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        static string Text(double seconds) => (TapeScan.Microseconds(seconds) / 1e6).ToString("0.######", CultureInfo.InvariantCulture);
    }

    // The report's black, frozen and silent spans against blackdetect, freezedetect and silencedetect with the same
    // options, in one pass on the stream's own timestamps. A detector left out of the options reports nothing.
    static void SameAsFilters(string path, TapeReport report, TapeScanOptions o, bool sound)
    {
        string d = o.MinSpan.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        FormattableString video = $"blackdetect=d={d}:pix_th={o.BlackPixel}:pic_th={o.BlackPicture},freezedetect=d={d}:n={o.FreezeNoise}";
        FormattableString audio = $"aformat=sample_fmts=flt,silencedetect=d={d}:n={o.SilenceLevel}dB";
        string log = Ffmpeg(["-hide_banner", "-nostats", "-nostdin", "-copyts", "-i", path, "-vf", FormattableString.Invariant(video),
            .. sound ? ["-af", FormattableString.Invariant(audio)] : Array.Empty<string>(), "-f", "null", "-"]);
        Same("black", report, o.Kinds.Contains("black") ? [.. Black().Matches(log).Select(m => (m.Groups[1].Value, (string?)m.Groups[2].Value))] : []);
        Same("frozen", report, o.Kinds.Contains("frozen") ? Frozen(log) : []);
        Same("silence", report, [.. Silence(log).Select(s => (s.Item1, (string?)s.Item2))]);
    }

    // One detector's spans in the report against FFmpeg's printed times (null: no end printed).
    static void Same(string kind, TapeReport report, List<(string Start, string? End)> theirs)
    {
        var ours = report.Spans.Where(s => s.Kind == kind).ToList();
        Assert.True(ours.Count == theirs.Count, $"{kind}: {ours.Count} spans, FFmpeg's {theirs.Count}: {string.Join(", ", theirs)}");
        foreach (var (o, t) in ours.Zip(theirs))
        {
            Assert.True(Printed(t.Start, o.Start), $"{kind}: {o}, FFmpeg's {t}");
            Assert.True(t.End is null ? o.End is null : o.End is { } end && Printed(t.End, end), $"{kind}: {o}, FFmpeg's {t}");
        }
    }

    // freezedetect's start, and its end where it printed one (none for a picture still frozen at the end).
    static List<(string, string?)> Frozen(string log)
    {
        var spans = new List<(string, string?)>();
        foreach (Match m in Freeze().Matches(log))
        {
            if (m.Groups[1].Value == "start")
                spans.Add((m.Groups[2].Value, null));
            else if (m.Groups[1].Value == "end")
                spans[^1] = (spans[^1].Item1, m.Groups[2].Value);
        }

        return spans;
    }

    static List<(string, string)> Silence(string log) =>
        [.. SilenceStart().Matches(log).Zip(SilenceEnd().Matches(log), (s, e) => (s.Groups[1].Value, e.Groups[1].Value))];

    /// <summary>Whether <paramref name="value"/> prints as <paramref name="text"/>: within half its last digit.</summary>
    static bool Printed(string text, double value)
    {
        int dot = text.IndexOf('.', StringComparison.Ordinal);
        int decimals = dot < 0 ? 0 : text.Length - dot - 1;
        return Math.Abs(double.Parse(text, CultureInfo.InvariantCulture) - value) <= Math.Pow(10, -decimals) / 2 * (1 + 1e-9);
    }

    static string Ffmpeg(params string[] args) => Run("ffmpeg", args);

    static string Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? tool + ".exe" : tool))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        return err + stdout.Result;
    }

    static string? Load()
    {
        string? dir = Environment.GetEnvironmentVariable("FRAMEREADER_FFMPEG_LIBS");
        if (dir is not null)
            FFmpegLibraries.Load(dir);
        return dir;
    }

    [GeneratedRegex(@"black_start:(\S+) black_end:(\S+)")]
    private static partial Regex Black();

    [GeneratedRegex(@"lavfi\.freezedetect\.freeze_(start|end): (\S+)")]
    private static partial Regex Freeze();

    [GeneratedRegex(@"silence_start: (\S+)")]
    private static partial Regex SilenceStart();

    [GeneratedRegex(@"silence_end: (\S+)")]
    private static partial Regex SilenceEnd();
}
